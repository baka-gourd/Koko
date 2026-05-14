using System.Buffers;

namespace Koko.Core.Ltfs;

public enum LtfsTapeCommandKind
{
    ReserveDrive,
    PreventRemoval,
    TestUnitReady,
    SetBlockSize,
    LocateEod,
    LocateBlock,
    LocateFilemark,
    ReadPosition,
    WriteDataBlock,
    WriteDataRun,
    WriteFilemark,
    Flush,
    RefreshIndexPartition,
    WriteVolumeCoherencyInformation,
    AllowRemoval,
    ReleaseDrive,
    ReadWriteErrorCounters,
    LoadUnload
}

public enum LtfsTapeCommandPriority
{
    Data,
    Health,
    Control,
    Telemetry,
    Background
}

public enum LtfsTapeBarrierKind
{
    None,
    SoftBoundary,
    HardBarrier,
    SessionBarrier
}

public sealed record LtfsTapeCommand(
    LtfsTapeCommandKind Kind,
    Func<CancellationToken, ValueTask> ExecuteAsync,
    LtfsTapeCommandPriority Priority = LtfsTapeCommandPriority.Control,
    LtfsTapeBarrierKind Barrier = LtfsTapeBarrierKind.HardBarrier,
    string? CorrelationId = null);

public sealed record LtfsTapeCommandResult(
    LtfsTapeCommand Command,
    bool Succeeded,
    Exception? Exception = null);

public sealed class LtfsTapeCommandQueue
{
    private readonly Queue<LtfsTapeCommand> commands = [];

    public int Count => commands.Count;

    public void Enqueue(LtfsTapeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        commands.Enqueue(command);
    }

    public bool TryDequeue(out LtfsTapeCommand command)
    {
        if (commands.Count == 0)
        {
            command = null!;
            return false;
        }

        command = commands.Dequeue();
        return true;
    }
}

public sealed class LtfsTapeCommandExecutor
{
    public async ValueTask<IReadOnlyList<LtfsTapeCommandResult>> ExecuteAsync(
        LtfsTapeCommandQueue queue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var results = new List<LtfsTapeCommandResult>();

        while (queue.TryDequeue(out var command))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                results.Add(new LtfsTapeCommandResult(command, true));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new LtfsTapeCommandResult(command, false, ex));
                throw;
            }
        }

        return results;
    }
}

public sealed class LtfsTapeBuffer : IDisposable
{
    private readonly ArrayPool<byte> pool;
    private int referenceCount;
    private bool returned;

    internal LtfsTapeBuffer(ArrayPool<byte> pool, byte[] array)
    {
        this.pool = pool;
        Array = array;
        referenceCount = 1;
    }

    public byte[] Array { get; }
    public int Length { get; set; }
    public Memory<byte> Memory => Array.AsMemory(0, Length);

    public void AddReference()
    {
        if (returned)
            throw new ObjectDisposedException(nameof(LtfsTapeBuffer));
        Interlocked.Increment(ref referenceCount);
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref referenceCount) != 0)
            return;

        returned = true;
        pool.Return(Array);
    }

    public void Dispose() => Release();
}

public sealed class LtfsTapeBufferPool
{
    private readonly ArrayPool<byte> pool;
    private readonly int blockSizeBytes;

    public LtfsTapeBufferPool(int blockSizeBytes, ArrayPool<byte>? pool = null)
    {
        if (blockSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSizeBytes));

        this.blockSizeBytes = blockSizeBytes;
        this.pool = pool ?? ArrayPool<byte>.Shared;
    }

    public LtfsTapeBuffer Rent()
    {
        return new LtfsTapeBuffer(pool, pool.Rent(blockSizeBytes));
    }
}
