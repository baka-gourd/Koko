using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;

using Koko.Core.Events;
using Koko.Core.Ltfs;
using Koko.Core.Scsi.Commands;

namespace Koko.Core.Tests;

public sealed class LtfsWriterTests
{
    [Test]
    public async Task Write_files_records_extents_checkpoints_refreshes_index_and_vci()
    {
        var device = new RecordingWriterDevice();
        device.Position = new LtfsTapePosition(LtfsPartition.B, 10);
        var bus = new KokoEventBus();
        var steps = new List<LtfsWriterStepKind>();
        using var subscription = bus.Subscribe<LtfsWriterStepEvent>(x => steps.Add(x.Step));
        var service = new LtfsWriterService(device, bus);
        var index = CreateIndex();
        var root = index.RootDirectory!;
        var data = Encoding.ASCII.GetBytes("abcdefghijklmnopqrst");

        var result = await service.WriteFilesAsync(new LtfsWriteRequest(
            index,
            root,
            [new LtfsWriteSource("alpha.bin", data.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            new LtfsWriterOptions(BlockSizeBytes: 8, ComputeHashes: true)));

        await Assert.That(result.BytesWritten).IsEqualTo(20L);
        await Assert.That(result.FilesWritten).IsEqualTo(1L);
        await Assert.That(result.DataPartitionIndexWritten).IsTrue();
        await Assert.That(result.IndexPartitionRefreshed).IsTrue();
        await Assert.That(result.VciWritten).IsTrue();
        await Assert.That(result.Index.Location.Partition).IsEqualTo(LtfsPartition.A);
        await Assert.That(result.Index.Location.StartBlock).IsEqualTo(4UL);
        await Assert.That(result.Index.PreviousGenerationLocation.Partition).IsEqualTo(LtfsPartition.B);
        await Assert.That(result.Index.PreviousGenerationLocation.StartBlock).IsEqualTo(14UL);

        var written = result.Index.RootDirectory!.Files.Single();
        await Assert.That(written.FileUid).IsEqualTo(2L);
        await Assert.That(written.Extents.Single().Partition).IsEqualTo(LtfsPartition.B);
        await Assert.That(written.Extents.Single().StartBlock).IsEqualTo(10L);
        await Assert.That(written.GetExtendedAttribute("ltfs.hash.blake3sum")).IsNotNull();
        await Assert.That(written.GetExtendedAttribute("ltfs.hash.xxhash128sum")).IsNotNull();
        await Assert.That(written.GetExtendedAttribute("ltfs.hash.xxhash3sum")).IsNotNull();
        await Assert.That(written.GetExtendedAttribute("ltfs.hash.sha1sum")).IsEqualTo("14A23AD70F2A5DD725575DE6C43E1CDD8B15E3E5");

        var eventTrace = string.Join("|", device.Events);
        await Assert.That(eventTrace).StartsWith("Reserve|Prevent:True|TestUnitReady|SetBlockSize:8|LocateEOD:B|ReadPosition:B:10|WriteBlock:B:10:8|WriteBlock:B:11:8|WriteBlock:B:12:4");
        await Assert.That(eventTrace).Contains("Filemarks:B:13:1|ReadPosition:B:14|WriteBlock:B:14:");
        await Assert.That(eventTrace).Contains("LocateFM:A:3|Filemarks:A:3:1|ReadPosition:A:4|WriteBlock:A:4:");
        await Assert.That(eventTrace).Contains("WriteVci:2:4:14|Prevent:False|Release");
        await Assert.That(steps.Contains(LtfsWriterStepKind.WriteFileCompleted)).IsTrue();
        await Assert.That(steps.Contains(LtfsWriterStepKind.Completed)).IsTrue();
    }

    [Test]
    public async Task Write_hashes_are_configurable()
    {
        var device = new RecordingWriterDevice { Position = new LtfsTapePosition(LtfsPartition.B, 10) };
        var data = Encoding.ASCII.GetBytes("hash me");

        var result = await new LtfsWriterService(device).WriteFilesAsync(new LtfsWriteRequest(
            CreateIndex(),
            CreateIndex().RootDirectory!,
            [new LtfsWriteSource("hash.bin", data.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            new LtfsWriterOptions(
                BlockSizeBytes: 8,
                ComputeHashes: true,
                Hashes: new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: false, XxHash128: true, XxHash64: true, Sha1: false, Md5: false))));

        var written = result.Index.RootDirectory!.Files.Single();
        await Assert.That(written.GetExtendedAttribute("ltfs.hash.xxhash128sum")).IsNotNull();
        await Assert.That(written.GetExtendedAttribute("ltfs.hash.xxhash3sum")).IsNotNull();
        await Assert.That(written.GetExtendedAttribute("ltfs.hash.blake3sum")).IsNull();
        await Assert.That(written.GetExtendedAttribute("ltfs.hash.sha512sum")).IsNull();
        await Assert.That(written.GetExtendedAttribute("ltfs.hash.sha1sum")).IsNull();
    }

    [Test]
    public async Task Write_packs_small_files_into_one_logical_block()
    {
        var device = new RecordingWriterDevice { Position = new LtfsTapePosition(LtfsPartition.B, 10) };
        var first = Encoding.ASCII.GetBytes("alpha");
        var second = Encoding.ASCII.GetBytes("beta");

        var result = await new LtfsWriterService(device).WriteFilesAsync(new LtfsWriteRequest(
            CreateIndex(),
            CreateIndex().RootDirectory!,
            [
                new LtfsWriteSource("a.txt", first.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(first, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                new LtfsWriteSource("b.txt", second.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(second, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            ],
            new LtfsWriterOptions(BlockSizeBytes: 16, WriteDataPartitionIndexOnComplete: false, RefreshIndexPartitionOnComplete: false, WriteVci: false)));

        var files = result.Index.RootDirectory!.Files.OrderBy(x => x.Name).ToArray();
        await Assert.That(files.Length).IsEqualTo(2);
        await Assert.That(files[0].Extents.Single().StartBlock).IsEqualTo(10L);
        await Assert.That(files[0].Extents.Single().ByteOffset).IsEqualTo(0L);
        await Assert.That(files[0].Extents.Single().ByteCount).IsEqualTo(5L);
        await Assert.That(files[1].Extents.Single().StartBlock).IsEqualTo(10L);
        await Assert.That(files[1].Extents.Single().ByteOffset).IsEqualTo(5L);
        await Assert.That(files[1].Extents.Single().ByteCount).IsEqualTo(4L);
        await Assert.That(device.Blocks[(LtfsPartition.B, 10)]).IsEquivalentTo(Encoding.ASCII.GetBytes("alphabeta"));
        await Assert.That(string.Join("|", device.Events)).Contains("WriteBlock:B:10:9");
    }

    [Test]
    public async Task Write_default_policies_sample_health_after_file_boundary()
    {
        var device = new RecordingWriterDevice { Position = new LtfsTapePosition(LtfsPartition.B, 10) };
        var data = Encoding.ASCII.GetBytes("abcdefghijklmnop");

        await new LtfsWriterService(device).WriteFilesAsync(new LtfsWriteRequest(
            CreateIndex(),
            CreateIndex().RootDirectory!,
            [new LtfsWriteSource("large.bin", data.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            new LtfsWriterOptions(BlockSizeBytes: 8, WriteDataPartitionIndexOnComplete: false, RefreshIndexPartitionOnComplete: false, WriteVci: false)));

        await Assert.That(string.Join("|", device.Events)).IsEqualTo("Reserve|Prevent:True|TestUnitReady|SetBlockSize:8|LocateEOD:B|ReadPosition:B:10|WriteBlock:B:10:8|WriteBlock:B:11:8|LogSense:0x02|Prevent:False|Release");
    }

    [Test]
    public async Task Write_auto_reload_checkpoints_flushes_reloads_and_relocates_data_eod_at_file_boundary()
    {
        var device = new RecordingWriterDevice { Position = new LtfsTapePosition(LtfsPartition.B, 10) };
        var data = Encoding.ASCII.GetBytes("abcdefgh");
        var second = Encoding.ASCII.GetBytes("ijklmnop");
        var healthEvents = new List<LtfsWriteHealthPolicyEvent>();
        var bus = new KokoEventBus();
        using var subscription = bus.Subscribe<LtfsWriteHealthPolicyEvent>(healthEvents.Add);

        var result = await new LtfsWriterService(device, bus).WriteFilesAsync(new LtfsWriteRequest(
            CreateIndex(),
            CreateIndex().RootDirectory!,
            [
                new LtfsWriteSource("reload-1.bin", data.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                new LtfsWriteSource("reload-2.bin", second.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(second, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            ],
            new LtfsWriterOptions(
                BlockSizeBytes: 8,
                WriteDataPartitionIndexOnComplete: false,
                RefreshIndexPartitionOnComplete: false,
                WriteVci: false,
                AutoReloadPolicy: new LtfsAutoReloadPolicyOptions(
                    Enabled: true,
                    LowSpeedMiBPerSecond: 0,
                    HighSpeedMiBPerSecond: double.MaxValue,
                    SustainedDuration: TimeSpan.Zero,
                    ReloadAfterFlushCount: 1),
                HealthSampling: new LtfsHealthSamplingOptions(
                    CustomSampler: (_, _, _) => ValueTask.FromResult<double?>(0)))));

        var eventTrace = string.Join("|", device.Events);
        await Assert.That(result.DataPartitionIndexWritten).IsTrue();
        await Assert.That(healthEvents.Select(x => x.Action).ToArray()).IsEquivalentTo([
            LtfsWriteHealthAction.Flush,
            LtfsWriteHealthAction.PendingReload,
            LtfsWriteHealthAction.Reload,
        ]);
        await Assert.That(eventTrace).Contains("WriteBlock:B:10:8|ReadPosition:B:11|LogSense:0x02|Filemarks:B:11:0|WriteBlock:B:11:8");
        await Assert.That(eventTrace).Contains("Filemarks:B:12:1|ReadPosition:B:13|WriteBlock:B:13:");
        await Assert.That(eventTrace).Contains("|Prevent:False|LoadUnload:False|LoadUnload:True|TestUnitReady|SetBlockSize:8|Prevent:True|LocateEOD:B|ReadPosition:B:");
    }

    [Test]
    public async Task Health_monitor_flushes_twice_then_reloads_on_third_successful_flush()
    {
        var device = new RecordingWriterDevice();
        var monitor = new LtfsWriteHealthMonitor(
            new LtfsAutoReloadPolicyOptions(
                Enabled: true,
                LowSpeedMiBPerSecond: 0,
                HighSpeedMiBPerSecond: double.MaxValue,
                SustainedDuration: TimeSpan.Zero,
                ReloadAfterFlushCount: 3),
            new LtfsWriteErrorRateSampler(
                device,
                new LtfsHealthSamplingOptions(CustomSampler: (_, _, _) => ValueTask.FromResult<double?>(0))));

        for (var i = 1; i <= 2; i++)
        {
            var flush = await monitor.SampleAsync("op", i * 1024 * 1024, CancellationToken.None);
            await Assert.That(flush.Action).IsEqualTo(LtfsWriteHealthAction.Flush);
            await Assert.That(monitor.RecordCapacityLossFlushSucceeded(flush)).IsNull();
        }

        var thirdFlush = await monitor.SampleAsync("op", 3 * 1024 * 1024, CancellationToken.None);
        await Assert.That(thirdFlush.Action).IsEqualTo(LtfsWriteHealthAction.Flush);
        var pending = monitor.RecordCapacityLossFlushSucceeded(thirdFlush);
        await Assert.That(pending?.Action).IsEqualTo(LtfsWriteHealthAction.PendingReload);

        var reload = monitor.TryConsumePendingReload();
        await Assert.That(reload?.Action).IsEqualTo(LtfsWriteHealthAction.Reload);
        await Assert.That(reload?.ReloadCount).IsEqualTo(1);
    }

    [Test]
    public async Task Write_large_file_health_interval_samples_before_file_completion()
    {
        var device = new RecordingWriterDevice { Position = new LtfsTapePosition(LtfsPartition.B, 10) };
        var data = Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwx");
        var sampleCount = 0;

        await new LtfsWriterService(device).WriteFilesAsync(new LtfsWriteRequest(
            CreateIndex(),
            CreateIndex().RootDirectory!,
            [new LtfsWriteSource("large.bin", data.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            new LtfsWriterOptions(
                BlockSizeBytes: 8,
                WriteDataPartitionIndexOnComplete: false,
                RefreshIndexPartitionOnComplete: false,
                WriteVci: false,
                AutoReloadPolicy: new LtfsAutoReloadPolicyOptions(Enabled: true),
                HealthSampling: new LtfsHealthSamplingOptions(
                    SampleAfterFile: false,
                    LargeFileByteInterval: 8,
                    CustomSampler: (_, _, _) =>
                    {
                        sampleCount += 1;
                        return ValueTask.FromResult<double?>(double.NegativeInfinity);
                    }))));

        await Assert.That(sampleCount).IsGreaterThanOrEqualTo(3);
        await Assert.That(string.Join("|", device.Events)).Contains("WriteBlock:B:10:8|WriteBlock:B:11:8|WriteBlock:B:12:8");
    }

    [Test]
    public async Task Write_encryption_key_is_applied_before_locate_and_after_reload()
    {
        var device = new RecordingWriterDevice { Position = new LtfsTapePosition(LtfsPartition.B, 10) };
        var data = Encoding.ASCII.GetBytes("abcdefgh");
        var second = Encoding.ASCII.GetBytes("ijklmnop");

        await new LtfsWriterService(device).WriteFilesAsync(new LtfsWriteRequest(
            CreateIndex(),
            CreateIndex().RootDirectory!,
            [
                new LtfsWriteSource("encrypted-1.bin", data.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                new LtfsWriteSource("encrypted-2.bin", second.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(second, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            ],
            new LtfsWriterOptions(
                BlockSizeBytes: 8,
                WriteDataPartitionIndexOnComplete: false,
                RefreshIndexPartitionOnComplete: false,
                WriteVci: false,
                Encryption: new LtfsEncryptionOptions(LtfsEncryptionMode.WriteKeyRequired, new StaticKeyProvider(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()), "test-key"),
                AutoReloadPolicy: new LtfsAutoReloadPolicyOptions(
                    Enabled: true,
                    LowSpeedMiBPerSecond: 0,
                    HighSpeedMiBPerSecond: double.MaxValue,
                    SustainedDuration: TimeSpan.Zero,
                    ReloadAfterFlushCount: 1),
                HealthSampling: new LtfsHealthSamplingOptions(CustomSampler: (_, _, _) => ValueTask.FromResult<double?>(0)))));

        var eventTrace = string.Join("|", device.Events);
        await Assert.That(eventTrace).StartsWith("Reserve|Prevent:True|TestUnitReady|SetEncryption:01020304|SetBlockSize:8|LocateEOD:B");
        await Assert.That(eventTrace).Contains("LoadUnload:True|TestUnitReady|SetEncryption:01020304|SetBlockSize:8|Prevent:True|LocateEOD:B");
    }

    [Test]
    public async Task Write_autosave_exports_single_tar_zstandard_archive()
    {
        var temp = Path.Combine(Path.GetTempPath(), "KokoLtfsAutosaveTests", Guid.NewGuid().ToString("N"));
        try
        {
            var device = new RecordingWriterDevice
            {
                Position = new LtfsTapePosition(LtfsPartition.B, 10),
                CartridgeMemory = Encoding.ASCII.GetBytes("cm-dump"),
            };
            var data = Encoding.ASCII.GetBytes("autosave");
            var label = new LtfsLabel { VolumeUuid = Guid.Parse("11111111-1111-1111-1111-111111111111"), BlockSize = 8 };

            await new LtfsWriterService(device).WriteFilesAsync(new LtfsWriteRequest(
                CreateIndex(),
                CreateIndex().RootDirectory!,
                [new LtfsWriteSource("autosave.bin", data.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
                new LtfsWriterOptions(
                    BlockSizeBytes: 8,
                    WriteDataPartitionIndexOnComplete: false,
                    RefreshIndexPartitionOnComplete: false,
                    WriteVci: false,
                    Autosave: new LtfsAutosaveOptions(Enabled: true, RootDirectory: temp)),
                label));

            var volumeDir = Path.Combine(temp, label.VolumeUuid.ToString("D"));
            var archives = Directory.GetFiles(volumeDir, "*.tar.zst");
            await Assert.That(archives.Length).IsEqualTo(1);
            await Assert.That(Directory.GetFiles(volumeDir, "*.schema").Length).IsEqualTo(0);
            await Assert.That(Directory.GetFiles(volumeDir, "*.label").Length).IsEqualTo(0);
            await Assert.That(Directory.GetFiles(volumeDir, "*.session.json").Length).IsEqualTo(0);
            await Assert.That(Directory.GetFiles(volumeDir, "*.manifest.json").Length).IsEqualTo(0);
            await Assert.That(Directory.GetFiles(volumeDir, "*.partial").Length).IsEqualTo(0);

            var entryNames = new List<string>();
            await using var archiveStream = File.OpenRead(archives[0]);
            await using var zstandardStream = new ZstandardStream(archiveStream, CompressionMode.Decompress, leaveOpen: false);
            using var tarReader = new TarReader(zstandardStream, leaveOpen: false);
            while (tarReader.GetNextEntry() is { } entry)
                entryNames.Add(entry.Name);

            await Assert.That(entryNames.Count(x => x.EndsWith(".schema", StringComparison.Ordinal))).IsEqualTo(1);
            await Assert.That(entryNames.Count(x => x.EndsWith(".label", StringComparison.Ordinal))).IsEqualTo(1);
            await Assert.That(entryNames.Count(x => x.EndsWith(".session.json", StringComparison.Ordinal))).IsEqualTo(1);
            await Assert.That(entryNames.Count(x => x.EndsWith(".manifest.json", StringComparison.Ordinal))).IsEqualTo(1);
            await Assert.That(entryNames.Count(x => x.EndsWith(".cm.bin", StringComparison.Ordinal))).IsEqualTo(1);
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [Test]
    public async Task Write_policy_treats_failed_write_as_committed_when_position_advanced()
    {
        var device = new RecordingWriterDevice
        {
            Position = new LtfsTapePosition(LtfsPartition.B, 10),
            FailNextWriteAfterAdvance = true,
        };
        var data = Encoding.ASCII.GetBytes("abcdefgh");

        var result = await new LtfsWriterService(device).WriteFilesAsync(new LtfsWriteRequest(
            CreateIndex(),
            CreateIndex().RootDirectory!,
            [new LtfsWriteSource("advanced.bin", data.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            new LtfsWriterOptions(BlockSizeBytes: 8, WriteDataPartitionIndexOnComplete: false, RefreshIndexPartitionOnComplete: false, WriteVci: false)));

        await Assert.That(result.FilesWritten).IsEqualTo(1L);
        await Assert.That(string.Join("|", device.Events)).Contains("WriteBlock:B:10:8|ReadPosition:B:11");
    }

    [Test]
    public async Task Discovery_loads_two_partition_index_from_legacy_layout_and_reports_dirty_append()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.A, StartBlock = 5 };
        index.PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var label = CreateLabel(index.VolumeUuid, LtfsPartition.A);
        var device = new RecordingWriterDevice
        {
            Position = new LtfsTapePosition(LtfsPartition.B, 50),
            DataEodBlock = 50,
            MamAttributes = [new LtfsVolumeCoherencyInformation(99, 99, index.VolumeUuid).ToMamAttribute()],
        };
        SetupLegacyTwoPartition(device, label, index);

        var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(writerOptions: new LtfsWriterOptions(BlockSizeBytes: 8));

        await Assert.That(result.Index.GenerationNumber).IsEqualTo(index.GenerationNumber);
        await Assert.That(result.Source).IsEqualTo(LtfsIndexDiscoverySource.LabelLayout);
        await Assert.That(result.AppendPoint.Block).IsEqualTo(50UL);
        await Assert.That(result.DirtyAppendDetected).IsTrue();
        await Assert.That(result.Graph).IsNull();
        await Assert.That(device.ReadBlockLimits).Contains(524288L);
        await Assert.That(string.Join("|", device.Events)).Contains("ReadMam:A|ReadMam:B");
        await Assert.That(string.Join("|", device.Events)).Contains("Locate:A:0|ReadBlock:A:0|LocateFM:A:1");
        await Assert.That(string.Join("|", device.Events)).Contains("ReadToFM:A:1|ReadPosition:A:1|LocateFM:A:1|ReadPosition:A:1|Locate:A:2|ReadToFM:A:2");
        await Assert.That(string.Join("|", device.Events)).Contains("SetBlockSize:8|ReadMam:A|ReadMam:B");
        await Assert.That(string.Join("|", device.Events)).Contains("ReadToFM:A:4|ReadPosition:A:4|LocateFM:A:3|ReadPosition:A:4|Locate:A:5|ReadToFM:A:5|LocateEOD:B|ReadPosition:B:50");
    }

    [Test]
    public async Task Discovery_uses_vci_index_partition_fast_path_when_valid()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.A, StartBlock = 5 };
        index.PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var label = CreateLabel(index.VolumeUuid, LtfsPartition.A);
        var device = new RecordingWriterDevice
        {
            DataEodBlock = 42,
            PartitionMamAttributes =
            {
                [LtfsPartition.A] = [new LtfsVolumeCoherencyInformation(index.GenerationNumber, 5, index.VolumeUuid).ToMamAttribute()],
            },
        };
        SetupLegacyTwoPartition(device, label, index);

        var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(),
            new LtfsWriterOptions(BlockSizeBytes: 8));

        await Assert.That(result.Source).IsEqualTo(LtfsIndexDiscoverySource.VciIndexPartition);
        await Assert.That(result.Label).IsNotNull();
        await Assert.That(result.Index.VolumeUuid).IsEqualTo(index.VolumeUuid);
        await Assert.That(string.Join("|", device.Events)).Contains("SetBlockSize:8|ReadMam:A|ReadMam:B|Locate:A:5|ReadToFM:A:5|LocateEOD:B|ReadPosition:B:42");
        await Assert.That(string.Join("|", device.Events)).DoesNotContain("LocateFM:A:3");
    }

    [Test]
    public async Task Discovery_index_partition_only_vci_does_not_probe_data_partition_eod()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.A, StartBlock = 5 };
        index.PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var label = CreateLabel(index.VolumeUuid, LtfsPartition.A);
        var device = new RecordingWriterDevice
        {
            DataEodBlock = 99,
            PartitionMamAttributes =
            {
                [LtfsPartition.A] = [new LtfsVolumeCoherencyInformation(index.GenerationNumber, 5, index.VolumeUuid).ToMamAttribute()],
            },
        };
        SetupLegacyTwoPartition(device, label, index);

        var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(IndexPartitionOnly: true),
            new LtfsWriterOptions(BlockSizeBytes: 8));

        await Assert.That(result.Source).IsEqualTo(LtfsIndexDiscoverySource.VciIndexPartition);
        await Assert.That(result.AppendPoint.Partition).IsEqualTo(LtfsPartition.B);
        await Assert.That(result.AppendPoint.Block).IsEqualTo(42UL);
        await Assert.That(result.DirtyAppendDetected).IsFalse();
        await Assert.That(result.Warnings.Any(x => x.Contains("did not probe data partition EOD", StringComparison.Ordinal))).IsTrue();
        await Assert.That(string.Join("|", device.Events)).Contains("Locate:A:5|ReadToFM:A:5");
        await Assert.That(string.Join("|", device.Events)).DoesNotContain("LocateEOD:B");
        await Assert.That(string.Join("|", device.Events)).DoesNotContain("Locate:B:");
    }

    [Test]
    public async Task Discovery_uses_vci_data_partition_fast_path_when_latest_stable_is_requested()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        index.PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.A, StartBlock = 5 };
        var label = CreateLabel(index.VolumeUuid, LtfsPartition.A);
        var device = new RecordingWriterDevice
        {
            DataEodBlock = 42,
            PartitionMamAttributes =
            {
                [LtfsPartition.B] = [new LtfsVolumeCoherencyInformation(index.GenerationNumber, 42, index.VolumeUuid).ToMamAttribute()],
            },
        };
        SetupLegacyTwoPartition(device, label, index);
        device.IndexPayloads[(LtfsPartition.B, 42)] = WriteIndex(index);

        var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(IndexPreference: LtfsDiscoveryIndexPreference.LatestStable),
            new LtfsWriterOptions(BlockSizeBytes: 8));

        await Assert.That(result.Source).IsEqualTo(LtfsIndexDiscoverySource.VciDataPartition);
        await Assert.That(result.Label).IsNotNull();
        await Assert.That(result.Index.Location.Partition).IsEqualTo(LtfsPartition.B);
        await Assert.That(string.Join("|", device.Events)).Contains("SetBlockSize:8|ReadMam:A|ReadMam:B|Locate:B:42|ReadToFM:B:42|LocateEOD:B");
        await Assert.That(string.Join("|", device.Events)).DoesNotContain("LocateFM:A:3");
    }

    [Test]
    public async Task Discovery_prefers_index_partition_vci_when_data_partition_vci_is_newer_by_default()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.A, StartBlock = 5 };
        index.PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var dataIndex = index.Clone();
        dataIndex.GenerationNumber = index.GenerationNumber + 1;
        dataIndex.Location = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        dataIndex.PreviousGenerationLocation = index.Location.Clone();
        var label = CreateLabel(index.VolumeUuid, LtfsPartition.A);
        var device = new RecordingWriterDevice
        {
            DataEodBlock = 42,
            PartitionMamAttributes =
            {
                [LtfsPartition.A] = [new LtfsVolumeCoherencyInformation(index.GenerationNumber, 5, index.VolumeUuid).ToMamAttribute()],
                [LtfsPartition.B] = [new LtfsVolumeCoherencyInformation(dataIndex.GenerationNumber, 42, dataIndex.VolumeUuid).ToMamAttribute()],
            },
        };
        SetupLegacyTwoPartition(device, label, index);
        device.IndexPayloads[(LtfsPartition.B, 42)] = WriteIndex(dataIndex);

        var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(),
            new LtfsWriterOptions(BlockSizeBytes: 8));

        await Assert.That(result.Source).IsEqualTo(LtfsIndexDiscoverySource.VciIndexPartition);
        await Assert.That(result.Index.GenerationNumber).IsEqualTo(index.GenerationNumber);
        await Assert.That(result.Warnings.Any(x => x.Contains("newer than index partition generation", StringComparison.Ordinal))).IsTrue();
        await Assert.That(string.Join("|", device.Events)).Contains("Locate:A:5|ReadToFM:A:5");
        await Assert.That(string.Join("|", device.Events)).DoesNotContain("Locate:B:42|ReadToFM:B:42");
    }

    [Test]
    public async Task Discovery_falls_back_to_index_partition_layout_when_only_data_partition_vci_exists_by_default()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.A, StartBlock = 5 };
        index.PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var dataIndex = index.Clone();
        dataIndex.GenerationNumber = index.GenerationNumber + 1;
        dataIndex.Location = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        dataIndex.PreviousGenerationLocation = index.Location.Clone();
        var label = CreateLabel(index.VolumeUuid, LtfsPartition.A);
        var device = new RecordingWriterDevice
        {
            DataEodBlock = 42,
            PartitionMamAttributes =
            {
                [LtfsPartition.B] = [new LtfsVolumeCoherencyInformation(dataIndex.GenerationNumber, 42, dataIndex.VolumeUuid).ToMamAttribute()],
            },
        };
        SetupLegacyTwoPartition(device, label, index);
        device.IndexPayloads[(LtfsPartition.B, 42)] = WriteIndex(dataIndex);

        var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(),
            new LtfsWriterOptions(BlockSizeBytes: 8));

        await Assert.That(result.Source).IsEqualTo(LtfsIndexDiscoverySource.LabelLayout);
        await Assert.That(result.Index.GenerationNumber).IsEqualTo(index.GenerationNumber);
        await Assert.That(result.Warnings.Any(x => x.Contains("non-index partition", StringComparison.Ordinal))).IsTrue();
        await Assert.That(string.Join("|", device.Events)).Contains("LocateFM:A:3");
        await Assert.That(string.Join("|", device.Events)).DoesNotContain("Locate:B:42|ReadToFM:B:42");
    }

    [Test]
    public async Task Discovery_index_partition_only_legacy_does_not_probe_data_partition_eod()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.A, StartBlock = 5 };
        index.PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var label = CreateLabel(index.VolumeUuid, LtfsPartition.A);
        var device = new RecordingWriterDevice { DataEodBlock = 99 };
        SetupLegacyTwoPartition(device, label, index);

        var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(IndexPartitionOnly: true),
            new LtfsWriterOptions(BlockSizeBytes: 8));

        await Assert.That(result.Source).IsEqualTo(LtfsIndexDiscoverySource.LabelLayout);
        await Assert.That(result.AppendPoint.Partition).IsEqualTo(LtfsPartition.B);
        await Assert.That(result.AppendPoint.Block).IsEqualTo(42UL);
        await Assert.That(result.DirtyAppendDetected).IsFalse();
        await Assert.That(result.Warnings.Any(x => x.Contains("did not probe data partition EOD", StringComparison.Ordinal))).IsTrue();
        await Assert.That(string.Join("|", device.Events)).Contains("LocateFM:A:3");
        await Assert.That(string.Join("|", device.Events)).DoesNotContain("LocateEOD:B");
        await Assert.That(string.Join("|", device.Events)).DoesNotContain("Locate:B:");
    }

    [Test]
    public async Task Discovery_falls_back_to_legacy_when_vci_points_to_invalid_index()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.A, StartBlock = 5 };
        index.PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var label = CreateLabel(index.VolumeUuid, LtfsPartition.A);
        var device = new RecordingWriterDevice
        {
            DataEodBlock = 42,
            PartitionMamAttributes =
            {
                [LtfsPartition.A] = [new LtfsVolumeCoherencyInformation(index.GenerationNumber + 1, 99, index.VolumeUuid).ToMamAttribute()],
            },
        };
        SetupLegacyTwoPartition(device, label, index);

        var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(),
            new LtfsWriterOptions(BlockSizeBytes: 8));

        await Assert.That(result.Source).IsEqualTo(LtfsIndexDiscoverySource.LabelLayout);
        await Assert.That(result.Warnings.Any(x => x.Contains("VCI candidate A99", StringComparison.Ordinal))).IsTrue();
        await Assert.That(string.Join("|", device.Events)).Contains("ReadMam:A|ReadMam:B|Locate:A:99|ReadToFM:A:99|ReadPosition:A:99|LocateFM:A:3");
    }

    [Test]
    public async Task Discovery_uses_ibm_filemark_position_without_advance()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.A, StartBlock = 5 };
        index.PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var label = CreateLabel(index.VolumeUuid, LtfsPartition.A);
        var device = new RecordingWriterDevice
        {
            DataEodBlock = 42,
            LocateFilemarkStopsAfterFilemark = true,
        };
        SetupLegacyTwoPartition(device, label, index);

        var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(),
            new LtfsWriterOptions(BlockSizeBytes: 8));

        await Assert.That(result.Source).IsEqualTo(LtfsIndexDiscoverySource.LabelLayout);
        await Assert.That(string.Join("|", device.Events)).Contains("LocateFM:A:1|ReadPosition:A:2|ReadToFM:A:2|SetBlockSize:8|ReadMam:A|ReadMam:B|LocateFM:A:3|ReadPosition:A:5|ReadToFM:A:5");
        await Assert.That(string.Join("|", device.Events)).DoesNotContain("AdvanceFM");
    }

    [Test]
    public async Task Discovery_uses_label_block_size_when_options_are_missing_or_wrong()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.A, StartBlock = 5 };
        index.PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var label = CreateLabel(index.VolumeUuid, LtfsPartition.A);
        label.BlockSize = 4096;
        var device = new RecordingWriterDevice { DataEodBlock = 42 };
        SetupLegacyTwoPartition(device, label, index);

        var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(),
            new LtfsWriterOptions(BlockSizeBytes: 123));

        await Assert.That(result.Source).IsEqualTo(LtfsIndexDiscoverySource.LabelLayout);
        await Assert.That(device.ReadBlockLimits).Contains(524288L);
        await Assert.That(string.Join("|", device.Events)).Contains("SetBlockSize:4096");
        await Assert.That(device.ReadToFilemarkLimits).Contains(4096L);
    }

    [Test]
    public async Task Discovery_applies_encryption_before_legacy_probe()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.A, StartBlock = 5 };
        index.PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var label = CreateLabel(index.VolumeUuid, LtfsPartition.A);
        var device = new RecordingWriterDevice
        {
            Position = new LtfsTapePosition(LtfsPartition.B, 50),
            DataEodBlock = 50,
        };
        SetupLegacyTwoPartition(device, label, index);

        var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            writerOptions: new LtfsWriterOptions(
                BlockSizeBytes: 8,
                Encryption: new LtfsEncryptionOptions(
                    LtfsEncryptionMode.ReadOnlyKey,
                    new StaticKeyProvider(Enumerable.Repeat((byte)0x22, 32).ToArray()),
                    "test-key")));

        await Assert.That(result.Index.VolumeUuid).IsEqualTo(index.VolumeUuid);
        await Assert.That(device.Events.First()).StartsWith("SetEncryption:22222222");
    }

    [Test]
    public async Task Discovery_options_fall_back_to_legacy_when_no_partition_vci_exists()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.A, StartBlock = 5 };
        index.PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var label = CreateLabel(index.VolumeUuid, LtfsPartition.A);

        var device = new RecordingWriterDevice
        {
            DataEodBlock = 43,
            MamAttributes = [new LtfsVolumeCoherencyInformation(99, 99, index.VolumeUuid).ToMamAttribute()],
        };
        SetupLegacyTwoPartition(device, label, index);

        var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(),
            new LtfsWriterOptions(BlockSizeBytes: 8));

        await Assert.That(result.Source).IsEqualTo(LtfsIndexDiscoverySource.LabelLayout);
        await Assert.That(string.Join("|", device.Events)).Contains("ReadMam:A|ReadMam:B");
        await Assert.That(string.Join("|", device.Events)).Contains("ReadToFM:A:4|ReadPosition:A:4|LocateFM:A:3|ReadPosition:A:4|Locate:A:5|ReadToFM:A:5");
    }

    [Test]
    public async Task Discovery_loads_single_partition_index_from_previous_eod_filemark()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.A, StartBlock = 5 };
        index.PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.A, StartBlock = 0 };
        var label = new LtfsLabel
        {
            VolumeUuid = index.VolumeUuid,
            LocationPartition = LtfsPartition.A,
            IndexPartition = LtfsPartition.A,
            DataPartition = LtfsPartition.A,
            BlockSize = 8,
        };
        var device = new RecordingWriterDevice { IndexEodBlock = 8, IndexEodFileNumber = 4 };
        device.Blocks[(LtfsPartition.A, 0)] = LtfsVol1Label.Create("KOKO01");
        device.FilemarkPayloadStarts[(LtfsPartition.A, 1)] = 2;
        device.FilemarkPayloadStarts[(LtfsPartition.A, 3)] = 5;
        device.IndexPayloads[(LtfsPartition.A, 2)] = LtfsLabelWriter.ToArray(label);
        device.IndexPayloads[(LtfsPartition.A, 5)] = WriteIndex(index);

        var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(),
            new LtfsWriterOptions(BlockSizeBytes: 8));

        await Assert.That(result.Source).IsEqualTo(LtfsIndexDiscoverySource.DataCheckpointScan);
        await Assert.That(result.AppendPoint.Block).IsEqualTo(8UL);
        await Assert.That(string.Join("|", device.Events)).Contains("LocateEOD:A|ReadPosition:A:8|LocateFM:A:3|ReadPosition:A:4|ReadToFM:A:4|ReadPosition:A:4|LocateFM:A:3|ReadPosition:A:4|Locate:A:5|ReadToFM:A:5");
    }

    [Test]
    public async Task Discovery_reads_vol1_with_full_ltfs_probe_limit_and_accepts_short_data()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.A, StartBlock = 5 };
        index.PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var label = CreateLabel(index.VolumeUuid, LtfsPartition.A);
        var device = new RecordingWriterDevice { DataEodBlock = 42 };
        SetupLegacyTwoPartition(device, label, index);

        var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(),
            new LtfsWriterOptions(BlockSizeBytes: 8));

        await Assert.That(result.Index.VolumeUuid).IsEqualTo(index.VolumeUuid);
        await Assert.That(device.ReadBlockLimits.First()).IsEqualTo(524288L);
        await Assert.That(device.Blocks[(LtfsPartition.A, 0)].Length).IsEqualTo(80);
    }

    [Test]
    public async Task Discovery_accepts_vol1_when_drive_returns_more_than_80_bytes()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.A, StartBlock = 5 };
        index.PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var label = CreateLabel(index.VolumeUuid, LtfsPartition.A);
        var device = new RecordingWriterDevice { DataEodBlock = 42 };
        SetupLegacyTwoPartition(device, label, index);
        device.Blocks[(LtfsPartition.A, 0)] = [.. device.Blocks[(LtfsPartition.A, 0)], 0, 1, 2, 3];

        var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(),
            new LtfsWriterOptions(BlockSizeBytes: 8));

        await Assert.That(result.Index.VolumeUuid).IsEqualTo(index.VolumeUuid);
    }

    [Test]
    public async Task Discovery_rejects_vol1_when_required_content_is_missing()
    {
        var index = CreateIndex();
        var label = CreateLabel(index.VolumeUuid, LtfsPartition.A);
        var device = new RecordingWriterDevice();
        SetupLegacyTwoPartition(device, label, index);
        device.Blocks[(LtfsPartition.A, 0)] = "VOL1S00007L"u8.ToArray();

        await Assert.That(async () => await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(),
            new LtfsWriterOptions(BlockSizeBytes: 8))).ThrowsException();

        await Assert.That(string.Join("|", device.Events)).DoesNotContain("LocateFM:A:1");
    }

    [Test]
    public async Task Discovery_fails_immediately_when_vol1_is_invalid()
    {
        var index = CreateIndex();
        var label = CreateLabel(index.VolumeUuid, LtfsPartition.A);
        var device = new RecordingWriterDevice();
        SetupLegacyTwoPartition(device, label, index);
        device.Blocks[(LtfsPartition.A, 0)] = Encoding.ASCII.GetBytes("not-vol1");

        await Assert.That(async () => await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(),
            new LtfsWriterOptions(BlockSizeBytes: 8))).ThrowsException();

        await Assert.That(string.Join("|", device.Events)).DoesNotContain("LocateFM:A:1");
    }

    [Test]
    public async Task Discovery_fails_like_legacy_when_index_partition_candidate_is_invalid()
    {
        var index = CreateIndex();
        var label = CreateLabel(index.VolumeUuid, LtfsPartition.A);
        var device = new RecordingWriterDevice
        {
            DataEodBlock = 44,
            MamAttributes = [new LtfsVolumeCoherencyInformation(index.GenerationNumber, 42, index.VolumeUuid).ToMamAttribute()],
        };
        SetupLegacyTwoPartition(device, label, index);
        device.IndexPayloads[(LtfsPartition.A, 5)] = Encoding.UTF8.GetBytes("<ltfsindex>");

        await Assert.That(async () => await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(),
            new LtfsWriterOptions(BlockSizeBytes: 8))).ThrowsException();

        await Assert.That(string.Join("|", device.Events)).Contains("ReadMam:A|ReadMam:B");
    }

    [Test]
    public async Task Append_validation_warns_dirty_append_and_writes_from_stable_checkpoint()
    {
        var index = CreateIndex();
        var device = new RecordingWriterDevice { Position = new LtfsTapePosition(LtfsPartition.B, 20), DataEodBlock = 20 };
        device.IndexPayloads[(LtfsPartition.B, 5)] = WriteIndex(index);
        var discovery = new LtfsVolumeDiscoveryResult(index, null, new LtfsTapePosition(LtfsPartition.B, 10), LtfsIndexDiscoverySource.VciDataPartition, DirtyAppendDetected: true, Worm: false, WriteProtected: false, []);
        var events = new List<LtfsWriterStepEvent>();
        var bus = new KokoEventBus();
        using var subscription = bus.Subscribe<LtfsWriterStepEvent>(events.Add);

        var result = await new LtfsWriterService(device, bus).WriteFilesAsync(new LtfsWriteRequest(
            index,
            index.RootDirectory!,
            [new LtfsWriteSource("dirty.bin", 1, _ => ValueTask.FromResult<Stream>(new MemoryStream([1], writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            new LtfsWriterOptions(
                BlockSizeBytes: 8,
                AppendValidation: new LtfsAppendValidationOptions(Enabled: true),
                Discovery: discovery,
                WriteDataPartitionIndexOnComplete: false,
                RefreshIndexPartitionOnComplete: false,
                WriteVci: false)));

        await Assert.That(result.FilesWritten).IsEqualTo(1L);
        await Assert.That(events.Any(x => x.Step == LtfsWriterStepKind.Warning && x.Message.Contains("unindexed data", StringComparison.Ordinal))).IsTrue();
        await Assert.That(string.Join("|", device.Events)).Contains("LocateEOD:B|ReadPosition:B:20|Locate:B:5|ReadToFM:B:5");
    }

    [Test]
    public async Task Eom_mid_large_file_returns_remaining_manifest_without_committing_file()
    {
        var device = new RecordingWriterDevice
        {
            Position = new LtfsTapePosition(LtfsPartition.B, 10),
            ThrowVolumeOverflowOnWriteNumber = 2,
        };
        var data = Encoding.ASCII.GetBytes("abcdefghijklmnop");
        var index = CreateIndex();

        var result = await new LtfsWriterService(device).WriteFilesAsync(new LtfsWriteRequest(
            index,
            index.RootDirectory!,
            [new LtfsWriteSource("too-large.bin", data.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            new LtfsWriterOptions(BlockSizeBytes: 8, WriteDataPartitionIndexOnComplete: false, RefreshIndexPartitionOnComplete: false, WriteVci: false)));

        await Assert.That(result.CompletionKind).IsEqualTo(LtfsWriteCompletionKind.StoppedAtEndOfMedium);
        await Assert.That(result.FilesWritten).IsEqualTo(0L);
        await Assert.That(result.Index.RootDirectory!.Files.Count).IsEqualTo(0);
        await Assert.That(result.RemainingManifest).IsNotNull();
        await Assert.That(result.RemainingManifest!.RemainingFiles.Single().Name).IsEqualTo("too-large.bin");
    }

    [Test]
    public async Task Tape_command_queue_soft_cancel_stops_after_current_command()
    {
        var control = new LtfsTapeSessionControl();
        var executed = new List<string>();
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.WriteDataBlock,
            _ =>
            {
                executed.Add("first");
                control.RequestCancel(LtfsCancelMode.SoftAfterBlock);
                return ValueTask.CompletedTask;
            },
            LtfsTapeCommandPriority.Data,
            LtfsTapeBarrierKind.None));
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.WriteDataBlock,
            _ =>
            {
                executed.Add("second");
                return ValueTask.CompletedTask;
            },
            LtfsTapeCommandPriority.Data,
            LtfsTapeBarrierKind.None));

        var executor = new LtfsTapeCommandExecutor();
        var results = await executor.ExecuteAsync(queue, control);

        await Assert.That(executed).IsEquivalentTo(["first"]);
        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(executor.State).IsEqualTo(LtfsTapeCommandExecutorState.Faulted);
    }

    [Test]
    public async Task Tape_command_queue_prioritizes_control_and_coalesces_telemetry()
    {
        var executed = new List<string>();
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(LtfsTapeCommandKind.WriteDataBlock, _ => { executed.Add("data"); return ValueTask.CompletedTask; }, LtfsTapeCommandPriority.Data, LtfsTapeBarrierKind.None));
        queue.Enqueue(new LtfsTapeCommand(LtfsTapeCommandKind.ReadPosition, _ => { executed.Add("telemetry-old"); return ValueTask.CompletedTask; }, LtfsTapeCommandPriority.Telemetry, LtfsTapeBarrierKind.None, CorrelationId: "position"));
        queue.Enqueue(new LtfsTapeCommand(LtfsTapeCommandKind.ReadPosition, _ => { executed.Add("telemetry-new"); return ValueTask.CompletedTask; }, LtfsTapeCommandPriority.Telemetry, LtfsTapeBarrierKind.None, CorrelationId: "position"));
        queue.Enqueue(new LtfsTapeCommand(LtfsTapeCommandKind.Flush, _ => { executed.Add("control"); return ValueTask.CompletedTask; }, LtfsTapeCommandPriority.Control, LtfsTapeBarrierKind.HardBarrier));

        await new LtfsTapeCommandExecutor().ExecuteAsync(queue);

        await Assert.That(executed).IsEquivalentTo(["control", "data", "telemetry-new"]);
    }

    [Test]
    public async Task Tape_command_executor_tracks_position_and_reconciles_after_failure()
    {
        var queue = new LtfsTapeCommandQueue();
        var realPosition = new LtfsTapePosition(LtfsPartition.B, 11);
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.WriteDataBlock,
            _ => throw new InvalidOperationException("position advanced"),
            LtfsTapeCommandPriority.Data,
            LtfsTapeBarrierKind.None,
            ExpectedStartPosition: new LtfsTapePosition(LtfsPartition.B, 10),
            ExpectedEndPosition: new LtfsTapePosition(LtfsPartition.B, 11),
            ReadPositionAsync: _ => ValueTask.FromResult(realPosition)));

        var executor = new LtfsTapeCommandExecutor();
        executor.SetExpectedPosition(new LtfsTapePosition(LtfsPartition.B, 10));

        await Assert.That(async () => await executor.ExecuteAsync(queue)).ThrowsException();
        await Assert.That(executor.State).IsEqualTo(LtfsTapeCommandExecutorState.Faulted);
        await Assert.That(executor.ExpectedPosition).IsEqualTo(realPosition);
    }

    [Test]
    public async Task Tape_command_executor_applies_command_timeout()
    {
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.ReadPosition,
            async ct => await Task.Delay(TimeSpan.FromSeconds(5), ct),
            LtfsTapeCommandPriority.Telemetry,
            LtfsTapeBarrierKind.None,
            Timeout: TimeSpan.FromMilliseconds(10)));

        await Assert.That(async () => await new LtfsTapeCommandExecutor().ExecuteAsync(queue)).ThrowsException();
    }

    [Test]
    public async Task Tape_command_queue_coalesces_adjacent_data_blocks_into_run()
    {
        var executed = new List<string>();
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.WriteDataBlock,
            _ =>
            {
                executed.Add("first");
                return ValueTask.CompletedTask;
            },
            LtfsTapeCommandPriority.Data,
            LtfsTapeBarrierKind.None,
            ExpectedStartPosition: new LtfsTapePosition(LtfsPartition.B, 10),
            ExpectedEndPosition: new LtfsTapePosition(LtfsPartition.B, 11),
            CanCoalesce: true));
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.WriteDataBlock,
            _ =>
            {
                executed.Add("second");
                return ValueTask.CompletedTask;
            },
            LtfsTapeCommandPriority.Data,
            LtfsTapeBarrierKind.None,
            ExpectedStartPosition: new LtfsTapePosition(LtfsPartition.B, 11),
            ExpectedEndPosition: new LtfsTapePosition(LtfsPartition.B, 12),
            CanCoalesce: true));

        await Assert.That(queue.Count).IsEqualTo(1);
        var executor = new LtfsTapeCommandExecutor();
        executor.SetExpectedPosition(new LtfsTapePosition(LtfsPartition.B, 10));
        var results = await executor.ExecuteAsync(queue);

        await Assert.That(results.Single().Command.Kind).IsEqualTo(LtfsTapeCommandKind.WriteDataRun);
        await Assert.That(results.Single().Command.LogicalBlockCount).IsEqualTo(2);
        await Assert.That(string.Join("|", executed)).IsEqualTo("first|second");
        await Assert.That(executor.ExpectedPosition).IsEqualTo(new LtfsTapePosition(LtfsPartition.B, 12));
    }

    [Test]
    public async Task Tape_command_queue_does_not_coalesce_across_checkpoint_boundary()
    {
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.WriteDataBlock,
            _ => ValueTask.CompletedTask,
            LtfsTapeCommandPriority.Data,
            LtfsTapeBarrierKind.None,
            CanCoalesce: true));
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.WriteDataBlock,
            _ => ValueTask.CompletedTask,
            LtfsTapeCommandPriority.Data,
            LtfsTapeBarrierKind.HardBarrier,
            CanCoalesce: true));

        await Assert.That(queue.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Tape_command_executor_blocks_data_when_position_is_uncertain()
    {
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.WriteDataBlock,
            _ => ValueTask.CompletedTask,
            LtfsTapeCommandPriority.Data,
            LtfsTapeBarrierKind.None));

        var executor = new LtfsTapeCommandExecutor();
        executor.MarkBuffered("previous command may have moved tape");

        await Assert.That(async () => await executor.ExecuteAsync(queue)).ThrowsException();
        await Assert.That(executor.State).IsEqualTo(LtfsTapeCommandExecutorState.Faulted);
        await Assert.That(executor.PositionUncertain).IsTrue();
    }

    [Test]
    public async Task Capacity_policy_stops_before_next_file_and_returns_remaining_manifest()
    {
        var device = new RecordingWriterDevice
        {
            Position = new LtfsTapePosition(LtfsPartition.B, 10),
            LogSenseResponse = BuildCapacityLogSenseResponse(12),
        };
        var index = CreateIndex();

        var result = await new LtfsWriterService(device).WriteFilesAsync(new LtfsWriteRequest(
            index,
            index.RootDirectory!,
            [
                new LtfsWriteSource("first.bin", 4, _ => ValueTask.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("abcd"), writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                new LtfsWriteSource("second.bin", 16, _ => ValueTask.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("abcdefghijklmnop"), writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            ],
            new LtfsWriterOptions(
                BlockSizeBytes: 8,
                SmallFileThresholdBytes: 1,
                CapacityPolicy: new LtfsCapacityPolicyOptions(Enabled: true, SafetyReserveBytes: 8),
                RefreshIndexPartitionOnComplete: false,
                WriteVci: false)));

        await Assert.That(result.CompletionKind).IsEqualTo(LtfsWriteCompletionKind.StoppedAtEndOfMedium);
        await Assert.That(result.FilesWritten).IsEqualTo(1L);
        await Assert.That(result.RemainingManifest!.RemainingFiles.Single().Name).IsEqualTo("second.bin");
    }

    [Test]
    public async Task Worm_refresh_index_partition_appends_at_index_partition_eod()
    {
        var device = new RecordingWriterDevice
        {
            Position = new LtfsTapePosition(LtfsPartition.B, 10),
            IndexEodBlock = 20,
        };
        var index = CreateIndex();
        index.VolumeLockState = LtfsVolumeLockState.PermLocked;
        var data = Encoding.ASCII.GetBytes("abcd");

        await new LtfsWriterService(device).WriteFilesAsync(new LtfsWriteRequest(
            index,
            index.RootDirectory!,
            [new LtfsWriteSource("worm.bin", data.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            new LtfsWriterOptions(BlockSizeBytes: 8, Discovery: new LtfsVolumeDiscoveryResult(index, null, new LtfsTapePosition(LtfsPartition.B, 10), LtfsIndexDiscoverySource.VciDataPartition, false, Worm: true, WriteProtected: false, []))));

        await Assert.That(string.Join("|", device.Events)).Contains("LocateEOD:A|ReadPosition:A:20|Filemarks:A:20:1");
    }

    [Test]
    public async Task Writer_soft_cancel_at_file_boundary_checkpoints_and_returns_remaining_manifest()
    {
        var device = new RecordingWriterDevice { Position = new LtfsTapePosition(LtfsPartition.B, 10) };
        var control = new LtfsTapeSessionControl();
        var bus = new KokoEventBus();
        using var subscription = bus.Subscribe<LtfsWriterStepEvent>(x =>
        {
            if (x.Step == LtfsWriterStepKind.WriteFileCompleted)
                control.RequestCancel(LtfsCancelMode.SoftAfterFile);
        });
        var index = CreateIndex();

        var result = await new LtfsWriterService(device, bus).WriteFilesAsync(new LtfsWriteRequest(
            index,
            index.RootDirectory!,
            [
                new LtfsWriteSource("first.bin", 4, _ => ValueTask.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("abcd"), writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                new LtfsWriteSource("second.bin", 4, _ => ValueTask.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("efgh"), writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            ],
            new LtfsWriterOptions(BlockSizeBytes: 8, SmallFileThresholdBytes: 1, RefreshIndexPartitionOnComplete: false, WriteVci: false, TapeControl: control)));

        await Assert.That(result.CompletionKind).IsEqualTo(LtfsWriteCompletionKind.SoftCanceled);
        await Assert.That(result.FilesWritten).IsEqualTo(1L);
        await Assert.That(result.DataPartitionIndexWritten).IsTrue();
        await Assert.That(result.RemainingManifest!.RemainingFiles.Single().Name).IsEqualTo("second.bin");
    }

    [Test]
    public async Task Throttle_limiter_delays_when_window_limit_would_be_exceeded()
    {
        var limiter = new LtfsSlidingThroughputLimiter(new LtfsThrottlePolicyOptions(
            Enabled: true,
            LimitMiBPerSecond: 0.00002,
            WindowDuration: TimeSpan.FromMilliseconds(50),
            DelayGranularity: TimeSpan.FromMilliseconds(5)));

        await limiter.DelayBeforeWriteAsync(1);
        await limiter.DelayBeforeWriteAsync(1);

        var stopwatch = Stopwatch.StartNew();
        await limiter.DelayBeforeWriteAsync(1);
        stopwatch.Stop();

        await Assert.That(stopwatch.Elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(20));
    }

    [Test]
    public async Task Source_manifest_maps_directory_input_under_its_own_name_with_glob_filter()
    {
        var temp = Path.Combine(Path.GetTempPath(), "KokoLtfsManifestTests", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temp, "aaa", "bbb");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "c.txt"), "c");
            await File.WriteAllTextAsync(Path.Combine(root, "skip.tmp"), "tmp");
            await File.WriteAllTextAsync(Path.Combine(root, "sub", "d.txt"), "d");

            var manifest = LtfsSourceManifestBuilder.Build(new LtfsSourceManifestRequest(
                [root],
                "**/*.txt"));

            var files = manifest.Files.Select(x => x.DestinationPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            var directories = manifest.Directories.Select(x => x.DestinationPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

            await Assert.That(files).IsEquivalentTo(["bbb/c.txt", "bbb/sub/d.txt"]);
            await Assert.That(directories).Contains("bbb");
            await Assert.That(directories).Contains("bbb/sub");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [Test]
    public async Task Rollback_reads_previous_generation_index()
    {
        var previous = CreateIndex();
        previous.GenerationNumber = 7;
        previous.Location = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var current = previous.Clone();
        current.GenerationNumber = 8;
        current.Location = new LtfsLocation { Partition = LtfsPartition.A, StartBlock = 4 };
        current.PreviousGenerationLocation = previous.Location.Clone();

        var device = new RecordingWriterDevice();
        device.IndexPayloads[(LtfsPartition.B, 42)] = WriteIndex(previous);
        var result = await new LtfsWriterService(device).RollbackAsync(new LtfsRollbackRequest(current, new LtfsWriterOptions(BlockSizeBytes: 8)));

        await Assert.That(result.Index.GenerationNumber).IsEqualTo(7UL);
        await Assert.That(result.RolledBackFrom.Partition).IsEqualTo(LtfsPartition.A);
        await Assert.That(result.RolledBackTo.StartBlock).IsEqualTo(42UL);
        await Assert.That(string.Join("|", device.Events)).IsEqualTo("Reserve|Prevent:True|TestUnitReady|SetBlockSize:8|Locate:B:42|ReadToFM:B:42|Prevent:False|Release");
    }

    [Test]
    public async Task Extract_uses_memory_cache_limit_and_writes_file_data()
    {
        var temp = Path.Combine(Path.GetTempPath(), "KokoLtfsWriterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var device = new RecordingWriterDevice();
            device.Blocks[(LtfsPartition.B, 10)] = Encoding.ASCII.GetBytes("abcd");
            device.Blocks[(LtfsPartition.B, 11)] = Encoding.ASCII.GetBytes("efgh");
            var file = new LtfsFile { Name = "out.bin", FileUid = 2, Length = 8 };
            file.Extents.Add(new LtfsExtent { Partition = LtfsPartition.B, StartBlock = 10, ByteOffset = 0, ByteCount = 8, FileOffset = 0 });
            var destination = Path.Combine(temp, "out.bin");

            var result = await new LtfsWriterService(device).ExtractAsync(new LtfsExtractRequest(
                [new LtfsReadTarget(file, destination, LtfsReadOperation.ExtractOnly)],
                new LtfsWriterOptions(BlockSizeBytes: 4, MemoryCacheLimitBytes: LtfsWriterOptions.MinimumMemoryCacheLimitBytes)));

            await Assert.That(result.BytesRead).IsEqualTo(8L);
            await Assert.That(result.Plan.MemorySpoolLimitBytes).IsEqualTo(LtfsWriterOptions.MinimumMemoryCacheLimitBytes);
            await Assert.That(await File.ReadAllTextAsync(destination)).IsEqualTo("abcdefgh");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [Test]
    public async Task Verify_requires_all_enabled_present_hashes_to_match()
    {
        var data = Encoding.ASCII.GetBytes("verify-data");
        var device = new RecordingWriterDevice();
        device.Blocks[(LtfsPartition.B, 10)] = data;
        var file = new LtfsFile { Name = "verify.bin", FileUid = 2, Length = data.Length };
        file.Extents.Add(new LtfsExtent { Partition = LtfsPartition.B, StartBlock = 10, ByteOffset = 0, ByteCount = data.Length, FileOffset = 0 });
        file.SetExtendedAttribute("ltfs.hash.blake3sum", ComputeHash(data, LtfsHashAlgorithmKind.Blake3));

        var result = await new LtfsWriterService(device).ExtractAsync(new LtfsExtractRequest(
            [new LtfsReadTarget(file, string.Empty, LtfsReadOperation.VerifyOnly)],
            new LtfsWriterOptions(BlockSizeBytes: data.Length, MemoryCacheLimitBytes: LtfsWriterOptions.MinimumMemoryCacheLimitBytes)));

        await Assert.That(result.BytesRead).IsEqualTo(data.Length);
        await Assert.That(result.FileResults!.Single().VerificationStatus).IsEqualTo(LtfsExtractVerificationStatus.Verified);
    }

    [Test]
    public async Task Verify_can_limit_enabled_hashes_to_xxhash128()
    {
        var data = Encoding.ASCII.GetBytes("verify-xxhash");
        var device = new RecordingWriterDevice();
        device.Blocks[(LtfsPartition.B, 10)] = data;
        var file = new LtfsFile { Name = "verifyxx.bin", FileUid = 2, Length = data.Length };
        file.Extents.Add(new LtfsExtent { Partition = LtfsPartition.B, StartBlock = 10, ByteOffset = 0, ByteCount = data.Length, FileOffset = 0 });
        file.SetExtendedAttribute("ltfs.hash.xxhash3sum", new string('0', 16));
        file.SetExtendedAttribute("ltfs.hash.sha1sum", new string('0', 40));
        file.SetExtendedAttribute("ltfs.hash.md5sum", new string('0', 32));
        file.SetExtendedAttribute("ltfs.hash.xxhash128sum", ComputeHash(data, LtfsHashAlgorithmKind.XxHash128));

        var result = await new LtfsWriterService(device).ExtractAsync(new LtfsExtractRequest(
            [new LtfsReadTarget(file, string.Empty, LtfsReadOperation.VerifyOnly)],
            new LtfsWriterOptions(
                BlockSizeBytes: data.Length,
                MemoryCacheLimitBytes: LtfsWriterOptions.MinimumMemoryCacheLimitBytes,
                Hashes: new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: false, XxHash128: true, XxHash64: false, Sha1: false, Md5: false))));

        await Assert.That(result.BytesRead).IsEqualTo(data.Length);
    }

    [Test]
    public async Task Write_crc32_hash_when_enabled()
    {
        var device = new RecordingWriterDevice { Position = new LtfsTapePosition(LtfsPartition.B, 10) };
        var data = Encoding.ASCII.GetBytes("crc32-data");

        var result = await new LtfsWriterService(device).WriteFilesAsync(new LtfsWriteRequest(
            CreateIndex(),
            CreateIndex().RootDirectory!,
            [new LtfsWriteSource("crc.bin", data.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            new LtfsWriterOptions(
                BlockSizeBytes: 16,
                ComputeHashes: true,
                Hashes: new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: false, XxHash128: false, XxHash64: false, Sha1: false, Md5: false, Crc32: true),
                WriteDataPartitionIndexOnComplete: false,
                RefreshIndexPartitionOnComplete: false,
                WriteVci: false)));

        var written = result.Index.RootDirectory!.Files.Single();
        await Assert.That(written.GetExtendedAttribute("ltfs.hash.crc32sum")).IsEqualTo(ComputeHash(data, LtfsHashAlgorithmKind.Crc32));
    }

    [Test]
    public async Task Hash_update_only_populates_enabled_hash_and_writes_checkpoint()
    {
        var data = Encoding.ASCII.GetBytes("hash-update");
        var index = CreateIndex();
        var file = new LtfsFile { Name = "existing.bin", FileUid = 2, Length = data.Length };
        file.Extents.Add(new LtfsExtent { Partition = LtfsPartition.B, StartBlock = 10, ByteOffset = 0, ByteCount = data.Length, FileOffset = 0 });
        index.RootDirectory!.Files.Add(file);
        index.HighestFileUid = 2;
        var device = new RecordingWriterDevice { DataEodBlock = 100 };
        device.Blocks[(LtfsPartition.B, 10)] = data;

        var result = await new LtfsWriterService(device).RunHashMaintenanceAsync(new LtfsHashMaintenanceRequest(
            index,
            [new LtfsReadTarget(file, string.Empty, LtfsReadOperation.VerifyOnly)],
            LtfsHashMaintenanceMode.UpdateOnly,
            new LtfsWriterOptions(
                BlockSizeBytes: data.Length,
                MemoryCacheLimitBytes: LtfsWriterOptions.MinimumMemoryCacheLimitBytes,
                Hashes: new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: true, XxHash128: false, XxHash64: false, Sha1: false, Md5: false, Crc32: false),
                RefreshIndexPartitionOnComplete: false,
                WriteVci: false)));

        var updated = result.Index.RootDirectory!.Files.Single();
        await Assert.That(updated.GetExtendedAttribute("ltfs.hash.sha256sum")).IsEqualTo(ComputeHash(data, LtfsHashAlgorithmKind.Sha256));
        await Assert.That(result.DataPartitionIndexWritten).IsTrue();
        await Assert.That(result.FileResults.Single().UpdateStatus).IsEqualTo(LtfsHashUpdateStatus.Updated);
        await Assert.That(string.Join("|", device.Events)).Contains("LocateEOD:B|ReadPosition:B:100|Filemarks:B:100:1|ReadPosition:B:101|WriteBlock:B:101:");
    }

    [Test]
    public async Task Hash_update_only_mismatch_does_not_commit_index()
    {
        var data = Encoding.ASCII.GetBytes("hash-mismatch");
        var index = CreateIndex();
        var file = new LtfsFile { Name = "existing.bin", FileUid = 2, Length = data.Length };
        file.Extents.Add(new LtfsExtent { Partition = LtfsPartition.B, StartBlock = 10, ByteOffset = 0, ByteCount = data.Length, FileOffset = 0 });
        file.SetExtendedAttribute("ltfs.hash.sha256sum", new string('0', 64));
        index.RootDirectory!.Files.Add(file);
        index.HighestFileUid = 2;
        var device = new RecordingWriterDevice { DataEodBlock = 100 };
        device.Blocks[(LtfsPartition.B, 10)] = data;

        await Assert.That(async () => await new LtfsWriterService(device).RunHashMaintenanceAsync(new LtfsHashMaintenanceRequest(
            index,
            [new LtfsReadTarget(file, string.Empty, LtfsReadOperation.VerifyOnly)],
            LtfsHashMaintenanceMode.UpdateOnly,
            new LtfsWriterOptions(
                BlockSizeBytes: data.Length,
                MemoryCacheLimitBytes: LtfsWriterOptions.MinimumMemoryCacheLimitBytes,
                Hashes: new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: true, XxHash128: false, XxHash64: false, Sha1: false, Md5: false, Crc32: false),
                RefreshIndexPartitionOnComplete: false,
                WriteVci: false)))).ThrowsException();

        await Assert.That(string.Join("|", device.Events)).DoesNotContain("ltfsindex");
    }

    [Test]
    public async Task Hash_update_only_hashes_empty_file_without_reading_data_block()
    {
        var index = CreateIndex();
        var file = new LtfsFile { Name = "empty.bin", FileUid = 2, Length = 0 };
        index.RootDirectory!.Files.Add(file);
        index.HighestFileUid = 2;
        var device = new RecordingWriterDevice { DataEodBlock = 100 };

        var result = await new LtfsWriterService(device).RunHashMaintenanceAsync(new LtfsHashMaintenanceRequest(
            index,
            [new LtfsReadTarget(file, string.Empty, LtfsReadOperation.VerifyOnly)],
            LtfsHashMaintenanceMode.UpdateOnly,
            new LtfsWriterOptions(
                BlockSizeBytes: 8,
                MemoryCacheLimitBytes: LtfsWriterOptions.MinimumMemoryCacheLimitBytes,
                Hashes: new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: false, XxHash128: false, XxHash64: false, Sha1: false, Md5: false, Crc32: true),
                RefreshIndexPartitionOnComplete: false,
                WriteVci: false)));

        await Assert.That(result.Index.RootDirectory!.Files.Single().GetExtendedAttribute("ltfs.hash.crc32sum")).IsEqualTo(ComputeHash([], LtfsHashAlgorithmKind.Crc32));
        await Assert.That(result.Plan.ReadCommandCount).IsEqualTo(0L);
        await Assert.That(string.Join("|", device.Events)).DoesNotContain("ReadBlock:");
    }

    [Test]
    public async Task Extract_rename_with_suffix_uses_available_destination()
    {
        var temp = Path.Combine(Path.GetTempPath(), "KokoLtfsWriterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var data = Encoding.ASCII.GetBytes("rename");
            var device = new RecordingWriterDevice();
            device.Blocks[(LtfsPartition.B, 10)] = data;
            var file = new LtfsFile { Name = "out.bin", FileUid = 2, Length = data.Length };
            file.Extents.Add(new LtfsExtent { Partition = LtfsPartition.B, StartBlock = 10, ByteOffset = 0, ByteCount = data.Length, FileOffset = 0 });
            var destination = Path.Combine(temp, "out.bin");
            await File.WriteAllTextAsync(destination, "existing");

            var result = await new LtfsWriterService(device).ExtractAsync(new LtfsExtractRequest(
                [new LtfsReadTarget(file, destination, LtfsReadOperation.ExtractOnly)],
                new LtfsWriterOptions(BlockSizeBytes: data.Length, MemoryCacheLimitBytes: LtfsWriterOptions.MinimumMemoryCacheLimitBytes),
                ExtractOptions: new LtfsExtractOptions(ConflictPolicy: LtfsExtractConflictPolicy.RenameWithSuffix)));

            var renamed = Path.Combine(temp, "out (1).bin");
            await Assert.That(await File.ReadAllTextAsync(destination)).IsEqualTo("existing");
            await Assert.That(await File.ReadAllTextAsync(renamed)).IsEqualTo("rename");
            await Assert.That(result.FileResults!.Single().DestinationPath).IsEqualTo(renamed);
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [Test]
    public async Task Extract_skip_if_same_length_and_timestamp_does_not_read_tape()
    {
        var temp = Path.Combine(Path.GetTempPath(), "KokoLtfsWriterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var modify = DateTimeOffset.UnixEpoch.AddMinutes(5);
            var destination = Path.Combine(temp, "same.bin");
            await File.WriteAllTextAsync(destination, "same");
            File.SetLastWriteTimeUtc(destination, modify.UtcDateTime);

            var file = new LtfsFile
            {
                Name = "same.bin",
                FileUid = 2,
                Length = 4,
                ModifyTime = LtfsIndex.FormatLtfsTime(modify),
            };
            file.Extents.Add(new LtfsExtent { Partition = LtfsPartition.B, StartBlock = 10, ByteOffset = 0, ByteCount = 4, FileOffset = 0 });
            var device = new RecordingWriterDevice();

            var result = await new LtfsWriterService(device).ExtractAsync(new LtfsExtractRequest(
                [new LtfsReadTarget(file, destination, LtfsReadOperation.ExtractOnly)],
                new LtfsWriterOptions(BlockSizeBytes: 4, MemoryCacheLimitBytes: LtfsWriterOptions.MinimumMemoryCacheLimitBytes),
                ExtractOptions: new LtfsExtractOptions(ConflictPolicy: LtfsExtractConflictPolicy.SkipIfSameLengthAndTimestamp)));

            await Assert.That(result.BytesRead).IsEqualTo(0L);
            await Assert.That(result.FileResults!.Single().ExtractStatus).IsEqualTo(LtfsExtractFileStatus.Skipped);
            await Assert.That(device.Events).DoesNotContain("Read:B:10");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [Test]
    public async Task Extract_symlink_skip_reports_skipped_without_reading()
    {
        var file = new LtfsFile { Name = "link", FileUid = 2, Length = 0, Symlink = "target.txt" };
        var result = await new LtfsWriterService(new RecordingWriterDevice()).ExtractAsync(new LtfsExtractRequest(
            [new LtfsReadTarget(file, "link", LtfsReadOperation.ExtractOnly)],
            new LtfsWriterOptions(MemoryCacheLimitBytes: LtfsWriterOptions.MinimumMemoryCacheLimitBytes),
            ExtractOptions: new LtfsExtractOptions(SymlinkPolicy: LtfsSymlinkRestorePolicy.Skip)));

        await Assert.That(result.FileResults!.Single().ExtractStatus).IsEqualTo(LtfsExtractFileStatus.Skipped);
    }

    [Test]
    public async Task Write_dedup_reuses_existing_extents_by_size_and_selected_hash()
    {
        var device = new RecordingWriterDevice { Position = new LtfsTapePosition(LtfsPartition.B, 10) };
        var data = Encoding.ASCII.GetBytes("duplicate");
        var index = CreateIndex();
        var existing = new LtfsFile
        {
            Name = "existing.bin",
            FileUid = 2,
            Length = data.Length,
            OpenForWrite = false,
        };
        existing.Extents.Add(new LtfsExtent { Partition = LtfsPartition.B, StartBlock = 42, ByteOffset = 0, ByteCount = data.Length, FileOffset = 0 });
        existing.SetExtendedAttribute("ltfs.hash.sha1sum", ComputeHash(data, LtfsHashAlgorithmKind.Sha1));
        existing.SetExtendedAttribute("ltfs.hash.md5sum", "legacy-md5");
        index.RootDirectory!.Files.Add(existing);
        index.HighestFileUid = 2;

        var result = await new LtfsWriterService(device).WriteFilesAsync(new LtfsWriteRequest(
            index,
            index.RootDirectory!,
            [new LtfsWriteSource("new.bin", data.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            new LtfsWriterOptions(
                BlockSizeBytes: 8,
                WriteDataPartitionIndexOnComplete: false,
                RefreshIndexPartitionOnComplete: false,
                WriteVci: false,
                Dedup: new LtfsDedupOptions(Enabled: true))));

        var written = result.Index.RootDirectory!.Files.Single(x => x.Name == "new.bin");
        await Assert.That(result.BytesWritten).IsEqualTo(0L);
        await Assert.That(result.FilesWritten).IsEqualTo(1L);
        await Assert.That(written.Extents.Single().StartBlock).IsEqualTo(42L);
        await Assert.That(written.GetExtendedAttribute("ltfs.hash.sha1sum")).IsEqualTo(existing.GetExtendedAttribute("ltfs.hash.sha1sum"));
        await Assert.That(written.GetExtendedAttribute("ltfs.hash.md5sum")).IsEqualTo("legacy-md5");
        await Assert.That(string.Join("|", device.Events)).DoesNotContain("WriteBlock:B:10");
    }

    [Test]
    public async Task Write_failure_publishes_failure_event_and_releases_drive()
    {
        var device = new RecordingWriterDevice { FailWrites = true };
        device.Position = new LtfsTapePosition(LtfsPartition.B, 10);
        var bus = new KokoEventBus();
        var steps = new List<LtfsWriterStepKind>();
        using var subscription = bus.Subscribe<LtfsWriterStepEvent>(x => steps.Add(x.Step));
        var data = Encoding.ASCII.GetBytes("abc");

        await Assert.That(async () => await new LtfsWriterService(device, bus).WriteFilesAsync(new LtfsWriteRequest(
            CreateIndex(),
            CreateIndex().RootDirectory!,
            [new LtfsWriteSource("bad.bin", data.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            new LtfsWriterOptions(BlockSizeBytes: 8)))).ThrowsException();

        await Assert.That(steps.Contains(LtfsWriterStepKind.Warning)).IsTrue();
        await Assert.That(steps.Contains(LtfsWriterStepKind.Failed)).IsTrue();
        await Assert.That(device.Events.TakeLast(2).ToArray()).IsEquivalentTo(["Prevent:False", "Release"]);
    }

    [Test]
    public async Task Write_release_clears_device_key_when_configured()
    {
        var device = new RecordingWriterDevice { Position = new LtfsTapePosition(LtfsPartition.B, 10) };

        await new LtfsWriterService(device).WriteFilesAsync(new LtfsWriteRequest(
            CreateIndex(),
            CreateIndex().RootDirectory!,
            [],
            new LtfsWriterOptions(
                BlockSizeBytes: 8,
                WriteDataPartitionIndexOnComplete: false,
                RefreshIndexPartitionOnComplete: false,
                WriteVci: false,
                Encryption: new LtfsEncryptionOptions(
                    LtfsEncryptionMode.WriteKeyRequired,
                    new StaticKeyProvider(Enumerable.Repeat((byte)0x33, 32).ToArray()),
                    "write-key",
                    ClearDeviceKeyOnRelease: true))));

        await Assert.That(string.Join("|", device.Events)).Contains("SetEncryption:33333333");
        await Assert.That(device.Events.TakeLast(3).ToArray()).IsEquivalentTo(["SetEncryption:null", "Prevent:False", "Release"]);
    }

    [Test]
    public async Task Writer_options_reject_cache_smaller_than_256m()
    {
        var service = new LtfsWriterService(new RecordingWriterDevice());
        await Assert.That(async () => await service.ExtractAsync(new LtfsExtractRequest([], new LtfsWriterOptions(MemoryCacheLimitBytes: 1)))).ThrowsException();
    }

    private static LtfsIndex CreateIndex()
    {
        var index = new LtfsIndex
        {
            Creator = "Koko.Core.Tests",
            VolumeUuid = Guid.Parse("129fa6c4-b043-4286-9188-0c588a94ad89"),
            GenerationNumber = 1,
            UpdateTime = LtfsIndex.FormatLtfsTime(DateTimeOffset.UnixEpoch),
            Location = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 5 },
            PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 0 },
            HighestFileUid = 1,
        };
        index.RootDirectories.Add(new LtfsDirectory { Name = "VOL", FileUid = 1 });
        return index;
    }

    private static byte[] WriteIndex(LtfsIndex index)
    {
        using var stream = new MemoryStream();
        LtfsSchemaWriter.Write(stream, index, new LtfsSchemaWriterOptions(LeaveOpen: true));
        return stream.ToArray();
    }

    private static LtfsLabel CreateLabel(Guid volumeUuid, LtfsPartition locationPartition)
    {
        return new LtfsLabel
        {
            VolumeUuid = volumeUuid,
            LocationPartition = locationPartition,
            IndexPartition = LtfsPartition.A,
            DataPartition = LtfsPartition.B,
            BlockSize = 8,
        };
    }

    private static void SetupLegacyTwoPartition(RecordingWriterDevice device, LtfsLabel label, LtfsIndex index)
    {
        device.Blocks[(LtfsPartition.A, 0)] = LtfsVol1Label.Create("KOKO01");
        device.FilemarkPayloadStarts[(LtfsPartition.A, 1)] = 2;
        device.FilemarkPayloadStarts[(LtfsPartition.A, 3)] = 5;
        device.IndexPayloads[(LtfsPartition.A, 2)] = LtfsLabelWriter.ToArray(label);
        device.IndexPayloads[(LtfsPartition.A, 5)] = WriteIndex(index);
    }

    private static string ComputeHash(byte[] data, LtfsHashAlgorithmKind algorithm)
    {
        using var hashSet = LtfsFileHashSet.Create(algorithm switch
        {
            LtfsHashAlgorithmKind.Blake3 => new LtfsHashOptions(Blake3: true, Sha512: false, Sha256: false, XxHash128: false, XxHash64: false, Sha1: false, Md5: false),
            LtfsHashAlgorithmKind.Sha256 => new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: true, XxHash128: false, XxHash64: false, Sha1: false, Md5: false),
            LtfsHashAlgorithmKind.XxHash128 => new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: false, XxHash128: true, XxHash64: false, Sha1: false, Md5: false),
            LtfsHashAlgorithmKind.Sha1 => new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: false, XxHash128: false, XxHash64: false, Sha1: true, Md5: false),
            LtfsHashAlgorithmKind.Crc32 => new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: false, XxHash128: false, XxHash64: false, Sha1: false, Md5: false, Crc32: true),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        });
        hashSet.Append(data);
        return hashSet.GetHex(algorithm);
    }

    private static LogSenseResponse BuildCapacityLogSenseResponse(long remainingBytes)
    {
        var raw = new byte[16];
        raw[0] = LogPageCode.TapeCapacity.Value;
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(2, 2), 12);
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(4, 2), 1);
        raw[7] = 8;
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(8, 8), (ulong)remainingBytes);
        return LogSenseResponse.FromRaw(raw);
    }

    private sealed class RecordingWriterDevice : ILtfsWriterDevice, ILtfsEncryptionCapableDevice, ILtfsMetadataExportDevice, ILtfsPartitionMamDevice
    {
        public List<string> Events { get; } = [];
        public List<long> ReadBlockLimits { get; } = [];
        public List<long> ReadToFilemarkLimits { get; } = [];
        public Dictionary<(LtfsPartition Partition, long Block), byte[]> Blocks { get; } = [];
        public Dictionary<(LtfsPartition Partition, ulong Block), byte[]> IndexPayloads { get; } = [];
        public Dictionary<(LtfsPartition Partition, ulong Filemark), ulong> FilemarkPayloadStarts { get; } = [];
        public Dictionary<LtfsPartition, IReadOnlyList<MamAttribute>> PartitionMamAttributes { get; init; } = [];
        public LtfsTapePosition Position { get; set; } = new(LtfsPartition.A, 0);
        public ulong? DataEodBlock { get; set; }
        public ulong? IndexEodBlock { get; set; }
        public ulong? DataEodFileNumber { get; set; }
        public ulong? IndexEodFileNumber { get; set; }
        public bool LocateFilemarkStopsAfterFilemark { get; set; }
        public bool FailWrites { get; set; }
        public bool FailNextWriteAfterAdvance { get; set; }
        public int ThrowVolumeOverflowOnWriteNumber { get; set; }
        public int WriteCount { get; set; }
        public LogSenseResponse LogSenseResponse { get; set; } = LogSenseResponse.FromRaw(Array.Empty<byte>());
        public IReadOnlyList<MamAttribute> MamAttributes { get; set; } = Array.Empty<MamAttribute>();
        public byte[]? CartridgeMemory { get; init; }

        public ValueTask ReserveAsync(CancellationToken cancellationToken = default)
        {
            Events.Add("Reserve");
            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
        {
            Events.Add("Release");
            return ValueTask.CompletedTask;
        }

        public ValueTask PreventRemovalAsync(bool prevent, CancellationToken cancellationToken = default)
        {
            Events.Add($"Prevent:{prevent}");
            return ValueTask.CompletedTask;
        }

        public ValueTask TestUnitReadyAsync(CancellationToken cancellationToken = default)
        {
            Events.Add("TestUnitReady");
            return ValueTask.CompletedTask;
        }

        public ValueTask SetBlockSizeAsync(long blockSizeBytes, CancellationToken cancellationToken = default)
        {
            Events.Add($"SetBlockSize:{blockSizeBytes}");
            return ValueTask.CompletedTask;
        }

        public ValueTask LocateAsync(LtfsPartition partition, ulong block, CancellationToken cancellationToken = default)
        {
            Position = new LtfsTapePosition(partition, block);
            Events.Add($"Locate:{partition}:{block}");
            return ValueTask.CompletedTask;
        }

        public ValueTask LocateAsync(LtfsPartition partition, long block, CancellationToken cancellationToken = default)
        {
            return LocateAsync(partition, checked((ulong)block), cancellationToken);
        }

        public ValueTask LocateEndOfDataAsync(LtfsPartition partition, CancellationToken cancellationToken = default)
        {
            var fileNumber = partition == LtfsPartition.B ? DataEodFileNumber : IndexEodFileNumber;
            Position = new LtfsTapePosition(
                partition,
                partition == LtfsPartition.B && DataEodBlock is { } dataBlock
                    ? dataBlock
                    : partition == LtfsPartition.A && IndexEodBlock is { } indexBlock
                        ? indexBlock
                        : Position.Partition == partition ? Position.Block : 10,
                fileNumber);
            Events.Add($"LocateEOD:{partition}");
            return ValueTask.CompletedTask;
        }

        public ValueTask LocateFilemarkAsync(LtfsPartition partition, ulong filemark, CancellationToken cancellationToken = default)
        {
            var block = FilemarkPayloadStarts.TryGetValue((partition, filemark), out var payloadStart)
                ? LocateFilemarkStopsAfterFilemark ? payloadStart : payloadStart - 1
                : filemark;
            Position = new LtfsTapePosition(partition, block, filemark);
            Events.Add($"LocateFM:{partition}:{filemark}");
            return ValueTask.CompletedTask;
        }

        public ValueTask<LtfsTapePosition> ReadPositionAsync(CancellationToken cancellationToken = default)
        {
            Events.Add($"ReadPosition:{Position.Partition}:{Position.Block}");
            return ValueTask.FromResult(Position);
        }

        public ValueTask<byte[]> ReadBlockAsync(long maximumBytes, CancellationToken cancellationToken = default)
        {
            ReadBlockLimits.Add(maximumBytes);
            Events.Add($"ReadBlock:{Position.Partition}:{Position.Block}");
            var data = Blocks[(Position.Partition, checked((long)Position.Block))];
            Position = Position with { Block = Position.Block + 1 };
            return ValueTask.FromResult(data);
        }

        public async ValueTask<int> ReadBlockAsync(LtfsPartition partition, long block, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await LocateAsync(partition, block, cancellationToken);
            var data = await ReadBlockAsync(buffer.Length, cancellationToken);
            data.CopyTo(buffer);
            return data.Length;
        }

        public ValueTask AdvancePastFilemarkAsync(CancellationToken cancellationToken = default)
        {
            if (Position.FileNumber is null)
                throw new LtfsWriterException("Expected filemark.");

            Position = Position with { Block = Position.Block + 1 };
            Events.Add($"AdvanceFM:{Position.Partition}:{Position.Block}");
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> ReadToFilemarkAsync(long blockSizeBytes, CancellationToken cancellationToken = default)
        {
            ReadToFilemarkLimits.Add(blockSizeBytes);
            Events.Add($"ReadToFM:{Position.Partition}:{Position.Block}");
            return ValueTask.FromResult(IndexPayloads[(Position.Partition, Position.Block)]);
        }

        public ValueTask WriteBlockAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            WriteCount += 1;
            if (ThrowVolumeOverflowOnWriteNumber == WriteCount)
                throw new LtfsScsiCommandException("Injected volume overflow.", true, ScsiCommandResult.From(false, 0x02, 0, CreateSense(0x0D, eom: true)));

            if (FailWrites)
                throw new InvalidOperationException("Injected write failure.");

            var array = data.ToArray();
            Blocks[(Position.Partition, checked((long)Position.Block))] = array;
            Events.Add($"WriteBlock:{Position.Partition}:{Position.Block}:{Classify(array)}");
            Position = Position with { Block = Position.Block + 1 };
            if (FailNextWriteAfterAdvance)
            {
                FailNextWriteAfterAdvance = false;
                throw new InvalidOperationException("Injected write failure after position advanced.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask WriteFilemarksAsync(uint count, CancellationToken cancellationToken = default)
        {
            Events.Add($"Filemarks:{Position.Partition}:{Position.Block}:{count}");
            Position = Position with { Block = Position.Block + count };
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteVciAsync(ulong generation, ulong? indexPartitionBlock, ulong dataPartitionBlock, Guid volumeUuid, CancellationToken cancellationToken = default)
        {
            Events.Add($"WriteVci:{generation}:{indexPartitionBlock}:{dataPartitionBlock}");
            return ValueTask.CompletedTask;
        }

        public ValueTask LoadUnloadAsync(bool load, CancellationToken cancellationToken = default)
        {
            Events.Add($"LoadUnload:{load}");
            return ValueTask.CompletedTask;
        }

        public ValueTask<LogSenseResponse> ReadLogSenseAsync(LogPageCode pageCode, CancellationToken cancellationToken = default)
        {
            Events.Add($"LogSense:{pageCode}");
            return ValueTask.FromResult(LogSenseResponse);
        }

        public ValueTask SetEncryptionAsync(ReadOnlyMemory<byte>? key, CancellationToken cancellationToken = default)
        {
            Events.Add($"SetEncryption:{(key is null ? "null" : Convert.ToHexString(key.Value.Span[..Math.Min(4, key.Value.Length)]))}");
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<MamAttribute>> ReadMamAttributesAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(MamAttributes);
        }

        public ValueTask<IReadOnlyList<MamAttribute>> ReadMamAttributesAsync(LtfsPartition partition, CancellationToken cancellationToken = default)
        {
            Events.Add($"ReadMam:{partition}");
            return ValueTask.FromResult(PartitionMamAttributes.TryGetValue(partition, out var attributes)
                ? attributes
                : Array.Empty<MamAttribute>());
        }

        public ValueTask<byte[]?> ReadCartridgeMemoryAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(CartridgeMemory);
        }

        private static string Classify(byte[] data)
        {
            var text = Encoding.UTF8.GetString(data);
            return text.Contains("<ltfsindex", StringComparison.Ordinal) ? "ltfsindex" : data.Length.ToString();
        }

        private static byte[] CreateSense(byte senseKey, bool eom)
        {
            var sense = new byte[14];
            sense[0] = 0x70;
            sense[2] = (byte)(senseKey | (eom ? 0x40 : 0));
            return sense;
        }
    }

    private sealed class StaticKeyProvider : ILtfsEncryptionKeyProvider
    {
        private readonly byte[] key;

        public StaticKeyProvider(byte[] key)
        {
            this.key = key;
        }

        public ValueTask<LtfsEncryptionKeyMaterial?> ResolveKeyAsync(LtfsEncryptionKeyRequest request, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<LtfsEncryptionKeyMaterial?>(new LtfsEncryptionKeyMaterial(key, request.KeyId));
        }
    }
}
