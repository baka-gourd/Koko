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

public enum LtfsTapeCommandSafeBoundary
{
    Block,
    File,
    PackedBlock,
    Checkpoint,
    Idle
}

public enum LtfsTapeCommandCancellationMode
{
    CompleteCurrentCommand,
    CompleteCurrentBlock,
    CompleteCurrentFile,
    AbortAfterCurrentCommand
}

public enum LtfsTapeCommandExecutorState
{
    Created,
    Reserved,
    Positioned,
    WritingData,
    CheckpointBarrier,
    HealthBarrier,
    FlushReloadBarrier,
    Paused,
    Finalizing,
    Faulted,
    Completed
}

public enum LtfsPauseMode
{
    AfterBlock,
    AfterFile,
    AfterCheckpoint
}

public enum LtfsCancelMode
{
    SoftAfterBlock,
    SoftAfterFile,
    AbortAfterCurrentCommand
}

public sealed record LtfsTapeCommand(
    LtfsTapeCommandKind Kind,
    Func<CancellationToken, ValueTask> ExecuteAsync,
    LtfsTapeCommandPriority Priority = LtfsTapeCommandPriority.Control,
    LtfsTapeBarrierKind Barrier = LtfsTapeBarrierKind.HardBarrier,
    string? CorrelationId = null,
    Guid? CommandId = null,
    LtfsTapeCommandSafeBoundary SafeBoundary = LtfsTapeCommandSafeBoundary.Block,
    LtfsTapePosition? ExpectedStartPosition = null,
    LtfsTapePosition? ExpectedEndPosition = null,
    bool AffectsPosition = true,
    bool AffectsIndex = false,
    bool CanCoalesce = false,
    LtfsTapeCommandCancellationMode CancellationMode = LtfsTapeCommandCancellationMode.CompleteCurrentCommand,
    TimeSpan? Timeout = null);

public sealed record LtfsTapeCommandResult(
    LtfsTapeCommand Command,
    bool Succeeded,
    Exception? Exception = null,
    LtfsTapeCommandExecutorState State = LtfsTapeCommandExecutorState.Completed);

public sealed record LtfsTapeCommandExecutorSnapshot(
    LtfsTapeCommandExecutorState State,
    int PendingCommandCount,
    bool PauseRequested,
    bool CancelRequested,
    LtfsCancelMode? CancelMode);

public sealed class LtfsTapeSessionControl
{
    private readonly ManualResetEventSlim resumeGate = new(true);

    public bool PauseRequested { get; private set; }

    public LtfsPauseMode? PauseMode { get; private set; }

    public bool CancelRequested { get; private set; }

    public LtfsCancelMode? CancelMode { get; private set; }

    public void RequestPause(LtfsPauseMode mode)
    {
        PauseMode = mode;
        PauseRequested = true;
        resumeGate.Reset();
    }

    public void Resume()
    {
        PauseRequested = false;
        PauseMode = null;
        resumeGate.Set();
    }

    public void RequestCancel(LtfsCancelMode mode)
    {
        CancelRequested = true;
        CancelMode = mode;
        resumeGate.Set();
    }

    internal void WaitIfPaused(CancellationToken cancellationToken)
    {
        while (PauseRequested && !CancelRequested)
            resumeGate.Wait(TimeSpan.FromMilliseconds(50), cancellationToken);
    }
}

public sealed class LtfsTapeCommandQueue
{
    private readonly List<LtfsTapeCommand> commands = [];

    public int Count => commands.Count;

    public void Enqueue(LtfsTapeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Priority is LtfsTapeCommandPriority.Telemetry or LtfsTapeCommandPriority.Background
            || (command.Priority == LtfsTapeCommandPriority.Control && command.CanCoalesce))
        {
            commands.RemoveAll(x => x.Kind == command.Kind && string.Equals(x.CorrelationId, command.CorrelationId, StringComparison.Ordinal));
        }

        commands.Add(command);
    }

    public bool TryDequeue(out LtfsTapeCommand command)
    {
        if (commands.Count == 0)
        {
            command = null!;
            return false;
        }

        var index = SelectNextCommandIndex();
        command = commands[index];
        commands.RemoveAt(index);
        return true;
    }

    private int SelectNextCommandIndex()
    {
        var bestIndex = 0;
        var bestRank = PriorityRank(commands[0]);
        for (var i = 1; i < commands.Count; i++)
        {
            var rank = PriorityRank(commands[i]);
            if (rank >= bestRank)
                continue;

            bestRank = rank;
            bestIndex = i;
        }

        return bestIndex;
    }

    private static int PriorityRank(LtfsTapeCommand command)
    {
        return command.Priority switch
        {
            LtfsTapeCommandPriority.Health => 1,
            LtfsTapeCommandPriority.Control => command.Barrier == LtfsTapeBarrierKind.SessionBarrier ? 0 : 2,
            LtfsTapeCommandPriority.Data => 3,
            LtfsTapeCommandPriority.Telemetry => 4,
            LtfsTapeCommandPriority.Background => 5,
            _ => 9,
        };
    }
}

public sealed class LtfsTapeCommandExecutor
{
    public LtfsTapeCommandExecutorState State { get; private set; } = LtfsTapeCommandExecutorState.Created;

    public LtfsTapeCommandExecutorSnapshot Snapshot(LtfsTapeCommandQueue queue, LtfsTapeSessionControl? control = null) =>
        new(State, queue.Count, control?.PauseRequested ?? false, control?.CancelRequested ?? false, control?.CancelMode);

    public async ValueTask<IReadOnlyList<LtfsTapeCommandResult>> ExecuteAsync(
        LtfsTapeCommandQueue queue,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(queue, control: null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<LtfsTapeCommandResult>> ExecuteAsync(
        LtfsTapeCommandQueue queue,
        LtfsTapeSessionControl? control,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var results = new List<LtfsTapeCommandResult>();

        while (queue.TryDequeue(out var command))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (control?.CancelRequested == true && control.CancelMode == LtfsCancelMode.AbortAfterCurrentCommand)
                break;

            if (control?.PauseRequested == true)
            {
                State = LtfsTapeCommandExecutorState.Paused;
                control.WaitIfPaused(cancellationToken);
            }

            if (control?.CancelRequested == true && command.Barrier != LtfsTapeBarrierKind.SessionBarrier)
                break;

            try
            {
                State = GetExecutionState(command);
                await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                State = command.Barrier == LtfsTapeBarrierKind.SessionBarrier ? LtfsTapeCommandExecutorState.Reserved : LtfsTapeCommandExecutorState.Positioned;
                results.Add(new LtfsTapeCommandResult(command, true, State: State));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                State = LtfsTapeCommandExecutorState.Faulted;
                results.Add(new LtfsTapeCommandResult(command, false, ex, State));
                throw;
            }
        }

        if (State != LtfsTapeCommandExecutorState.Faulted)
            State = control?.CancelRequested == true ? LtfsTapeCommandExecutorState.Faulted : LtfsTapeCommandExecutorState.Completed;
        return results;
    }

    private static LtfsTapeCommandExecutorState GetExecutionState(LtfsTapeCommand command)
    {
        return command.Kind switch
        {
            LtfsTapeCommandKind.WriteDataBlock or LtfsTapeCommandKind.WriteDataRun => LtfsTapeCommandExecutorState.WritingData,
            LtfsTapeCommandKind.RefreshIndexPartition or LtfsTapeCommandKind.WriteVolumeCoherencyInformation => LtfsTapeCommandExecutorState.CheckpointBarrier,
            LtfsTapeCommandKind.ReadWriteErrorCounters => LtfsTapeCommandExecutorState.HealthBarrier,
            LtfsTapeCommandKind.LoadUnload => LtfsTapeCommandExecutorState.FlushReloadBarrier,
            LtfsTapeCommandKind.AllowRemoval or LtfsTapeCommandKind.ReleaseDrive => LtfsTapeCommandExecutorState.Finalizing,
            _ => command.Barrier == LtfsTapeBarrierKind.SessionBarrier ? LtfsTapeCommandExecutorState.Reserved : LtfsTapeCommandExecutorState.Positioned,
        };
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
