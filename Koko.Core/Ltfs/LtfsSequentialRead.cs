using System.Buffers;

namespace Koko.Core.Ltfs;

public enum LtfsReadOperation
{
    VerifyOnly,
    ExtractOnly,
    ExtractAndVerify
}

public enum LtfsSliceDelivery
{
    Direct,
    MemorySpool,
    LocateReplay
}

public sealed record LtfsSequentialReadPlanOptions(
    long LtfsBlockSizeBytes,
    long MemorySpoolLimitBytes = LtfsSequentialReadPlanner.DefaultMemorySpoolLimitBytes)
{
    public static LtfsSequentialReadPlanOptions Default { get; } = new(512 * 1024);
}

public sealed record LtfsReadTarget(
    LtfsFile File,
    string DestinationPath,
    LtfsReadOperation Operation);

public sealed record LtfsSliceConsumer(
    long FileUid,
    string FileName,
    string DestinationPath,
    LtfsReadOperation Operation,
    long FileOffset,
    long BlockOffset,
    long Length,
    LtfsSliceDelivery Delivery);

public sealed record LtfsBlockRequest(
    LtfsPartition Partition,
    long Block,
    long ReadLength,
    IReadOnlyList<LtfsSliceConsumer> Consumers);

public sealed record LtfsReadPass(
    int PassNumber,
    bool RequiresBackwardLocateBeforePass,
    IReadOnlyList<LtfsBlockRequest> Requests);

public sealed record LtfsSequentialReadPlan(
    IReadOnlyList<LtfsReadPass> Passes,
    long MaxMemorySpoolBytes,
    long MemorySpoolLimitBytes,
    bool UsesMemorySpool,
    bool UsesLocateReplay)
{
    public long ReadCommandCount => Passes.Sum(x => (long)x.Requests.Count);
    public long BackwardLocatePassCount => Passes.Count(x => x.RequiresBackwardLocateBeforePass);
}

public interface ILtfsBlockReader
{
    ValueTask LocateAsync(LtfsPartition partition, long block, CancellationToken cancellationToken = default);

    ValueTask<int> ReadBlockAsync(
        LtfsPartition partition,
        long block,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default);
}

public interface ILtfsReadSink
{
    ValueTask ReceiveAsync(
        LtfsSliceConsumer consumer,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default);
}

public static class LtfsSequentialReadPlanner
{
    public const long DefaultMemorySpoolLimitBytes = 512L * 1024 * 1024;

    public static LtfsSequentialReadPlan CreatePlan(
        IEnumerable<LtfsReadTarget> targets,
        LtfsSequentialReadPlanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        options ??= LtfsSequentialReadPlanOptions.Default;
        ValidateOptions(options);

        var slices = targets
            .SelectMany(target => ExpandTarget(target, options.LtfsBlockSizeBytes))
            .ToList();

        if (slices.Count == 0)
        {
            return new LtfsSequentialReadPlan(
                [new LtfsReadPass(0, false, [])],
                MaxMemorySpoolBytes: 0,
                MemorySpoolLimitBytes: options.MemorySpoolLimitBytes,
                UsesMemorySpool: false,
                UsesLocateReplay: false);
        }

        var maxMemorySpoolBytes = EstimateMaxMemorySpoolBytes(slices);
        if (maxMemorySpoolBytes <= options.MemorySpoolLimitBytes)
        {
            var pass = new LtfsReadPass(
                PassNumber: 0,
                RequiresBackwardLocateBeforePass: false,
                Requests: BuildRequests(MarkMemorySpoolDeliveries(slices)));

            return new LtfsSequentialReadPlan(
                [pass],
                maxMemorySpoolBytes,
                options.MemorySpoolLimitBytes,
                UsesMemorySpool: maxMemorySpoolBytes > 0,
                UsesLocateReplay: false);
        }

        var passes = BuildLocateReplayPasses(slices);
        return new LtfsSequentialReadPlan(
            passes,
            maxMemorySpoolBytes,
            options.MemorySpoolLimitBytes,
            UsesMemorySpool: false,
            UsesLocateReplay: passes.Count > 1);
    }

    private static void ValidateOptions(LtfsSequentialReadPlanOptions options)
    {
        if (options.LtfsBlockSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "LTFS block size must be greater than zero.");

        if (options.MemorySpoolLimitBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Memory spool limit cannot be negative.");
    }

    private static IEnumerable<PlannedSlice> ExpandTarget(LtfsReadTarget target, long blockSize)
    {
        ArgumentNullException.ThrowIfNull(target.File);

        if (target.File.Length == 0 || target.File.Symlink is not null)
            yield break;

        foreach (var extent in target.File.Extents.OrderBy(x => x.FileOffset))
        {
            if (extent.ByteCount <= 0)
                continue;

            if (extent.ByteOffset < 0 || extent.ByteOffset >= blockSize)
                throw new InvalidOperationException($"Extent byte offset {extent.ByteOffset} is outside LTFS block size {blockSize}.");

            var remaining = extent.ByteCount;
            var block = extent.StartBlock;
            var blockOffset = extent.ByteOffset;
            var fileOffset = extent.FileOffset;

            while (remaining > 0)
            {
                var length = Math.Min(blockSize - blockOffset, remaining);
                yield return new PlannedSlice(
                    target.File.FileUid,
                    target.File.Name,
                    target.DestinationPath,
                    target.Operation,
                    extent.Partition,
                    block,
                    blockOffset,
                    fileOffset,
                    length,
                    LtfsSliceDelivery.Direct);

                remaining -= length;
                fileOffset += length;
                block += 1;
                blockOffset = 0;
            }
        }
    }

    private static long EstimateMaxMemorySpoolBytes(IReadOnlyList<PlannedSlice> slices)
    {
        var expectedOffsets = CreateInitialExpectedOffsets(slices);
        var pendingOffsets = new Dictionary<long, SortedDictionary<long, long>>();
        long pendingBytes = 0;
        long maxPendingBytes = 0;

        foreach (var slice in SortPhysical(slices))
        {
            var expectedOffset = expectedOffsets[slice.FileUid];
            if (slice.FileOffset <= expectedOffset)
            {
                expectedOffsets[slice.FileUid] = Math.Max(expectedOffset, slice.FileOffset + slice.Length);
                DrainPending(slice.FileUid, expectedOffsets, pendingOffsets, ref pendingBytes);
                continue;
            }

            if (!pendingOffsets.TryGetValue(slice.FileUid, out var filePending))
            {
                filePending = [];
                pendingOffsets.Add(slice.FileUid, filePending);
            }

            filePending[slice.FileOffset] = slice.Length;
            pendingBytes += slice.Length;
            maxPendingBytes = Math.Max(maxPendingBytes, pendingBytes);
        }

        return maxPendingBytes;
    }

    private static IReadOnlyList<PlannedSlice> MarkMemorySpoolDeliveries(IReadOnlyList<PlannedSlice> slices)
    {
        var expectedOffsets = CreateInitialExpectedOffsets(slices);
        var pendingOffsets = new Dictionary<long, SortedDictionary<long, long>>();
        var memorySpoolKeys = new HashSet<SliceKey>();

        foreach (var slice in SortPhysical(slices))
        {
            var expectedOffset = expectedOffsets[slice.FileUid];
            if (slice.FileOffset <= expectedOffset)
            {
                expectedOffsets[slice.FileUid] = Math.Max(expectedOffset, slice.FileOffset + slice.Length);
                DrainPending(slice.FileUid, expectedOffsets, pendingOffsets);
                continue;
            }

            if (!pendingOffsets.TryGetValue(slice.FileUid, out var filePending))
            {
                filePending = [];
                pendingOffsets.Add(slice.FileUid, filePending);
            }

            filePending[slice.FileOffset] = slice.Length;
            memorySpoolKeys.Add(SliceKey.From(slice));
        }

        return slices
            .Select(slice => slice with
            {
                Delivery = memorySpoolKeys.Contains(SliceKey.From(slice))
                    ? LtfsSliceDelivery.MemorySpool
                    : LtfsSliceDelivery.Direct
            })
            .ToList();
    }

    private static IReadOnlyList<LtfsReadPass> BuildLocateReplayPasses(IReadOnlyList<PlannedSlice> slices)
    {
        var expectedOffsets = CreateInitialExpectedOffsets(slices);
        var remaining = slices.ToList();
        var passes = new List<LtfsReadPass>();

        while (remaining.Count > 0)
        {
            var passSlices = new List<PlannedSlice>();

            foreach (var slice in SortPhysical(remaining))
            {
                if (slice.FileOffset != expectedOffsets[slice.FileUid])
                    continue;

                passSlices.Add(slice with
                {
                    Delivery = passes.Count == 0 ? LtfsSliceDelivery.Direct : LtfsSliceDelivery.LocateReplay
                });
                expectedOffsets[slice.FileUid] += slice.Length;
            }

            if (passSlices.Count == 0)
                throw new InvalidOperationException("Cannot build a forward-only LTFS read pass. The extent list may contain gaps or overlaps.");

            var passKeys = passSlices.Select(SliceKey.From).ToHashSet();
            remaining.RemoveAll(slice => passKeys.Contains(SliceKey.From(slice)));

            passes.Add(new LtfsReadPass(
                passes.Count,
                RequiresBackwardLocateBeforePass: passes.Count > 0,
                Requests: BuildRequests(passSlices)));
        }

        return passes;
    }

    private static IReadOnlyList<LtfsBlockRequest> BuildRequests(IEnumerable<PlannedSlice> slices)
    {
        return slices
            .GroupBy(slice => new { slice.Partition, slice.Block })
            .OrderBy(group => PartitionOrder(group.Key.Partition))
            .ThenBy(group => group.Key.Block)
            .Select(group =>
            {
                var consumers = group
                    .OrderBy(x => x.BlockOffset)
                    .ThenBy(x => x.FileUid)
                    .ThenBy(x => x.FileOffset)
                    .Select(x => new LtfsSliceConsumer(
                        x.FileUid,
                        x.FileName,
                        x.DestinationPath,
                        x.Operation,
                        x.FileOffset,
                        x.BlockOffset,
                        x.Length,
                        x.Delivery))
                    .ToList();

                var readLength = consumers.Max(x => x.BlockOffset + x.Length);
                return new LtfsBlockRequest(group.Key.Partition, group.Key.Block, readLength, consumers);
            })
            .ToList();
    }

    private static Dictionary<long, long> CreateInitialExpectedOffsets(IEnumerable<PlannedSlice> slices)
    {
        return slices
            .GroupBy(x => x.FileUid)
            .ToDictionary(x => x.Key, _ => 0L);
    }

    private static IEnumerable<PlannedSlice> SortPhysical(IEnumerable<PlannedSlice> slices)
    {
        return slices
            .OrderBy(x => PartitionOrder(x.Partition))
            .ThenBy(x => x.Block)
            .ThenBy(x => x.BlockOffset)
            .ThenBy(x => x.FileUid)
            .ThenBy(x => x.FileOffset);
    }

    private static int PartitionOrder(LtfsPartition partition)
    {
        return partition == LtfsPartition.A ? 0 : 1;
    }

    private static void DrainPending(
        long fileUid,
        Dictionary<long, long> expectedOffsets,
        Dictionary<long, SortedDictionary<long, long>> pendingOffsets,
        ref long pendingBytes)
    {
        if (!pendingOffsets.TryGetValue(fileUid, out var filePending))
            return;

        while (filePending.TryGetValue(expectedOffsets[fileUid], out var length))
        {
            filePending.Remove(expectedOffsets[fileUid]);
            expectedOffsets[fileUid] += length;
            pendingBytes -= length;
        }
    }

    private static void DrainPending(
        long fileUid,
        Dictionary<long, long> expectedOffsets,
        Dictionary<long, SortedDictionary<long, long>> pendingOffsets)
    {
        if (!pendingOffsets.TryGetValue(fileUid, out var filePending))
            return;

        while (filePending.TryGetValue(expectedOffsets[fileUid], out var length))
        {
            filePending.Remove(expectedOffsets[fileUid]);
            expectedOffsets[fileUid] += length;
        }
    }

    private sealed record PlannedSlice(
        long FileUid,
        string FileName,
        string DestinationPath,
        LtfsReadOperation Operation,
        LtfsPartition Partition,
        long Block,
        long BlockOffset,
        long FileOffset,
        long Length,
        LtfsSliceDelivery Delivery);

    private readonly record struct SliceKey(long FileUid, long FileOffset, LtfsPartition Partition, long Block, long BlockOffset, long Length)
    {
        public static SliceKey From(PlannedSlice slice)
        {
            return new SliceKey(slice.FileUid, slice.FileOffset, slice.Partition, slice.Block, slice.BlockOffset, slice.Length);
        }
    }
}

public sealed class LtfsSequentialReadExecutor
{
    private readonly ILtfsBlockReader reader;

    public LtfsSequentialReadExecutor(ILtfsBlockReader reader)
    {
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async ValueTask ExecuteAsync(
        LtfsSequentialReadPlan plan,
        ILtfsReadSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sink);

        var memorySpool = new Dictionary<long, SortedDictionary<long, PendingSpoolSlice>>();
        var expectedOffsets = CreateInitialExpectedOffsets(plan);
        LtfsPartition? currentPartition = null;
        long? currentBlock = null;

        foreach (var pass in plan.Passes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var request in pass.Requests)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (currentPartition != request.Partition || currentBlock != request.Block)
                    await reader.LocateAsync(request.Partition, request.Block, cancellationToken).ConfigureAwait(false);

                var buffer = ArrayPool<byte>.Shared.Rent(checked((int)request.ReadLength));
                try
                {
                    var bytesRead = await reader.ReadBlockAsync(
                        request.Partition,
                        request.Block,
                        buffer.AsMemory(0, checked((int)request.ReadLength)),
                        cancellationToken).ConfigureAwait(false);

                    if (bytesRead < request.ReadLength)
                        throw new InvalidOperationException($"Read P{request.Partition} B{request.Block} returned {bytesRead} bytes, expected {request.ReadLength}.");

                    foreach (var consumer in request.Consumers)
                    {
                        var slice = buffer.AsMemory(checked((int)consumer.BlockOffset), checked((int)consumer.Length));
                        if (consumer.Delivery == LtfsSliceDelivery.MemorySpool)
                        {
                            AddMemorySpool(memorySpool, consumer, slice.Span);
                            continue;
                        }

                        await DeliverAsync(consumer, slice, sink, expectedOffsets, cancellationToken).ConfigureAwait(false);
                        await DrainMemorySpoolAsync(consumer.FileUid, memorySpool, sink, expectedOffsets, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                currentPartition = request.Partition;
                currentBlock = request.Block + 1;
            }
        }

        if (memorySpool.Count != 0)
            throw new InvalidOperationException("Memory spool still contains undelivered LTFS slices after all passes completed.");
    }

    private static Dictionary<long, long> CreateInitialExpectedOffsets(LtfsSequentialReadPlan plan)
    {
        return plan.Passes
            .SelectMany(pass => pass.Requests)
            .SelectMany(request => request.Consumers)
            .GroupBy(consumer => consumer.FileUid)
            .ToDictionary(group => group.Key, _ => 0L);
    }

    private static void AddMemorySpool(
        Dictionary<long, SortedDictionary<long, PendingSpoolSlice>> memorySpool,
        LtfsSliceConsumer consumer,
        ReadOnlySpan<byte> data)
    {
        if (!memorySpool.TryGetValue(consumer.FileUid, out var fileSpool))
        {
            fileSpool = [];
            memorySpool.Add(consumer.FileUid, fileSpool);
        }

        fileSpool[consumer.FileOffset] = new PendingSpoolSlice(consumer, data.ToArray());
    }

    private static async ValueTask DrainMemorySpoolAsync(
        long fileUid,
        Dictionary<long, SortedDictionary<long, PendingSpoolSlice>> memorySpool,
        ILtfsReadSink sink,
        Dictionary<long, long> expectedOffsets,
        CancellationToken cancellationToken)
    {
        if (!memorySpool.TryGetValue(fileUid, out var fileSpool))
            return;

        while (fileSpool.TryGetValue(expectedOffsets[fileUid], out var pending))
        {
            fileSpool.Remove(expectedOffsets[fileUid]);
            await DeliverAsync(pending.Consumer, pending.Data, sink, expectedOffsets, cancellationToken).ConfigureAwait(false);
        }

        if (fileSpool.Count == 0)
            memorySpool.Remove(fileUid);
    }

    private static async ValueTask DeliverAsync(
        LtfsSliceConsumer consumer,
        ReadOnlyMemory<byte> data,
        ILtfsReadSink sink,
        Dictionary<long, long> expectedOffsets,
        CancellationToken cancellationToken)
    {
        var expectedOffset = expectedOffsets[consumer.FileUid];
        if (consumer.FileOffset != expectedOffset)
        {
            throw new InvalidOperationException(
                $"File {consumer.FileUid} received offset {consumer.FileOffset}, expected {expectedOffset}. Hash/extract delivery must be file-order.");
        }

        await sink.ReceiveAsync(consumer, data, cancellationToken).ConfigureAwait(false);
        expectedOffsets[consumer.FileUid] += consumer.Length;
    }

    private sealed record PendingSpoolSlice(LtfsSliceConsumer Consumer, byte[] Data);
}
