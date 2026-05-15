using System.Buffers;

namespace Koko.Core.Ltfs;

public enum LtfsTapeCommandKind
{
    ReserveDrive,
    PreventRemoval,
    TestUnitReady,
    SetBlockSize,
    SetEncryption,
    ClearEncryption,
    LocateEod,
    LocateBlock,
    LocateFilemark,
    Rewind,
    SpaceFilemark,
    ReadPosition,
    ReadDataBlock,
    ReadDataRun,
    WriteDataBlock,
    WriteDataRun,
    WriteFilemark,
    Flush,
    RefreshIndexPartition,
    RefreshCapacity,
    WriteVolumeCoherencyInformation,
    WriteMamAttributes,
    FormatMedium,
    ConfigurePartition,
    SetCapacity,
    AllowRemoval,
    ReleaseDrive,
    ReadWriteErrorCounters,
    ReadTapeAlert,
    ReadVolumeStatistics,
    ReadDataCompression,
    ReadTapeCapacity,
    ReadTemperature,
    ReadDeviceStatus,
    LoadUnload,
    PauseAtBoundary,
    Resume,
    SoftCancel,
    AbortAfterCurrentCommand,
    ForceCheckpoint,
    ForceFlushReload
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
    RefreshingIndexPartition,
    HealthBarrier,
    FlushReloadBarrier,
    RecoveringPosition,
    Paused,
    Finalizing,
    Faulted,
    Completed
}

public enum LtfsTapePositionKnowledge
{
    Unknown,
    ExpectedOnly,
    Verified
}

public enum LtfsTapeCommandOutcomeKind
{
    Succeeded,
    RetryableNoPositionChange,
    CommittedPositionAdvanced,
    PositionUnknown,
    EarlyWarningEndOfMedium,
    EndOfMedium,
    VolumeOverflow,
    WriteProtected,
    NotReady,
    UnitAttention,
    MediumOrHardwareError,
    TimeoutOrTransport,
    UnknownFailure
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
    string? CoalesceKey = null,
    long LogicalBlockCount = 1,
    LtfsTapeCommandCancellationMode CancellationMode = LtfsTapeCommandCancellationMode.CompleteCurrentCommand,
    TimeSpan? Timeout = null,
    Func<CancellationToken, ValueTask<LtfsTapePosition>>? ReadPositionAsync = null);

public sealed record LtfsTapeCommandResult(
    LtfsTapeCommand Command,
    bool Succeeded,
    Exception? Exception = null,
    LtfsTapeCommandOutcomeKind Outcome = LtfsTapeCommandOutcomeKind.Succeeded,
    LtfsTapeCommandExecutorState State = LtfsTapeCommandExecutorState.Completed);

public sealed record LtfsTapeCommandExecutorSnapshot(
    LtfsTapeCommandExecutorState State,
    int PendingCommandCount,
    bool PauseRequested,
    bool CancelRequested,
    LtfsCancelMode? CancelMode,
    LtfsTapePosition? ExpectedPosition = null,
    LtfsTapePosition? RealPosition = null,
    LtfsTapePositionKnowledge PositionKnowledge = LtfsTapePositionKnowledge.Unknown,
    Guid? LastCommandId = null,
    bool Buffered = false,
    bool PositionUncertain = false,
    string? PositionUncertaintyReason = null)
{
    public bool PositionKnown => PositionKnowledge != LtfsTapePositionKnowledge.Unknown;
}

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
        if (TryCoalesceWithPrevious(command))
            return;

        if (command.Priority is LtfsTapeCommandPriority.Telemetry or LtfsTapeCommandPriority.Background
            || (command.Priority == LtfsTapeCommandPriority.Control && command.CanCoalesce))
        {
            commands.RemoveAll(x =>
                x.Kind == command.Kind
                && string.Equals(x.CorrelationId, command.CorrelationId, StringComparison.Ordinal)
                && string.Equals(x.CoalesceKey, command.CoalesceKey, StringComparison.Ordinal));
        }

        commands.Add(command);
    }

    private bool TryCoalesceWithPrevious(LtfsTapeCommand command)
    {
        if (!CanCoalesceData(command) || commands.Count == 0)
            return false;

        var previous = commands[^1];
        if (!CanCoalesceData(previous))
            return false;

        if (previous.Barrier != LtfsTapeBarrierKind.None || command.Barrier != LtfsTapeBarrierKind.None)
            return false;

        if (previous.SafeBoundary != command.SafeBoundary || previous.SafeBoundary != LtfsTapeCommandSafeBoundary.Block)
            return false;

        if (!string.Equals(previous.CorrelationId, command.CorrelationId, StringComparison.Ordinal)
            || !string.Equals(previous.CoalesceKey, command.CoalesceKey, StringComparison.Ordinal))
            return false;

        if (previous.ExpectedEndPosition is not null
            && command.ExpectedStartPosition is not null
            && !SamePosition(previous.ExpectedEndPosition, command.ExpectedStartPosition))
            return false;

        var start = previous.ExpectedStartPosition ?? command.ExpectedStartPosition;
        var end = command.ExpectedEndPosition ?? previous.ExpectedEndPosition;
        var combined = previous with
        {
            Kind = previous.Kind == LtfsTapeCommandKind.ReadDataBlock ? LtfsTapeCommandKind.ReadDataRun : LtfsTapeCommandKind.WriteDataRun,
            ExecuteAsync = async ct =>
            {
                await previous.ExecuteAsync(ct).ConfigureAwait(false);
                await command.ExecuteAsync(ct).ConfigureAwait(false);
            },
            ExpectedStartPosition = start,
            ExpectedEndPosition = end,
            LogicalBlockCount = previous.LogicalBlockCount + command.LogicalBlockCount,
            Timeout = CombineTimeout(previous.Timeout, command.Timeout),
            ReadPositionAsync = command.ReadPositionAsync ?? previous.ReadPositionAsync,
        };
        commands[^1] = combined;
        return true;
    }

    private static bool CanCoalesceData(LtfsTapeCommand command) =>
        command.CanCoalesce
        && command.Priority == LtfsTapeCommandPriority.Data
        && command.Kind is LtfsTapeCommandKind.WriteDataBlock or LtfsTapeCommandKind.WriteDataRun or LtfsTapeCommandKind.ReadDataBlock or LtfsTapeCommandKind.ReadDataRun;

    private static TimeSpan? CombineTimeout(TimeSpan? left, TimeSpan? right)
    {
        if (left is null)
            return right;
        if (right is null)
            return left;
        return left.Value + right.Value;
    }

    private static bool SamePosition(LtfsTapePosition left, LtfsTapePosition right) =>
        left.Partition == right.Partition && left.Block == right.Block;

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
        var firstDataIndex = commands.FindIndex(x => x.Priority == LtfsTapeCommandPriority.Data);
        if (firstDataIndex >= 0)
        {
            var bestBeforeDataIndex = -1;
            var bestBeforeDataRank = int.MaxValue;
            for (var i = 0; i < firstDataIndex; i++)
            {
                var rank = PriorityRank(commands[i]);
                if (rank >= bestBeforeDataRank)
                    continue;

                bestBeforeDataRank = rank;
                bestBeforeDataIndex = i;
            }

            return bestBeforeDataIndex >= 0 ? bestBeforeDataIndex : firstDataIndex;
        }

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
        if (command.Kind == LtfsTapeCommandKind.AbortAfterCurrentCommand)
            return 0;

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

    public LtfsTapePosition? ExpectedPosition { get; private set; }

    public LtfsTapePosition? RealPosition { get; private set; }

    public LtfsTapePositionKnowledge PositionKnowledge { get; private set; } = LtfsTapePositionKnowledge.Unknown;

    public Guid? LastCommandId { get; private set; }

    public bool Buffered { get; private set; }

    public bool PositionUncertain { get; private set; }

    public string? PositionUncertaintyReason { get; private set; }

    public bool PositionKnown => PositionKnowledge != LtfsTapePositionKnowledge.Unknown;

    public LtfsTapeCommandExecutorSnapshot Snapshot(LtfsTapeCommandQueue queue, LtfsTapeSessionControl? control = null) =>
        new(
            State,
            queue.Count,
            control?.PauseRequested ?? false,
            control?.CancelRequested ?? false,
            control?.CancelMode,
            ExpectedPosition,
            RealPosition,
            PositionKnowledge,
            LastCommandId,
            Buffered,
            PositionUncertain,
            PositionUncertaintyReason);

    public void SetExpectedPosition(LtfsTapePosition position)
    {
        ExpectedPosition = position;
        RealPosition = position;
        PositionKnowledge = LtfsTapePositionKnowledge.Verified;
        PositionUncertain = false;
        PositionUncertaintyReason = null;
        State = LtfsTapeCommandExecutorState.Positioned;
    }

    public void MarkBuffered(string? reason = null)
    {
        Buffered = true;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            PositionUncertain = true;
            PositionUncertaintyReason = reason;
        }
    }

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

            if (command.Kind == LtfsTapeCommandKind.AbortAfterCurrentCommand)
            {
                control?.RequestCancel(LtfsCancelMode.AbortAfterCurrentCommand);
                break;
            }

            if (control?.PauseRequested == true)
            {
                State = LtfsTapeCommandExecutorState.Paused;
                control.WaitIfPaused(cancellationToken);
            }

            if (command.Kind == LtfsTapeCommandKind.PauseAtBoundary)
            {
                State = LtfsTapeCommandExecutorState.Paused;
                results.Add(new LtfsTapeCommandResult(command, true, State: State));
                continue;
            }

            if (command.Kind == LtfsTapeCommandKind.Resume)
            {
                control?.Resume();
                State = ExpectedPosition is null ? LtfsTapeCommandExecutorState.Reserved : LtfsTapeCommandExecutorState.Positioned;
                results.Add(new LtfsTapeCommandResult(command, true, State: State));
                continue;
            }

            if (command.Kind == LtfsTapeCommandKind.SoftCancel)
                control?.RequestCancel(command.CancellationMode == LtfsTapeCommandCancellationMode.AbortAfterCurrentCommand ? LtfsCancelMode.AbortAfterCurrentCommand : LtfsCancelMode.SoftAfterBlock);

            if (control?.CancelRequested == true
                && command.Priority == LtfsTapeCommandPriority.Data
                && command.Barrier == LtfsTapeBarrierKind.None)
                break;

            try
            {
                ValidateCanExecute(command);
                ValidateExpectedStart(command);
                State = GetExecutionState(command);
                await ExecuteCommandWithTimeoutAsync(command, cancellationToken).ConfigureAwait(false);
                AdvanceExpectedPosition(command);
                LastCommandId = command.CommandId;
                Buffered = command.Kind is LtfsTapeCommandKind.WriteDataBlock or LtfsTapeCommandKind.WriteDataRun;
                State = command.Barrier == LtfsTapeBarrierKind.SessionBarrier ? LtfsTapeCommandExecutorState.Reserved : LtfsTapeCommandExecutorState.Positioned;
                results.Add(new LtfsTapeCommandResult(command, true, State: State));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var outcome = LtfsTapeScsiOutcomeClassifier.Classify(ex);
                await ReconcilePositionAfterFailureAsync(command, cancellationToken).ConfigureAwait(false);
                State = LtfsTapeCommandExecutorState.Faulted;
                results.Add(new LtfsTapeCommandResult(command, false, ex, outcome, State));
                throw;
            }
        }

        if (State != LtfsTapeCommandExecutorState.Faulted)
            State = control?.CancelRequested == true ? LtfsTapeCommandExecutorState.Faulted : LtfsTapeCommandExecutorState.Completed;
        return results;
    }

    private void ValidateCanExecute(LtfsTapeCommand command)
    {
        if (command.Priority == LtfsTapeCommandPriority.Data && PositionUncertain)
        {
            State = LtfsTapeCommandExecutorState.RecoveringPosition;
            throw new InvalidOperationException("Cannot execute LTFS data command while tape position is uncertain.");
        }
    }

    private static async ValueTask ExecuteCommandWithTimeoutAsync(LtfsTapeCommand command, CancellationToken cancellationToken)
    {
        if (command.Timeout is null)
        {
            await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        using var timeout = new CancellationTokenSource(command.Timeout.Value);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await command.ExecuteAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"LTFS tape command {command.Kind} timed out after {command.Timeout.Value}.");
        }
    }

    private void ValidateExpectedStart(LtfsTapeCommand command)
    {
        if (command.ExpectedStartPosition is null)
            return;

        if (ExpectedPosition is null)
        {
            ExpectedPosition = command.ExpectedStartPosition;
            return;
        }

        if (!SamePosition(ExpectedPosition, command.ExpectedStartPosition))
        {
            State = LtfsTapeCommandExecutorState.RecoveringPosition;
            throw new InvalidOperationException(
                $"LTFS tape command {command.Kind} expected start {Format(command.ExpectedStartPosition)}, but executor expected {Format(ExpectedPosition)}.");
        }
    }

    private void AdvanceExpectedPosition(LtfsTapeCommand command)
    {
        if (!command.AffectsPosition)
            return;

        if (command.ExpectedEndPosition is not null)
        {
            ExpectedPosition = command.ExpectedEndPosition;
            RealPosition = command.ExpectedEndPosition;
            PositionKnowledge = LtfsTapePositionKnowledge.ExpectedOnly;
            PositionUncertain = false;
            PositionUncertaintyReason = null;
            return;
        }

        ExpectedPosition = command.Kind switch
        {
            LtfsTapeCommandKind.LocateBlock when command.ExpectedStartPosition is { } start => start,
            LtfsTapeCommandKind.LocateEod when command.ExpectedStartPosition is { } start => start,
            LtfsTapeCommandKind.LocateFilemark when command.ExpectedStartPosition is { } start => start,
            LtfsTapeCommandKind.WriteDataBlock when ExpectedPosition is { } current => current with { Block = current.Block + 1 },
            LtfsTapeCommandKind.WriteDataRun when ExpectedPosition is { } current => current with { Block = current.Block + (ulong)Math.Max(command.LogicalBlockCount, 0) },
            LtfsTapeCommandKind.ReadDataBlock when ExpectedPosition is { } current => current with { Block = current.Block + 1 },
            LtfsTapeCommandKind.ReadDataRun when ExpectedPosition is { } current => current with { Block = current.Block + (ulong)Math.Max(command.LogicalBlockCount, 0) },
            LtfsTapeCommandKind.WriteFilemark when ExpectedPosition is { } current => current with { Block = current.Block + 1 },
            LtfsTapeCommandKind.Rewind => ExpectedPosition is { } current ? current with { Block = 0, FileNumber = 0 } : ExpectedPosition,
            _ => ExpectedPosition,
        };
        RealPosition = ExpectedPosition;
        PositionKnowledge = ExpectedPosition is null ? LtfsTapePositionKnowledge.Unknown : LtfsTapePositionKnowledge.ExpectedOnly;
        PositionUncertain = false;
        PositionUncertaintyReason = null;
    }

    private async ValueTask ReconcilePositionAfterFailureAsync(LtfsTapeCommand command, CancellationToken cancellationToken)
    {
        State = LtfsTapeCommandExecutorState.RecoveringPosition;
        if (command.ReadPositionAsync is null)
        {
            ExpectedPosition = null;
            RealPosition = null;
            PositionKnowledge = LtfsTapePositionKnowledge.Unknown;
            PositionUncertain = true;
            PositionUncertaintyReason = "No READ POSITION command was available after failure.";
            return;
        }

        try
        {
            RealPosition = await command.ReadPositionAsync(cancellationToken).ConfigureAwait(false);
            ExpectedPosition = RealPosition;
            PositionKnowledge = LtfsTapePositionKnowledge.Verified;
            PositionUncertain = false;
            PositionUncertaintyReason = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ExpectedPosition = null;
            RealPosition = null;
            PositionKnowledge = LtfsTapePositionKnowledge.Unknown;
            PositionUncertain = true;
            PositionUncertaintyReason = "READ POSITION failed after command failure.";
        }
    }

    private static bool SamePosition(LtfsTapePosition left, LtfsTapePosition right)
    {
        return left.Partition == right.Partition && left.Block == right.Block;
    }

    private static string Format(LtfsTapePosition position)
    {
        return $"{position.Partition}{position.Block}";
    }

    private static LtfsTapeCommandExecutorState GetExecutionState(LtfsTapeCommand command)
    {
        return command.Kind switch
        {
            LtfsTapeCommandKind.ReadDataBlock or LtfsTapeCommandKind.ReadDataRun or LtfsTapeCommandKind.WriteDataBlock or LtfsTapeCommandKind.WriteDataRun => LtfsTapeCommandExecutorState.WritingData,
            LtfsTapeCommandKind.RefreshIndexPartition => LtfsTapeCommandExecutorState.RefreshingIndexPartition,
            LtfsTapeCommandKind.WriteVolumeCoherencyInformation or LtfsTapeCommandKind.WriteMamAttributes => LtfsTapeCommandExecutorState.CheckpointBarrier,
            LtfsTapeCommandKind.ReadWriteErrorCounters or LtfsTapeCommandKind.RefreshCapacity or LtfsTapeCommandKind.ReadTapeAlert or LtfsTapeCommandKind.ReadVolumeStatistics or LtfsTapeCommandKind.ReadDataCompression or LtfsTapeCommandKind.ReadTapeCapacity or LtfsTapeCommandKind.ReadTemperature or LtfsTapeCommandKind.ReadDeviceStatus => LtfsTapeCommandExecutorState.HealthBarrier,
            LtfsTapeCommandKind.LoadUnload or LtfsTapeCommandKind.SetEncryption or LtfsTapeCommandKind.ClearEncryption or LtfsTapeCommandKind.SetBlockSize or LtfsTapeCommandKind.ForceFlushReload => LtfsTapeCommandExecutorState.FlushReloadBarrier,
            LtfsTapeCommandKind.AllowRemoval or LtfsTapeCommandKind.ReleaseDrive => LtfsTapeCommandExecutorState.Finalizing,
            LtfsTapeCommandKind.FormatMedium or LtfsTapeCommandKind.ConfigurePartition or LtfsTapeCommandKind.SetCapacity or LtfsTapeCommandKind.ForceCheckpoint => LtfsTapeCommandExecutorState.CheckpointBarrier,
            _ => command.Barrier == LtfsTapeBarrierKind.SessionBarrier ? LtfsTapeCommandExecutorState.Reserved : LtfsTapeCommandExecutorState.Positioned,
        };
    }
}

public static class LtfsTapeScsiOutcomeClassifier
{
    public static LtfsTapeCommandOutcomeKind Classify(Exception exception)
    {
        if (exception is TimeoutException)
            return LtfsTapeCommandOutcomeKind.TimeoutOrTransport;

        if (exception is LtfsScsiCommandException scsi)
        {
            if (!scsi.TransportOk)
                return LtfsTapeCommandOutcomeKind.TimeoutOrTransport;
            if (scsi.WriteProtected)
                return LtfsTapeCommandOutcomeKind.WriteProtected;
            if (scsi.VolumeOverflow)
                return LtfsTapeCommandOutcomeKind.VolumeOverflow;
            if (scsi.EarlyWarningEndOfMedium)
                return LtfsTapeCommandOutcomeKind.EarlyWarningEndOfMedium;
            if (scsi.EndOfMedium)
                return LtfsTapeCommandOutcomeKind.EndOfMedium;

            return scsi.SenseKey switch
            {
                0x02 => LtfsTapeCommandOutcomeKind.NotReady,
                0x06 => LtfsTapeCommandOutcomeKind.UnitAttention,
                0x03 or 0x04 => LtfsTapeCommandOutcomeKind.MediumOrHardwareError,
                _ => LtfsTapeCommandOutcomeKind.UnknownFailure,
            };
        }

        if (exception is IOException)
            return LtfsTapeCommandOutcomeKind.TimeoutOrTransport;

        return LtfsTapeCommandOutcomeKind.UnknownFailure;
    }

    public static LtfsWriterRecoveryAction ToWriterRecoveryAction(
        LtfsTapeCommandOutcomeKind outcome,
        LtfsTapePosition? expectedPosition,
        LtfsTapePosition? realPosition)
    {
        return outcome switch
        {
            LtfsTapeCommandOutcomeKind.RetryableNoPositionChange => LtfsWriterRecoveryAction.Retry,
            LtfsTapeCommandOutcomeKind.CommittedPositionAdvanced => LtfsWriterRecoveryAction.Ignore,
            LtfsTapeCommandOutcomeKind.EarlyWarningEndOfMedium => LtfsWriterRecoveryAction.CheckpointThenAbort,
            LtfsTapeCommandOutcomeKind.NotReady or LtfsTapeCommandOutcomeKind.UnitAttention => LtfsWriterRecoveryAction.ReloadThenRetry,
            LtfsTapeCommandOutcomeKind.PositionUnknown => LtfsWriterRecoveryAction.Abort,
            _ when expectedPosition is not null
                && realPosition is not null
                && expectedPosition.Partition == realPosition.Partition
                && realPosition.Block == expectedPosition.Block => LtfsWriterRecoveryAction.Retry,
            _ when expectedPosition is not null
                && realPosition is not null
                && expectedPosition.Partition == realPosition.Partition
                && realPosition.Block > expectedPosition.Block => LtfsWriterRecoveryAction.Ignore,
            _ => LtfsWriterRecoveryAction.Abort,
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
