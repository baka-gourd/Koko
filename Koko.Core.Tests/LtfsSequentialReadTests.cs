using Koko.Core.Ltfs;

namespace Koko.Core.Tests;

public sealed class LtfsSequentialReadTests
{
    [Test]
    public async Task Interleaved_extents_within_512m_use_memory_spool_single_pass()
    {
        var targets = CreateInterleavedTargets(blockSize: 16);

        var plan = LtfsSequentialReadPlanner.CreatePlan(
            targets,
            new LtfsSequentialReadPlanOptions(LtfsBlockSizeBytes: 16));

        await Assert.That(plan.Passes.Count).IsEqualTo(1);
        await Assert.That(plan.UsesMemorySpool).IsTrue();
        await Assert.That(plan.UsesLocateReplay).IsFalse();
        await Assert.That(plan.MaxMemorySpoolBytes).IsEqualTo(16L);
        await Assert.That(plan.MemorySpoolLimitBytes).IsEqualTo(512L * 1024 * 1024);

        var memoryConsumer = plan.Passes[0].Requests
            .Single(x => x.Block == 10)
            .Consumers
            .Single(x => x.FileUid == 2);

        await Assert.That(memoryConsumer.Delivery).IsEqualTo(LtfsSliceDelivery.MemorySpool);
    }

    [Test]
    public async Task Interleaved_extents_over_512m_use_locate_replay_not_memory_spool()
    {
        const long blockSize = LtfsSequentialReadPlanner.DefaultMemorySpoolLimitBytes + 1;
        var targets = CreateInterleavedTargets(blockSize);

        var plan = LtfsSequentialReadPlanner.CreatePlan(
            targets,
            new LtfsSequentialReadPlanOptions(LtfsBlockSizeBytes: blockSize));

        await Assert.That(plan.Passes.Count).IsEqualTo(2);
        await Assert.That(plan.UsesMemorySpool).IsFalse();
        await Assert.That(plan.UsesLocateReplay).IsTrue();
        await Assert.That(plan.MaxMemorySpoolBytes).IsEqualTo(blockSize);
        await Assert.That(plan.Passes[1].RequiresBackwardLocateBeforePass).IsTrue();

        var replayConsumer = plan.Passes[1].Requests[0].Consumers.Single();
        await Assert.That(replayConsumer.FileUid).IsEqualTo(2L);
        await Assert.That(replayConsumer.FileOffset).IsEqualTo(blockSize);
        await Assert.That(replayConsumer.Delivery).IsEqualTo(LtfsSliceDelivery.LocateReplay);
    }

    [Test]
    public async Task Locate_replay_executor_uses_backward_locate_without_reading_backwards()
    {
        var targets = CreateInterleavedTargets(blockSize: 16);
        var plan = LtfsSequentialReadPlanner.CreatePlan(
            targets,
            new LtfsSequentialReadPlanOptions(LtfsBlockSizeBytes: 16, MemorySpoolLimitBytes: 8));
        var reader = new RecordingBlockReader();
        var sink = new RecordingReadSink();

        await new LtfsSequentialReadExecutor(reader).ExecuteAsync(plan, sink);

        await Assert.That(string.Join("|", reader.Events)).IsEqualTo(
            "Locate:B:10|Read:B:10|Locate:B:20|Read:B:20|Locate:B:10|Read:B:10");
        await Assert.That(string.Join("|", sink.Events)).IsEqualTo(
            "1:0:16:Direct|1:16:16:Direct|2:0:16:Direct|2:16:16:LocateReplay");
    }

    private static IReadOnlyList<LtfsReadTarget> CreateInterleavedTargets(long blockSize)
    {
        var first = new LtfsFile
        {
            Name = "first.bin",
            FileUid = 1,
            Length = checked(blockSize * 2)
        };
        first.Extents.Add(new LtfsExtent
        {
            FileOffset = 0,
            Partition = LtfsPartition.B,
            StartBlock = 10,
            ByteOffset = 0,
            ByteCount = blockSize
        });
        first.Extents.Add(new LtfsExtent
        {
            FileOffset = blockSize,
            Partition = LtfsPartition.B,
            StartBlock = 20,
            ByteOffset = 0,
            ByteCount = blockSize
        });

        var second = new LtfsFile
        {
            Name = "second.bin",
            FileUid = 2,
            Length = checked(blockSize * 2)
        };
        second.Extents.Add(new LtfsExtent
        {
            FileOffset = 0,
            Partition = LtfsPartition.B,
            StartBlock = 20,
            ByteOffset = 0,
            ByteCount = blockSize
        });
        second.Extents.Add(new LtfsExtent
        {
            FileOffset = blockSize,
            Partition = LtfsPartition.B,
            StartBlock = 10,
            ByteOffset = 0,
            ByteCount = blockSize
        });

        return
        [
            new LtfsReadTarget(first, "first.bin", LtfsReadOperation.ExtractAndVerify),
            new LtfsReadTarget(second, "second.bin", LtfsReadOperation.ExtractAndVerify)
        ];
    }

    private sealed class RecordingBlockReader : ILtfsBlockReader
    {
        public List<string> Events { get; } = [];

        public ValueTask LocateAsync(LtfsPartition partition, long block, CancellationToken cancellationToken = default)
        {
            Events.Add($"Locate:{partition}:{block}");
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> ReadBlockAsync(
            LtfsPartition partition,
            long block,
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Events.Add($"Read:{partition}:{block}");
            buffer.Span.Fill((byte)block);
            return ValueTask.FromResult(buffer.Length);
        }
    }

    private sealed class RecordingReadSink : ILtfsReadSink
    {
        public List<string> Events { get; } = [];

        public ValueTask ReceiveAsync(
            LtfsSliceConsumer consumer,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            Events.Add($"{consumer.FileUid}:{consumer.FileOffset}:{data.Length}:{consumer.Delivery}");
            return ValueTask.CompletedTask;
        }
    }
}
