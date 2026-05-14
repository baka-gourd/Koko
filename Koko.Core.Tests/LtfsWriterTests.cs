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
    public async Task Write_default_policies_do_not_add_health_telemetry_between_data_blocks()
    {
        var device = new RecordingWriterDevice { Position = new LtfsTapePosition(LtfsPartition.B, 10) };
        var data = Encoding.ASCII.GetBytes("abcdefghijklmnop");

        await new LtfsWriterService(device).WriteFilesAsync(new LtfsWriteRequest(
            CreateIndex(),
            CreateIndex().RootDirectory!,
            [new LtfsWriteSource("large.bin", data.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            new LtfsWriterOptions(BlockSizeBytes: 8, WriteDataPartitionIndexOnComplete: false, RefreshIndexPartitionOnComplete: false, WriteVci: false)));

        await Assert.That(string.Join("|", device.Events)).IsEqualTo("Reserve|Prevent:True|TestUnitReady|SetBlockSize:8|LocateEOD:B|ReadPosition:B:10|WriteBlock:B:10:8|WriteBlock:B:11:8|Prevent:False|Release");
    }

    [Test]
    public async Task Write_auto_reload_checkpoints_flushes_reloads_and_relocates_data_eod_at_file_boundary()
    {
        var device = new RecordingWriterDevice { Position = new LtfsTapePosition(LtfsPartition.B, 10) };
        var data = Encoding.ASCII.GetBytes("abcdefgh");
        var healthEvents = new List<LtfsWriteHealthPolicyEvent>();
        var bus = new KokoEventBus();
        using var subscription = bus.Subscribe<LtfsWriteHealthPolicyEvent>(healthEvents.Add);

        var result = await new LtfsWriterService(device, bus).WriteFilesAsync(new LtfsWriteRequest(
            CreateIndex(),
            CreateIndex().RootDirectory!,
            [new LtfsWriteSource("reload.bin", data.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            new LtfsWriterOptions(
                BlockSizeBytes: 8,
                WriteDataPartitionIndexOnComplete: false,
                RefreshIndexPartitionOnComplete: false,
                WriteVci: false,
                AutoReloadPolicy: new LtfsAutoReloadPolicyOptions(
                    Enabled: true,
                    LowSpeedMiBPerSecond: 0,
                    HighSpeedMiBPerSecond: double.MaxValue,
                    SustainedDuration: TimeSpan.Zero),
                HealthSampling: new LtfsHealthSamplingOptions(
                    CustomSampler: (_, _, _) => ValueTask.FromResult<double?>(0)))));

        var eventTrace = string.Join("|", device.Events);
        await Assert.That(result.DataPartitionIndexWritten).IsTrue();
        await Assert.That(healthEvents.Single().Action).IsEqualTo(LtfsWriteHealthAction.Reload);
        await Assert.That(eventTrace).Contains("WriteBlock:B:10:8|Filemarks:B:11:1|ReadPosition:B:12|WriteBlock:B:12:");
        await Assert.That(eventTrace).Contains("|Filemarks:B:100:0|LoadUnload:False|LoadUnload:True|TestUnitReady|SetBlockSize:8|LocateEOD:B|ReadPosition:B:100");
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

        await new LtfsWriterService(device).WriteFilesAsync(new LtfsWriteRequest(
            CreateIndex(),
            CreateIndex().RootDirectory!,
            [new LtfsWriteSource("encrypted.bin", data.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(data, writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
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
                    SustainedDuration: TimeSpan.Zero),
                HealthSampling: new LtfsHealthSamplingOptions(CustomSampler: (_, _, _) => ValueTask.FromResult<double?>(0)))));

        var eventTrace = string.Join("|", device.Events);
        await Assert.That(eventTrace).StartsWith("Reserve|Prevent:True|TestUnitReady|SetEncryption:01020304|SetBlockSize:8|LocateEOD:B");
        await Assert.That(eventTrace).Contains("LoadUnload:True|TestUnitReady|SetEncryption:01020304|SetBlockSize:8|LocateEOD:B");
    }

    [Test]
    public async Task Write_autosave_exports_single_tar_zstandard_archive()
    {
        var temp = Path.Combine(Path.GetTempPath(), "KokoLtfsAutosaveTests", Guid.NewGuid().ToString("N"));
        try
        {
            var device = new RecordingWriterDevice { Position = new LtfsTapePosition(LtfsPartition.B, 10) };
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
    public async Task Discovery_loads_index_from_vci_and_reports_dirty_append()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var device = new RecordingWriterDevice
        {
            Position = new LtfsTapePosition(LtfsPartition.B, 50),
            DataEodBlock = 50,
            MamAttributes = [new LtfsVolumeCoherencyInformation(index.GenerationNumber, 42, index.VolumeUuid).ToMamAttribute()],
        };
        device.IndexPayloads[(LtfsPartition.B, 42)] = WriteIndex(index);

        var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(writerOptions: new LtfsWriterOptions(BlockSizeBytes: 8));

        await Assert.That(result.Index.GenerationNumber).IsEqualTo(index.GenerationNumber);
        await Assert.That(result.Source).IsEqualTo(LtfsIndexDiscoverySource.VciDataPartition);
        await Assert.That(result.AppendPoint.Block).IsEqualTo(50UL);
        await Assert.That(result.DirtyAppendDetected).IsTrue();
        await Assert.That(string.Join("|", device.Events)).Contains("Locate:B:42|ReadToFM:B:42|LocateEOD:B|ReadPosition:B:50");
    }

    [Test]
    public async Task Discovery_applies_encryption_before_loading_vci_index()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var device = new RecordingWriterDevice
        {
            Position = new LtfsTapePosition(LtfsPartition.B, 50),
            DataEodBlock = 50,
            MamAttributes = [new LtfsVolumeCoherencyInformation(index.GenerationNumber, 42, index.VolumeUuid).ToMamAttribute()],
        };
        device.IndexPayloads[(LtfsPartition.B, 42)] = WriteIndex(index);

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
    public async Task Discovery_fallback_scan_loads_data_checkpoint_when_vci_is_missing()
    {
        var index = CreateIndex();
        index.Location = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var device = new RecordingWriterDevice
        {
            DataEodBlock = 43,
        };
        device.Blocks[(LtfsPartition.B, 42)] = WriteIndex(index);

        var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(Mode: LtfsDiscoveryMode.Normal, NormalScanMaxBlocks: 48),
            new LtfsWriterOptions(BlockSizeBytes: 8));

        await Assert.That(result.Index.Location.StartBlock).IsEqualTo(42UL);
        await Assert.That(result.Source).IsEqualTo(LtfsIndexDiscoverySource.DataCheckpointScan);
        await Assert.That(result.Graph).IsNotNull();
    }

    [Test]
    public async Task Discovery_rejects_lower_generation_when_newer_invalid_candidate_exists()
    {
        var oldIndex = CreateIndex();
        oldIndex.GenerationNumber = 2;
        oldIndex.Location = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 42 };
        var badIndex = oldIndex.Clone();
        badIndex.GenerationNumber = 3;
        badIndex.HighestFileUid = 0;
        badIndex.RootDirectory!.FileUid = 2;
        badIndex.Location = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 43 };
        var device = new RecordingWriterDevice { DataEodBlock = 44 };
        device.Blocks[(LtfsPartition.B, 42)] = WriteIndex(oldIndex);
        device.Blocks[(LtfsPartition.B, 43)] = WriteIndex(badIndex);

        await Assert.That(async () => await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
            new LtfsVolumeDiscoveryOptions(Mode: LtfsDiscoveryMode.Normal, NormalScanMaxBlocks: 48),
            new LtfsWriterOptions(BlockSizeBytes: 8))).ThrowsException();
    }

    [Test]
    public async Task Append_validation_rejects_dirty_append_by_default()
    {
        var index = CreateIndex();
        var device = new RecordingWriterDevice { Position = new LtfsTapePosition(LtfsPartition.B, 20) };
        var discovery = new LtfsVolumeDiscoveryResult(index, null, new LtfsTapePosition(LtfsPartition.B, 10), LtfsIndexDiscoverySource.VciDataPartition, DirtyAppendDetected: true, Worm: false, WriteProtected: false, []);

        await Assert.That(async () => await new LtfsWriterService(device).WriteFilesAsync(new LtfsWriteRequest(
            index,
            index.RootDirectory!,
            [new LtfsWriteSource("dirty.bin", 1, _ => ValueTask.FromResult<Stream>(new MemoryStream([1], writable: false)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            new LtfsWriterOptions(
                BlockSizeBytes: 8,
                AppendValidation: new LtfsAppendValidationOptions(Enabled: true),
                Discovery: discovery,
                WriteDataPartitionIndexOnComplete: false,
                RefreshIndexPartitionOnComplete: false,
                WriteVci: false)))).ThrowsException();
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

        await Assert.That(string.Join("|", device.Events)).Contains("LocateEOD:A|Filemarks:A:20:1");
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
    public async Task Verify_prefers_blake3_over_other_enabled_hashes()
    {
        var data = Encoding.ASCII.GetBytes("verify-data");
        var device = new RecordingWriterDevice();
        device.Blocks[(LtfsPartition.B, 10)] = data;
        var file = new LtfsFile { Name = "verify.bin", FileUid = 2, Length = data.Length };
        file.Extents.Add(new LtfsExtent { Partition = LtfsPartition.B, StartBlock = 10, ByteOffset = 0, ByteCount = data.Length, FileOffset = 0 });
        file.SetExtendedAttribute("ltfs.hash.sha512sum", new string('0', 128));
        file.SetExtendedAttribute("ltfs.hash.blake3sum", ComputeHash(data, LtfsHashAlgorithmKind.Blake3));

        var result = await new LtfsWriterService(device).ExtractAsync(new LtfsExtractRequest(
            [new LtfsReadTarget(file, string.Empty, LtfsReadOperation.VerifyOnly)],
            new LtfsWriterOptions(BlockSizeBytes: data.Length, MemoryCacheLimitBytes: LtfsWriterOptions.MinimumMemoryCacheLimitBytes)));

        await Assert.That(result.BytesRead).IsEqualTo(data.Length);
    }

    [Test]
    public async Task Verify_uses_xxhash128_before_xxhash64_sha1_and_md5_when_stronger_hashes_missing()
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
            new LtfsWriterOptions(BlockSizeBytes: data.Length, MemoryCacheLimitBytes: LtfsWriterOptions.MinimumMemoryCacheLimitBytes)));

        await Assert.That(result.BytesRead).IsEqualTo(data.Length);
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

    private static string ComputeHash(byte[] data, LtfsHashAlgorithmKind algorithm)
    {
        using var hashSet = LtfsFileHashSet.Create(algorithm switch
        {
            LtfsHashAlgorithmKind.Blake3 => new LtfsHashOptions(Blake3: true, Sha512: false, Sha256: false, XxHash128: false, XxHash64: false, Sha1: false, Md5: false),
            LtfsHashAlgorithmKind.XxHash128 => new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: false, XxHash128: true, XxHash64: false, Sha1: false, Md5: false),
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

    private sealed class RecordingWriterDevice : ILtfsWriterDevice, ILtfsEncryptionCapableDevice, ILtfsMetadataExportDevice
    {
        public List<string> Events { get; } = [];
        public Dictionary<(LtfsPartition Partition, long Block), byte[]> Blocks { get; } = [];
        public Dictionary<(LtfsPartition Partition, ulong Block), byte[]> IndexPayloads { get; } = [];
        public LtfsTapePosition Position { get; set; } = new(LtfsPartition.A, 0);
        public ulong? DataEodBlock { get; set; }
        public ulong? IndexEodBlock { get; set; }
        public bool FailWrites { get; set; }
        public bool FailNextWriteAfterAdvance { get; set; }
        public int ThrowVolumeOverflowOnWriteNumber { get; set; }
        public int WriteCount { get; set; }
        public LogSenseResponse LogSenseResponse { get; set; } = LogSenseResponse.FromRaw(Array.Empty<byte>());
        public IReadOnlyList<MamAttribute> MamAttributes { get; set; } = Array.Empty<MamAttribute>();

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
            Position = new LtfsTapePosition(
                partition,
                partition == LtfsPartition.B && DataEodBlock is { } dataBlock
                    ? dataBlock
                    : partition == LtfsPartition.A && IndexEodBlock is { } indexBlock
                        ? indexBlock
                        : Position.Partition == partition ? Position.Block : 10);
            Events.Add($"LocateEOD:{partition}");
            return ValueTask.CompletedTask;
        }

        public ValueTask LocateFilemarkAsync(LtfsPartition partition, ulong filemark, CancellationToken cancellationToken = default)
        {
            Position = new LtfsTapePosition(partition, filemark);
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

        public ValueTask<byte[]> ReadToFilemarkAsync(long blockSizeBytes, CancellationToken cancellationToken = default)
        {
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

        public ValueTask<byte[]?> ReadCartridgeMemoryAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<byte[]?>(null);
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
