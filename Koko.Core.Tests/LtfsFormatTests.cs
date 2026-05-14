using Koko.Core.Events;
using Koko.Core.Ltfs;
using Koko.Core.Scsi.Commands;

namespace Koko.Core.Tests;

public sealed class LtfsFormatTests
{
    [Test]
    public async Task Format_two_partition_ltfs_writes_expected_layout_index_and_vci()
    {
        var device = new RecordingFormatDevice();
        var bus = new KokoEventBus();
        var steps = new List<LtfsFormatStepKind>();
        using var subscription = bus.Subscribe<LtfsFormatStepEvent>(x => steps.Add(x.Step));
        var service = new LtfsFormatService(device, bus);
        var uuid = Guid.Parse("129fa6c4-b043-4286-9188-0c588a94ad89");

        var result = await service.FormatAsync(new LtfsFormatRequest(
            VolumeName: "TESTVOL",
            Barcode: "ABC123L6",
            VolumeUuid: uuid,
            DestructiveConfirmationToken: LtfsFormatService.DestructiveConfirmationToken));

        await Assert.That(result.DataPartitionIndexBlock).IsEqualTo(5UL);
        await Assert.That(result.IndexPartitionIndexBlock).IsEqualTo(5UL);
        await Assert.That(result.Index.Location.Partition).IsEqualTo(LtfsPartition.A);
        await Assert.That(result.Index.Location.StartBlock).IsEqualTo(5UL);
        await Assert.That(result.Index.PreviousGenerationLocation.Partition).IsEqualTo(LtfsPartition.B);
        await Assert.That(result.Index.PreviousGenerationLocation.StartBlock).IsEqualTo(5UL);
        await Assert.That(result.Index.RootDirectory?.Name).IsEqualTo("TESTVOL");

        await Assert.That(string.Join("|", device.Events)).IsEqualTo(
            "Reserve|Prevent:True|TestUnitReady|ReadMaximumBlockSize|ReadMaximumExtraPartitionCount|SetCapacity:65535|Format:0|ConfigureTwoPartition:1:65535|Format:1|WriteMam:A:7|SetBlockSize:524288|Locate:B:0|WriteBlock:B:0:VOL1|Filemarks:B:1:1|WriteBlock:B:2:ltfslabel|Filemarks:B:3:2|ReadPosition:B:5|WriteBlock:B:5:ltfsindex|Filemarks:B:6:1|Locate:A:0|WriteBlock:A:0:VOL1|Filemarks:A:1:1|WriteBlock:A:2:ltfslabel|Filemarks:A:3:1|Filemarks:A:4:1|ReadPosition:A:5|WriteBlock:A:5:ltfsindex|Filemarks:A:6:1|WriteMam:B:1|WriteMam:A:1|Prevent:False|Release");

        var dataIndex = ReadIndex(device.Blocks[(LtfsPartition.B, 5)].Data);
        await Assert.That(dataIndex.Location.Partition).IsEqualTo(LtfsPartition.B);
        await Assert.That(dataIndex.Location.StartBlock).IsEqualTo(5UL);

        var indexCopy = ReadIndex(device.Blocks[(LtfsPartition.A, 5)].Data);
        await Assert.That(indexCopy.Location.Partition).IsEqualTo(LtfsPartition.A);
        await Assert.That(indexCopy.PreviousGenerationLocation.Partition).IsEqualTo(LtfsPartition.B);

        await Assert.That(LtfsVolumeCoherencyInformation.TryParse(device.Mam[(LtfsPartition.A, 0x080C)].AsSpan(), out var aVci)).IsTrue();
        await Assert.That(aVci.IndexBlock).IsEqualTo(5UL);
        await Assert.That(aVci.VolumeUuid).IsEqualTo(uuid);

        await Assert.That(steps.Contains(LtfsFormatStepKind.WriteDataPartitionIndex)).IsTrue();
        await Assert.That(steps.Contains(LtfsFormatStepKind.Completed)).IsTrue();
    }

    [Test]
    public async Task Format_requires_destructive_confirmation_token()
    {
        var service = new LtfsFormatService(new RecordingFormatDevice());

        await Assert.That(async () => await service.FormatAsync(new LtfsFormatRequest("TESTVOL"))).ThrowsException();
    }

    [Test]
    public async Task Format_detected_worm_uses_legacy_worm_layout()
    {
        var device = new WormRecordingFormatDevice { LogSenseResponse = BuildWormLogSenseResponse() };
        var service = new LtfsFormatService(device);

        var result = await service.FormatAsync(new LtfsFormatRequest(
            VolumeName: "WORMVOL",
            DestructiveConfirmationToken: LtfsFormatService.DestructiveConfirmationToken));

        await Assert.That(result.IndexPartitionIndexBlock).IsNull();
        await Assert.That(result.Index.Location.Partition).IsEqualTo(LtfsPartition.B);
        await Assert.That(string.Join("|", device.Events)).DoesNotContain("SetCapacity:");
        await Assert.That(string.Join("|", device.Events)).DoesNotContain("Format:0");
        await Assert.That(device.Blocks.ContainsKey((LtfsPartition.A, 5))).IsFalse();
    }

    private static LtfsIndex ReadIndex(byte[] data)
    {
        using var stream = new MemoryStream(data);
        return LtfsSchemaReader.Read(stream);
    }

    private sealed record RecordedBlock(string Kind, byte[] Data);

    private static LogSenseResponse BuildWormLogSenseResponse()
    {
        var raw = new byte[9];
        raw[0] = LogPageCode.VolumeStatistics.Value;
        raw[3] = 5;
        raw[4] = 0;
        raw[5] = 0x81;
        raw[7] = 1;
        raw[8] = 1;
        return LogSenseResponse.FromRaw(raw);
    }

    private class RecordingFormatDevice : ILtfsFormatDevice
    {
        private LtfsPartition partition = LtfsPartition.A;
        private ulong block;

        public List<string> Events { get; } = [];
        public Dictionary<(LtfsPartition Partition, ulong Block), RecordedBlock> Blocks { get; } = [];
        public Dictionary<(LtfsPartition Partition, ushort Id), byte[]> Mam { get; } = [];

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

        public ValueTask<long> ReadMaximumBlockSizeAsync(CancellationToken cancellationToken = default)
        {
            Events.Add("ReadMaximumBlockSize");
            return ValueTask.FromResult(1024L * 1024);
        }

        public ValueTask<byte> ReadMaximumExtraPartitionCountAsync(CancellationToken cancellationToken = default)
        {
            Events.Add("ReadMaximumExtraPartitionCount");
            return ValueTask.FromResult((byte)1);
        }

        public ValueTask SetCapacityAsync(ushort capacity, CancellationToken cancellationToken = default)
        {
            Events.Add($"SetCapacity:{capacity}");
            return ValueTask.CompletedTask;
        }

        public ValueTask ConfigureTwoPartitionAsync(ushort p0Size, ushort p1Size, CancellationToken cancellationToken = default)
        {
            Events.Add($"ConfigureTwoPartition:{p0Size}:{p1Size}");
            return ValueTask.CompletedTask;
        }

        public ValueTask FormatMediumAsync(byte formatCode, CancellationToken cancellationToken = default)
        {
            Events.Add($"Format:{formatCode}");
            return ValueTask.CompletedTask;
        }

        public ValueTask SetBlockSizeAsync(long blockSizeBytes, CancellationToken cancellationToken = default)
        {
            Events.Add($"SetBlockSize:{blockSizeBytes}");
            return ValueTask.CompletedTask;
        }

        public ValueTask LocateAsync(LtfsPartition partition, ulong block, CancellationToken cancellationToken = default)
        {
            this.partition = partition;
            this.block = block;
            Events.Add($"Locate:{partition}:{block}");
            return ValueTask.CompletedTask;
        }

        public ValueTask<LtfsTapePosition> ReadPositionAsync(CancellationToken cancellationToken = default)
        {
            Events.Add($"ReadPosition:{partition}:{block}");
            return ValueTask.FromResult(new LtfsTapePosition(partition, block));
        }

        public ValueTask WriteBlockAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            var array = data.ToArray();
            Blocks[(partition, block)] = new RecordedBlock(Classify(array), array);
            Events.Add($"WriteBlock:{partition}:{block}:{Blocks[(partition, block)].Kind}");
            block += 1;
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteFilemarksAsync(uint count, CancellationToken cancellationToken = default)
        {
            Events.Add($"Filemarks:{partition}:{block}:{count}");
            for (var i = 0; i < count; i++)
            {
                Blocks[(partition, block)] = new RecordedBlock("FM", []);
                block += 1;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask WriteMamAttributesAsync(LtfsPartition partition, IReadOnlyList<MamAttribute> attributes, CancellationToken cancellationToken = default)
        {
            Events.Add($"WriteMam:{partition}:{attributes.Count}");
            foreach (var attribute in attributes)
                Mam[(partition, attribute.Id)] = attribute.Value.ToArray();
            return ValueTask.CompletedTask;
        }

        private static string Classify(byte[] data)
        {
            if (data.Length >= 4 && data[0] == 'V' && data[1] == 'O' && data[2] == 'L' && data[3] == '1')
                return "VOL1";

            var text = System.Text.Encoding.UTF8.GetString(data);
            if (text.Contains("<ltfslabel", StringComparison.Ordinal))
                return "ltfslabel";
            if (text.Contains("<ltfsindex", StringComparison.Ordinal))
                return "ltfsindex";
            return "data";
        }
    }

    private sealed class WormRecordingFormatDevice : RecordingFormatDevice, ILtfsWormDetectionDevice
    {
        public LogSenseResponse LogSenseResponse { get; set; } = LogSenseResponse.FromRaw(Array.Empty<byte>());

        public ValueTask<LogSenseResponse> ReadLogSenseAsync(LogPageCode pageCode, CancellationToken cancellationToken = default)
        {
            Events.Add($"LogSense:{pageCode.Value}");
            return ValueTask.FromResult(LogSenseResponse);
        }
    }
}
