using System.Buffers;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Threading.Channels;

using Blake3;

using Koko.Core.Events;
using Koko.Core.Scsi;
using Koko.Core.Scsi.Commands;

using Serilog;

namespace Koko.Core.Ltfs;

public enum LtfsWriterStepKind
{
    Started,
    Preflight,
    LocateWritePosition,
    WriteFileStarted,
    WriteBlock,
    WriteFileCompleted,
    WriteDataPartitionIndex,
    RefreshIndexPartition,
    WriteVci,
    HealthPolicy,
    ReadStarted,
    ReadCompleted,
    RollbackStarted,
    RollbackCompleted,
    Completed,
    Warning,
    Failed
}

public sealed record LtfsWriterStepEvent(
    string OperationId,
    LtfsWriterStepKind Step,
    string Message,
    long? BytesProcessed = null,
    long? TotalBytes = null,
    long? FilesProcessed = null,
    long? TotalFiles = null,
    DateTimeOffset? TimestampOverride = null) : IKokoEvent
{
    public DateTimeOffset Timestamp { get; } = TimestampOverride ?? DateTimeOffset.UtcNow;
}

public sealed record LtfsWriteHealthPolicyEvent(
    string OperationId,
    string Reason,
    double CurrentSpeedMiBPerSecond,
    double? ErrorRate,
    int ReloadCount,
    LtfsWriteHealthAction Action,
    DateTimeOffset? TimestampOverride = null) : IKokoEvent
{
    public DateTimeOffset Timestamp { get; } = TimestampOverride ?? DateTimeOffset.UtcNow;
}

public enum LtfsWriterErrorDecision
{
    Abort,
    Retry,
    Ignore
}

public sealed record LtfsWriterErrorContext(
    string OperationId,
    LtfsWriterStepKind Step,
    string Message,
    Exception Exception,
    int Attempt);

public enum LtfsHashAlgorithmKind
{
    Blake3,
    Sha512,
    Sha256,
    XxHash128,
    XxHash64,
    Sha1,
    Md5,
    Crc32
}

public sealed record LtfsHashOptions(
    bool Blake3 = true,
    bool Sha512 = true,
    bool Sha256 = true,
    bool XxHash128 = true,
    bool XxHash64 = true,
    bool Sha1 = true,
    bool Md5 = true,
    bool Crc32 = true)
{
    public static LtfsHashOptions All { get; } = new();
    public static LtfsHashOptions None { get; } = new(false, false, false, false, false, false, false, false);

    public bool AnyEnabled => Blake3 || Sha512 || Sha256 || XxHash128 || XxHash64 || Sha1 || Md5 || Crc32;

    public bool IsEnabled(LtfsHashAlgorithmKind algorithm) => algorithm switch
    {
        LtfsHashAlgorithmKind.Blake3 => Blake3,
        LtfsHashAlgorithmKind.Sha512 => Sha512,
        LtfsHashAlgorithmKind.Sha256 => Sha256,
        LtfsHashAlgorithmKind.XxHash128 => XxHash128,
        LtfsHashAlgorithmKind.XxHash64 => XxHash64,
        LtfsHashAlgorithmKind.Sha1 => Sha1,
        LtfsHashAlgorithmKind.Md5 => Md5,
        LtfsHashAlgorithmKind.Crc32 => Crc32,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };
}

public sealed record LtfsAutoReloadPolicyOptions(
    bool Enabled = true,
    double LowSpeedMiBPerSecond = 60,
    double HighSpeedMiBPerSecond = 87,
    TimeSpan? SustainedDuration = null,
    double ErrorRateThreshold = -3.7,
    TimeSpan? Cooldown = null,
    TimeSpan? FlushCooldown = null,
    int? MaxReloadCount = null,
    int CleanReloadEvery = 3,
    int? ReloadAfterFlushCount = null,
    bool CheckpointBeforeReload = true)
{
    public TimeSpan EffectiveSustainedDuration => SustainedDuration ?? TimeSpan.FromSeconds(3);

    public TimeSpan EffectiveCooldown => Cooldown ?? TimeSpan.FromSeconds(300);

    public TimeSpan EffectiveFlushCooldown => FlushCooldown ?? TimeSpan.Zero;

    public int EffectiveReloadAfterFlushCount => ReloadAfterFlushCount ?? CleanReloadEvery;
}

public sealed record LtfsThrottlePolicyOptions(
    bool Enabled = false,
    double LimitMiBPerSecond = 0,
    TimeSpan? WindowDuration = null,
    TimeSpan? DelayGranularity = null)
{
    public TimeSpan EffectiveWindowDuration => WindowDuration ?? TimeSpan.FromMilliseconds(200);

    public TimeSpan EffectiveDelayGranularity => DelayGranularity ?? TimeSpan.FromMilliseconds(10);
}

public sealed record LtfsHealthSampleContext(
    string OperationId,
    long TotalBytesWritten,
    long BytesSinceLastSample,
    TimeSpan ElapsedSinceLastSample,
    CancellationToken CancellationToken);

public sealed record LtfsHealthSamplingOptions(
    bool SampleAfterFile = true,
    long? LargeFileByteInterval = null,
    TimeSpan? LargeFileTimeInterval = null,
    LogPageCode? LogPage = null,
    Func<ILtfsWriterDevice, LtfsHealthSampleContext, CancellationToken, ValueTask<double?>>? CustomSampler = null)
{
    public LogPageCode EffectiveLogPage => LogPage ?? LogPageCode.WriteErrorCounters;
}

public enum LtfsWriteHealthAction
{
    Continue,
    Flush,
    Reload,
    PendingReload,
    CleanReload,
    Abort
}

public enum LtfsWriteCompletionKind
{
    Completed,
    StoppedAtEndOfMedium,
    SoftCanceled,
    Aborted
}

public enum LtfsDirtyAppendPolicy
{
    Abort,
    AllowWithWarning
}

public sealed record LtfsAppendValidationOptions(
    bool Enabled = false,
    LtfsDirtyAppendPolicy DirtyAppendPolicy = LtfsDirtyAppendPolicy.Abort);

public sealed record LtfsEomPolicyOptions(
    bool Enabled = true,
    bool CheckpointAtSafeBoundary = true,
    bool ExportRemainingManifest = true,
    bool MultiVolumeEnabled = false);

public sealed record LtfsWormPolicyOptions(
    bool AutoDetect = true,
    bool FailClosedWhenInconclusive = true,
    bool AllowCorrectiveRollback = false,
    bool AllowVciFailureWarning = true);

public sealed record LtfsWriteHealthDecision(
    LtfsWriteHealthAction Action,
    string Reason,
    double CurrentSpeedMiBPerSecond,
    double? ErrorRate,
    int ReloadCount)
{
    public static LtfsWriteHealthDecision Continue(double speedMiBPerSecond, double? errorRate, int reloadCount) =>
        new(LtfsWriteHealthAction.Continue, "Health sample is within policy.", speedMiBPerSecond, errorRate, reloadCount);
}

public sealed record LtfsWriterOptions(
    long BlockSizeBytes = 512 * 1024,
    long MemoryCacheLimitBytes = LtfsWriterOptions.DefaultMemoryCacheLimitBytes,
    LtfsCheckpointPolicy? CheckpointPolicy = null,
    bool WriteDataPartitionIndexOnComplete = true,
    bool RefreshIndexPartitionOnComplete = true,
    bool WriteVci = true,
    bool ComputeHashes = false,
    LtfsHashOptions? Hashes = null,
    bool KeepUnwrittenFilesOnAbort = true,
    int SourceReadBufferBytes = 4 * 1024 * 1024,
    long? SmallFileThresholdBytes = null,
    double WriteStartWatermarkRatio = 0.75,
    double WriteStopWatermarkRatio = 0.25,
    LtfsSourceChangePolicy SourceChangePolicy = LtfsSourceChangePolicy.UpdateBeforeWrite,
    LtfsAutoReloadPolicyOptions? AutoReloadPolicy = null,
    LtfsThrottlePolicyOptions? ThrottlePolicy = null,
    LtfsHealthSamplingOptions? HealthSampling = null,
    LtfsEncryptionOptions? Encryption = null,
    LtfsAutosaveOptions? Autosave = null,
    LtfsAppendValidationOptions? AppendValidation = null,
    LtfsEomPolicyOptions? EomPolicy = null,
    LtfsWormPolicyOptions? WormPolicy = null,
    LtfsCapacityPolicyOptions? CapacityPolicy = null,
    LtfsDedupOptions? Dedup = null,
    LtfsVolumeDiscoveryResult? Discovery = null,
    LtfsTapeSessionControl? TapeControl = null,
    Func<LtfsWriterPolicyContext, CancellationToken, ValueTask<LtfsWriterPolicyDecision>>? PolicyHandler = null,
    Func<LtfsWriterErrorContext, CancellationToken, ValueTask<LtfsWriterErrorDecision>>? ErrorHandler = null)
{
    public const long MinimumMemoryCacheLimitBytes = 256L * 1024 * 1024;
    public const long DefaultMemoryCacheLimitBytes = 512L * 1024 * 1024;
    public const long MaximumMemoryCacheLimitBytes = 100L * 1024 * 1024 * 1024;
}

public sealed record LtfsWriteSource(
    string Name,
    long Length,
    Func<CancellationToken, ValueTask<Stream>> OpenReadAsync,
    DateTimeOffset CreationTime,
    DateTimeOffset ModifyTime,
    DateTimeOffset AccessTime,
    bool ReadOnly = false,
    string? SourcePath = null,
    string? DestinationPath = null,
    long? InitialLength = null,
    DateTimeOffset? InitialModifyTime = null)
{
    public static LtfsWriteSource FromFile(string path, string? name = null, int sourceReadBufferBytes = 4 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (sourceReadBufferBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceReadBufferBytes));

        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("LTFS write source file does not exist.", path);

        return new LtfsWriteSource(
            string.IsNullOrWhiteSpace(name) ? info.Name : name,
            info.Length,
            _ => ValueTask.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, sourceReadBufferBytes, FileOptions.SequentialScan)),
            info.CreationTimeUtc,
            info.LastWriteTimeUtc,
            info.LastAccessTimeUtc,
            info.IsReadOnly,
            SourcePath: path,
            DestinationPath: string.IsNullOrWhiteSpace(name) ? info.Name : name,
            InitialLength: info.Length,
            InitialModifyTime: info.LastWriteTimeUtc);
    }
}

public sealed record LtfsDedupOptions(
    bool Enabled = false,
    LtfsHashAlgorithmKind Algorithm = LtfsHashAlgorithmKind.Sha1);

public enum LtfsExtractConflictPolicy
{
    Overwrite,
    Skip,
    Fail,
    SkipIfSameLengthAndTimestamp,
    RenameWithSuffix
}

public enum LtfsSymlinkRestorePolicy
{
    Skip,
    CreateSymlink,
    WriteTextReport
}

public enum LtfsTargetWriteErrorPolicy
{
    Abort,
    SkipFileAndContinue
}

public sealed record LtfsExtractOptions(
    LtfsExtractConflictPolicy ConflictPolicy = LtfsExtractConflictPolicy.Overwrite,
    string? StagingDirectory = null,
    bool KeepPartial = false,
    bool RestoreTimestamps = true,
    bool RestoreReadOnly = true,
    bool RetryHashMismatchOnce = true,
    LtfsSymlinkRestorePolicy SymlinkPolicy = LtfsSymlinkRestorePolicy.Skip,
    LtfsTargetWriteErrorPolicy TargetWriteErrorPolicy = LtfsTargetWriteErrorPolicy.Abort);

public sealed record LtfsWriteRequest(
    LtfsIndex Index,
    LtfsDirectory TargetDirectory,
    IReadOnlyList<LtfsWriteSource> Sources,
    LtfsWriterOptions? Options = null,
    LtfsLabel? Label = null,
    bool OverwriteExisting = false,
    bool DryRun = false);

public sealed record LtfsWriteResult(
    LtfsIndex Index,
    long BytesWritten,
    long FilesWritten,
    bool DataPartitionIndexWritten,
    bool IndexPartitionRefreshed,
    bool VciWritten,
    bool DryRun,
    LtfsWriteCompletionKind CompletionKind = LtfsWriteCompletionKind.Completed,
    LtfsRemainingManifest? RemainingManifest = null,
    IReadOnlyList<string>? RemainingManifestArchivePaths = null,
    LtfsIndex? LastStableIndex = null);

public sealed record LtfsRollbackRequest(
    LtfsIndex CurrentIndex,
    LtfsWriterOptions? Options = null,
    bool DryRun = false);

public sealed record LtfsRollbackResult(
    LtfsIndex Index,
    LtfsLocation RolledBackFrom,
    LtfsLocation RolledBackTo,
    bool DryRun);

public sealed record LtfsExtractRequest(
    IReadOnlyList<LtfsReadTarget> Targets,
    LtfsWriterOptions? Options = null,
    bool DryRun = false,
    LtfsExtractOptions? ExtractOptions = null);

public enum LtfsExtractVerificationStatus
{
    NotRequested,
    Verified,
    NoExpectedHash,
    Mismatch,
    Skipped
}

public enum LtfsExtractFileStatus
{
    Pending,
    Extracted,
    VerifiedOnly,
    Skipped,
    Failed
}

public sealed record LtfsExtractFileResult(
    long FileUid,
    string FileName,
    string DestinationPath,
    LtfsReadOperation Operation,
    LtfsExtractVerificationStatus VerificationStatus,
    IReadOnlyList<LtfsHashAlgorithmKind> VerifiedAlgorithms,
    LtfsExtractFileStatus ExtractStatus = LtfsExtractFileStatus.Pending,
    string? Message = null);

public sealed record LtfsExtractResult(
    long BytesRead,
    long FilesRead,
    LtfsSequentialReadPlan Plan,
    bool DryRun,
    IReadOnlyList<LtfsExtractFileResult>? FileResults = null);

public enum LtfsHashMaintenanceMode
{
    VerifyOnly,
    ExtractOnly,
    ExtractAndVerify,
    UpdateOnly
}

public enum LtfsHashUpdateStatus
{
    NotRequested,
    Updated,
    VerifiedExisting,
    NoEnabledHash,
    Mismatch,
    Skipped
}

public sealed record LtfsHashMaintenanceRequest(
    LtfsIndex Index,
    IReadOnlyList<LtfsReadTarget> Targets,
    LtfsHashMaintenanceMode Mode,
    LtfsWriterOptions? Options = null,
    bool DryRun = false,
    LtfsExtractOptions? ExtractOptions = null);

public sealed record LtfsHashMaintenanceFileResult(
    long FileUid,
    string FileName,
    LtfsHashMaintenanceMode Mode,
    LtfsHashUpdateStatus UpdateStatus,
    LtfsExtractVerificationStatus VerificationStatus,
    LtfsExtractFileStatus ExtractStatus,
    IReadOnlyList<LtfsHashAlgorithmKind> Algorithms,
    string? Message = null);

public sealed record LtfsHashMaintenanceResult(
    LtfsIndex Index,
    long BytesRead,
    long FilesRead,
    bool DataPartitionIndexWritten,
    bool IndexPartitionRefreshed,
    bool VciWritten,
    bool DryRun,
    LtfsSequentialReadPlan Plan,
    IReadOnlyList<LtfsHashMaintenanceFileResult> FileResults);

public interface ILtfsWriterDevice : ILtfsBlockReader
{
    ValueTask ReserveAsync(CancellationToken cancellationToken = default);

    ValueTask ReleaseAsync(CancellationToken cancellationToken = default);

    ValueTask PreventRemovalAsync(bool prevent, CancellationToken cancellationToken = default);

    ValueTask TestUnitReadyAsync(CancellationToken cancellationToken = default);

    ValueTask SetBlockSizeAsync(long blockSizeBytes, CancellationToken cancellationToken = default);

    ValueTask LocateAsync(LtfsPartition partition, ulong block, CancellationToken cancellationToken = default);

    ValueTask LocateEndOfDataAsync(LtfsPartition partition, CancellationToken cancellationToken = default);

    ValueTask LocateFilemarkAsync(LtfsPartition partition, ulong filemark, CancellationToken cancellationToken = default);

    ValueTask<LtfsTapePosition> ReadPositionAsync(CancellationToken cancellationToken = default);

    ValueTask<byte[]> ReadBlockAsync(long maximumBytes, CancellationToken cancellationToken = default);

    ValueTask AdvancePastFilemarkAsync(CancellationToken cancellationToken = default);

    ValueTask<byte[]> ReadToFilemarkAsync(long blockSizeBytes, CancellationToken cancellationToken = default);

    ValueTask WriteBlockAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    ValueTask WriteFilemarksAsync(uint count, CancellationToken cancellationToken = default);

    ValueTask WriteVciAsync(ulong generation, ulong? indexPartitionBlock, ulong dataPartitionBlock, Guid volumeUuid, CancellationToken cancellationToken = default);

    ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        return WriteFilemarksAsync(0, cancellationToken);
    }

    ValueTask LoadUnloadAsync(bool load, CancellationToken cancellationToken = default)
    {
        _ = load;
        _ = cancellationToken;
        throw new NotSupportedException("LOAD/UNLOAD is not supported by this LTFS writer device.");
    }

    ValueTask<LogSenseResponse> ReadLogSenseAsync(LogPageCode pageCode, CancellationToken cancellationToken = default)
    {
        _ = pageCode;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(LogSenseResponse.FromRaw(Array.Empty<byte>()));
    }
}

public sealed class LtfsWriterException : Exception
{
    public LtfsWriterException(string message) : base(message)
    {
    }

    public LtfsWriterException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class LtfsSlidingThroughputLimiter
{
    private readonly LtfsThrottlePolicyOptions options;
    private readonly Queue<WriteSample> samples = [];

    public LtfsSlidingThroughputLimiter(LtfsThrottlePolicyOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask DelayBeforeWriteAsync(int byteCount, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || options.LimitMiBPerSecond <= 0 || byteCount <= 0)
            return;

        var window = options.EffectiveWindowDuration;
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Throttle window duration must be greater than zero.");

        var limitBytesPerSecond = options.LimitMiBPerSecond * 1024d * 1024d;
        var windowCapacityBytes = limitBytesPerSecond * window.TotalSeconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            Trim(now, window);

            var projectedBytes = samples.Sum(x => x.ByteCount) + byteCount;
            var projectedRate = projectedBytes / window.TotalSeconds;
            if (projectedRate <= limitBytesPerSecond)
            {
                samples.Enqueue(new WriteSample(now, byteCount));
                return;
            }

            if (samples.Count == 0 && byteCount > windowCapacityBytes)
            {
                var waitForOversizedWrite = TimeSpan.FromSeconds(byteCount / limitBytesPerSecond - window.TotalSeconds);
                if (waitForOversizedWrite > TimeSpan.Zero)
                    await Task.Delay(waitForOversizedWrite, cancellationToken).ConfigureAwait(false);

                samples.Enqueue(new WriteSample(DateTimeOffset.UtcNow, byteCount));
                return;
            }

            var oldest = samples.Count > 0 ? samples.Peek().Timestamp : now;
            var wait = oldest + window - now;
            if (wait <= TimeSpan.Zero)
                wait = options.EffectiveDelayGranularity;
            if (options.EffectiveDelayGranularity > TimeSpan.Zero && wait > options.EffectiveDelayGranularity)
                wait = options.EffectiveDelayGranularity;

            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Trim(DateTimeOffset now, TimeSpan window)
    {
        while (samples.Count > 0 && now - samples.Peek().Timestamp >= window)
            samples.Dequeue();
    }

    private readonly record struct WriteSample(DateTimeOffset Timestamp, int ByteCount);
}

public sealed class LtfsWriteErrorRateSampler
{
    private readonly ILtfsWriterDevice device;
    private readonly LtfsHealthSamplingOptions options;
    private ulong? previousCounter;
    private long previousBytes;

    public LtfsWriteErrorRateSampler(ILtfsWriterDevice device, LtfsHealthSamplingOptions options)
    {
        this.device = device ?? throw new ArgumentNullException(nameof(device));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<double?> SampleAsync(LtfsHealthSampleContext context, CancellationToken cancellationToken)
    {
        if (options.CustomSampler is not null)
            return await options.CustomSampler(device, context, cancellationToken).ConfigureAwait(false);

        var response = await device.ReadLogSenseAsync(options.EffectiveLogPage, cancellationToken).ConfigureAwait(false);
        var current = SumNumericParameters(response.Parameters);
        if (previousCounter is null)
        {
            previousCounter = current;
            previousBytes = context.TotalBytesWritten;
            return null;
        }

        var errorDelta = current >= previousCounter.Value ? current - previousCounter.Value : current;
        var byteDelta = Math.Max(0, context.TotalBytesWritten - previousBytes);
        previousCounter = current;
        previousBytes = context.TotalBytesWritten;

        if (byteDelta == 0 || errorDelta == 0)
            return double.NegativeInfinity;

        return Math.Log10(errorDelta / (double)byteDelta);
    }

    private static ulong SumNumericParameters(IReadOnlyList<LogParameter> parameters)
    {
        ulong total = 0;
        foreach (var parameter in parameters)
        {
            var span = parameter.Value.Span;
            ulong value = 0;
            for (var i = 0; i < span.Length && i < sizeof(ulong); i++)
                value = (value << 8) | span[i];
            total += value;
        }

        return total;
    }
}

public sealed class LtfsWriteHealthMonitor
{
    private readonly LtfsAutoReloadPolicyOptions reloadOptions;
    private readonly LtfsWriteErrorRateSampler sampler;
    private DateTimeOffset lastSampleTime = DateTimeOffset.UtcNow;
    private long lastSampleBytes;
    private DateTimeOffset? inBandSince;
    private DateTimeOffset? lastFlushTime;
    private DateTimeOffset? lastReloadTime;
    private int flushCount;
    private int reloadCount;
    private LtfsWriteHealthDecision? pendingReloadDecision;

    public LtfsWriteHealthMonitor(LtfsAutoReloadPolicyOptions reloadOptions, LtfsWriteErrorRateSampler sampler)
    {
        this.reloadOptions = reloadOptions ?? throw new ArgumentNullException(nameof(reloadOptions));
        this.sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
    }

    public async ValueTask<LtfsWriteHealthDecision> SampleAsync(string operationId, long totalBytesWritten, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = now - lastSampleTime;
        var byteDelta = Math.Max(0, totalBytesWritten - lastSampleBytes);
        var speed = elapsed > TimeSpan.Zero
            ? byteDelta / 1024d / 1024d / elapsed.TotalSeconds
            : 0;

        var context = new LtfsHealthSampleContext(operationId, totalBytesWritten, byteDelta, elapsed, cancellationToken);
        var errorRate = await sampler.SampleAsync(context, cancellationToken).ConfigureAwait(false);
        lastSampleTime = now;
        lastSampleBytes = totalBytesWritten;

        if (!reloadOptions.Enabled)
            return LtfsWriteHealthDecision.Continue(speed, errorRate, reloadCount);

        var speedInBand = speed >= reloadOptions.LowSpeedMiBPerSecond && speed <= reloadOptions.HighSpeedMiBPerSecond;
        var errorIsBad = errorRate.HasValue && errorRate.Value >= reloadOptions.ErrorRateThreshold;
        if (!speedInBand || !errorIsBad)
        {
            inBandSince = null;
            return LtfsWriteHealthDecision.Continue(speed, errorRate, reloadCount);
        }

        inBandSince ??= now;
        if (now - inBandSince.Value < reloadOptions.EffectiveSustainedDuration)
            return LtfsWriteHealthDecision.Continue(speed, errorRate, reloadCount);

        if (lastFlushTime is not null && now - lastFlushTime.Value < reloadOptions.EffectiveFlushCooldown)
            return LtfsWriteHealthDecision.Continue(speed, errorRate, reloadCount);

        inBandSince = null;

        return new LtfsWriteHealthDecision(
            LtfsWriteHealthAction.Flush,
            $"Sustained write speed {speed:F2} MiB/s and error rate {errorRate:F4} crossed LTFS capacity-loss flush policy.",
            speed,
            errorRate,
            reloadCount);
    }

    public LtfsWriteHealthDecision? RecordCapacityLossFlushSucceeded(LtfsWriteHealthDecision flushDecision)
    {
        if (flushDecision.Action != LtfsWriteHealthAction.Flush)
            throw new ArgumentException("Only a successful LTFS health flush can be recorded.", nameof(flushDecision));

        lastFlushTime = DateTimeOffset.UtcNow;
        flushCount += 1;
        var reloadAfterFlushCount = reloadOptions.EffectiveReloadAfterFlushCount;
        if (reloadAfterFlushCount <= 0 || flushCount % reloadAfterFlushCount != 0)
            return null;

        if (reloadOptions.MaxReloadCount is not null && reloadCount >= reloadOptions.MaxReloadCount.Value)
        {
            pendingReloadDecision = new LtfsWriteHealthDecision(
                LtfsWriteHealthAction.Abort,
                "LTFS auto reload maximum count was reached.",
                flushDecision.CurrentSpeedMiBPerSecond,
                flushDecision.ErrorRate,
                reloadCount);
            return pendingReloadDecision;
        }

        pendingReloadDecision = new LtfsWriteHealthDecision(
            LtfsWriteHealthAction.PendingReload,
            $"LTFS capacity-loss flush count reached {flushCount}; reload is pending at the next safe boundary.",
            flushDecision.CurrentSpeedMiBPerSecond,
            flushDecision.ErrorRate,
            reloadCount);
        return pendingReloadDecision;
    }

    public LtfsWriteHealthDecision? TryConsumePendingReload()
    {
        if (pendingReloadDecision is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        if (lastReloadTime is not null && now - lastReloadTime.Value < reloadOptions.EffectiveCooldown)
            return null;

        if (pendingReloadDecision.Action == LtfsWriteHealthAction.Abort)
        {
            var abort = pendingReloadDecision;
            pendingReloadDecision = null;
            return abort;
        }

        var pending = pendingReloadDecision;
        reloadCount += 1;
        lastReloadTime = now;
        pendingReloadDecision = null;
        return new LtfsWriteHealthDecision(
            LtfsWriteHealthAction.Reload,
            "LTFS capacity-loss flush count reached reload threshold.",
            pending?.CurrentSpeedMiBPerSecond ?? 0,
            pending?.ErrorRate,
            reloadCount);
    }
}

public sealed class LtfsWriterService
{
    private readonly ILtfsWriterDevice device;
    private readonly IKokoEventBus eventBus;

    public LtfsWriterService(ILtfsWriterDevice device, IKokoEventBus? eventBus = null)
    {
        this.device = device ?? throw new ArgumentNullException(nameof(device));
        this.eventBus = eventBus ?? NullKokoEventBus.Instance;
    }

    public async ValueTask<LtfsWriteResult> WriteFilesAsync(LtfsWriteRequest request, CancellationToken cancellationToken = default)
    {
        using (Log.PushMethod())
        {
            ArgumentNullException.ThrowIfNull(request);
            var options = ResolveOptions(request.Options, request.Label);
            ValidateWriteRequest(request, options);

            var operationId = Guid.NewGuid().ToString("N");
            Publish(operationId, LtfsWriterStepKind.Started, $"Writing {request.Sources.Count} LTFS file(s).", totalFiles: request.Sources.Count);
            Log.Information("LTFS write started. OperationId={OperationId}, FileCount={FileCount}, DryRun={DryRun}", operationId, request.Sources.Count, request.DryRun);

            var index = request.Index.Clone();
            var targetDirectory = FindDirectoryClone(index, request.TargetDirectory.FileUid) ?? throw new LtfsWriterException("Target directory does not exist in the supplied LTFS index.");
            if (request.OverwriteExisting && ((options.Discovery?.Worm ?? false) || index.VolumeLockState == LtfsVolumeLockState.PermLocked))
                throw new LtfsWriterException("WORM LTFS append cannot overwrite existing files.");
            if (request.DryRun)
                return new LtfsWriteResult(index, 0, 0, false, false, false, DryRun: true);

            var reserved = false;
            var removalPrevented = false;
            long bytesWritten = 0;
            long filesWritten = 0;
            var dataIndexWritten = false;
            var indexPartitionRefreshed = false;
            var vciWritten = false;
            var counters = new LtfsIndexCounters(0, 0, DateTimeOffset.UtcNow);
            var completionKind = LtfsWriteCompletionKind.Completed;
            LtfsRemainingManifest? remainingManifest = null;
            IReadOnlyList<string>? remainingArtifacts = null;
            var tapeExecutor = new LtfsTapeCommandExecutor();

            try
            {
                await PreflightAsync(operationId, options, tapeExecutor, cancellationToken).ConfigureAwait(false);
                reserved = true;
                removalPrevented = true;

                await ValidateAppendBaselineAsync(operationId, index, request.Label, options, tapeExecutor, cancellationToken).ConfigureAwait(false);
                await LocateToWritePositionAsync(operationId, index, options, tapeExecutor, cancellationToken).ConfigureAwait(false);

                var writeState = await WritePlannedSourcesAsync(
                    operationId,
                    index,
                    targetDirectory,
                    request.Sources,
                    request.OverwriteExisting,
                    options,
                    tapeExecutor,
                    cancellationToken).ConfigureAwait(false);
                index = writeState.Index;
                bytesWritten = writeState.BytesWritten;
                filesWritten = writeState.FilesWritten;
                counters = writeState.Counters;
                dataIndexWritten = writeState.DataPartitionIndexWritten;
                completionKind = writeState.CompletionKind;
                remainingManifest = writeState.RemainingManifest;

                if (options.WriteDataPartitionIndexOnComplete && counters.UnindexedBytes != 0
                    && (completionKind == LtfsWriteCompletionKind.Completed
                        || completionKind == LtfsWriteCompletionKind.SoftCanceled
                        || (options.EomPolicy!.CheckpointAtSafeBoundary && filesWritten > 0)))
                {
                    var reason = completionKind switch
                    {
                        LtfsWriteCompletionKind.StoppedAtEndOfMedium => "checkpoint-eom-data",
                        LtfsWriteCompletionKind.SoftCanceled => "checkpoint-soft-cancel",
                        _ => "checkpoint-final-data",
                    };
                    index = await WriteDataPartitionIndexAsync(operationId, index, options, tapeExecutor, request.Label, request.Sources, reason, cancellationToken).ConfigureAwait(false);
                    dataIndexWritten = true;
                }

                if (completionKind == LtfsWriteCompletionKind.Completed && options.RefreshIndexPartitionOnComplete)
                {
                    index = await RefreshIndexPartitionAsync(operationId, index, options, tapeExecutor, cancellationToken).ConfigureAwait(false);
                    indexPartitionRefreshed = true;
                    vciWritten = options.WriteVci;
                }
                else if (completionKind == LtfsWriteCompletionKind.Completed && options.WriteVci)
                {
                    await WriteVciWithWormPolicyAsync(operationId, index, options, tapeExecutor, cancellationToken).ConfigureAwait(false);
                    vciWritten = true;
                }

                if (remainingManifest is not null && dataIndexWritten)
                    remainingManifest = remainingManifest with { GenerationNumber = index.GenerationNumber, LastStableLocation = index.Location.Clone() };

                var finalReason = completionKind switch
                {
                    LtfsWriteCompletionKind.StoppedAtEndOfMedium => "eom-remaining",
                    LtfsWriteCompletionKind.SoftCanceled => "soft-cancel-remaining",
                    _ => "final",
                };
                remainingArtifacts = await TryExportAutosaveAndReturnAsync(operationId, finalReason, index, request.Label, request.Sources, options, cancellationToken, remainingManifest).ConfigureAwait(false);
                Publish(operationId, LtfsWriterStepKind.Completed, completionKind == LtfsWriteCompletionKind.Completed ? "LTFS write completed." : $"LTFS write finished with {completionKind}.", bytesWritten, bytesWritten, filesWritten, request.Sources.Count);
                Log.Information("LTFS write completed. OperationId={OperationId}, BytesWritten={BytesWritten}, FilesWritten={FilesWritten}", operationId, bytesWritten, filesWritten);
                return new LtfsWriteResult(index, bytesWritten, filesWritten, dataIndexWritten, indexPartitionRefreshed, vciWritten, DryRun: false, completionKind, remainingManifest, remainingArtifacts, index.Clone());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await TryExportAutosaveAsync(operationId, "safe-abort", index, request.Label, request.Sources, options, CancellationToken.None).ConfigureAwait(false);
                PublishFailure(operationId, LtfsWriterStepKind.Failed, "LTFS write failed.", ex);
                throw new LtfsWriterException("LTFS write failed.", ex);
            }
            finally
            {
                await ReleaseDriveAsync(removalPrevented, reserved, options).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask<LtfsRollbackResult> RollbackAsync(LtfsRollbackRequest request, CancellationToken cancellationToken = default)
    {
        using (Log.PushMethod())
        {
            ArgumentNullException.ThrowIfNull(request);
            var options = ResolveOptions(request.Options);
            var operationId = Guid.NewGuid().ToString("N");
            var from = request.CurrentIndex.Location.Clone();
            var to = request.CurrentIndex.PreviousGenerationLocation.Clone();

            if (to.StartBlock == 0 && request.CurrentIndex.GenerationNumber <= 1)
                throw new LtfsWriterException("Current LTFS index does not contain a rollback target.");

            Publish(operationId, LtfsWriterStepKind.RollbackStarted, $"Rollback from {from.Partition}{from.StartBlock} to {to.Partition}{to.StartBlock}.");
            Log.Information("LTFS rollback started. OperationId={OperationId}, From={FromPartition}{FromBlock}, To={ToPartition}{ToBlock}", operationId, from.Partition, from.StartBlock, to.Partition, to.StartBlock);

            if (request.DryRun)
                return new LtfsRollbackResult(request.CurrentIndex.Clone(), from, to, DryRun: true);

            var reserved = false;
            var removalPrevented = false;
            var tapeExecutor = new LtfsTapeCommandExecutor();
            try
            {
                await PreflightAsync(operationId, options, tapeExecutor, cancellationToken).ConfigureAwait(false);
                reserved = true;
                removalPrevented = true;

                var rolledBack = await ReadIndexAtAsync(operationId, to, options, tapeExecutor, cancellationToken).ConfigureAwait(false);
                Publish(operationId, LtfsWriterStepKind.RollbackCompleted, $"Rollback completed at generation {rolledBack.GenerationNumber}.");
                Log.Information("LTFS rollback completed. OperationId={OperationId}, Generation={Generation}", operationId, rolledBack.GenerationNumber);
                return new LtfsRollbackResult(rolledBack, from, to, DryRun: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PublishFailure(operationId, LtfsWriterStepKind.Failed, "LTFS rollback failed.", ex);
                throw new LtfsWriterException("LTFS rollback failed.", ex);
            }
            finally
            {
                await ReleaseDriveAsync(removalPrevented, reserved, options).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask<LtfsExtractResult> ExtractAsync(LtfsExtractRequest request, CancellationToken cancellationToken = default)
    {
        using (Log.PushMethod())
        {
            ArgumentNullException.ThrowIfNull(request);
            var options = ResolveOptions(request.Options);
            var extractOptions = request.ExtractOptions ?? new LtfsExtractOptions();
            ValidateExtractRequest(request, options);
            var skippedResults = new List<LtfsExtractFileResult>();
            var effectiveTargets = ApplyExtractConflictPolicy(request.Targets, extractOptions, skippedResults);

            var operationId = Guid.NewGuid().ToString("N");
            var plan = LtfsSequentialReadPlanner.CreatePlan(
                effectiveTargets,
                new LtfsSequentialReadPlanOptions(options.BlockSizeBytes, options.MemoryCacheLimitBytes));

            Publish(operationId, LtfsWriterStepKind.ReadStarted, $"Reading {effectiveTargets.Count} LTFS file(s). Memory cache limit={options.MemoryCacheLimitBytes} bytes.", totalFiles: effectiveTargets.Count);
            Log.Information("LTFS read started. OperationId={OperationId}, FileCount={FileCount}, CacheLimit={CacheLimit}, UsesMemorySpool={UsesMemorySpool}, UsesLocateReplay={UsesLocateReplay}", operationId, effectiveTargets.Count, options.MemoryCacheLimitBytes, plan.UsesMemorySpool, plan.UsesLocateReplay);

            if (request.DryRun)
                return new LtfsExtractResult(0, 0, plan, DryRun: true, FileResults: skippedResults);

            var reserved = false;
            var removalPrevented = false;
            var tapeExecutor = new LtfsTapeCommandExecutor();
            var sink = new FileSystemLtfsReadSink(operationId, eventBus, effectiveTargets, options.Hashes ?? LtfsHashOptions.None, extractOptions);
            try
            {
                await PreflightAsync(operationId, options, tapeExecutor, cancellationToken).ConfigureAwait(false);
                reserved = true;
                removalPrevented = true;

                try
                {
                    await new LtfsSequentialReadExecutor(new LtfsExecutorBlockReader(device, tapeExecutor, options.TapeControl)).ExecuteAsync(plan, sink, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsEncryptionRelated(ex) && sink.BytesRead == 0 && sink.FilesCompleted == 0)
                {
                    await ApplyEncryptionAsync(operationId, options, cancellationToken).ConfigureAwait(false);
                    await sink.DisposeAsync().ConfigureAwait(false);
                    sink = new FileSystemLtfsReadSink(operationId, eventBus, effectiveTargets, options.Hashes ?? LtfsHashOptions.None, extractOptions);
                    await new LtfsSequentialReadExecutor(new LtfsExecutorBlockReader(device, tapeExecutor, options.TapeControl)).ExecuteAsync(plan, sink, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (extractOptions.RetryHashMismatchOnce && IsHashMismatch(ex))
                {
                    await sink.DisposeAsync().ConfigureAwait(false);
                    sink = new FileSystemLtfsReadSink(operationId, eventBus, effectiveTargets, options.Hashes ?? LtfsHashOptions.None, extractOptions);
                    await new LtfsSequentialReadExecutor(new LtfsExecutorBlockReader(device, tapeExecutor, options.TapeControl)).ExecuteAsync(plan, sink, cancellationToken).ConfigureAwait(false);
                }

                Publish(operationId, LtfsWriterStepKind.ReadCompleted, "LTFS read completed.", sink.BytesRead, sink.TotalBytes, sink.FilesCompleted, effectiveTargets.Count);
                Log.Information("LTFS read completed. OperationId={OperationId}, BytesRead={BytesRead}, FilesRead={FilesRead}", operationId, sink.BytesRead, sink.FilesCompleted);
                return new LtfsExtractResult(sink.BytesRead, sink.FilesCompleted, plan, DryRun: false, skippedResults.Concat(sink.GetResults()).OrderBy(x => x.FileUid).ToArray());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PublishFailure(operationId, LtfsWriterStepKind.Failed, "LTFS read failed.", ex);
                throw new LtfsWriterException("LTFS read failed.", ex);
            }
            finally
            {
                await sink.DisposeAsync().ConfigureAwait(false);
                await ReleaseDriveAsync(removalPrevented, reserved, options).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask<LtfsHashMaintenanceResult> RunHashMaintenanceAsync(LtfsHashMaintenanceRequest request, CancellationToken cancellationToken = default)
    {
        using (Log.PushMethod())
        {
            ArgumentNullException.ThrowIfNull(request);
            var options = ResolveOptions(request.Options);
            ValidateHashMaintenanceRequest(request, options);

            var operationId = Guid.NewGuid().ToString("N");
            var index = request.Index.Clone();
            var targets = BuildMaintenanceTargets(index, request.Targets, request.Mode);
            var plan = LtfsSequentialReadPlanner.CreatePlan(
                targets,
                new LtfsSequentialReadPlanOptions(options.BlockSizeBytes, options.MemoryCacheLimitBytes));

            Publish(operationId, LtfsWriterStepKind.ReadStarted, $"LTFS hash maintenance {request.Mode} started for {targets.Count} file(s).", totalFiles: targets.Count);
            Log.Information("LTFS hash maintenance started. OperationId={OperationId}, Mode={Mode}, FileCount={FileCount}", operationId, request.Mode, targets.Count);

            if (request.Mode != LtfsHashMaintenanceMode.UpdateOnly)
            {
                var extractRequest = new LtfsExtractRequest(targets, options, request.DryRun, request.ExtractOptions);
                var extract = await ExtractAsync(extractRequest, cancellationToken).ConfigureAwait(false);
                return new LtfsHashMaintenanceResult(
                    index,
                    extract.BytesRead,
                    extract.FilesRead,
                    DataPartitionIndexWritten: false,
                    IndexPartitionRefreshed: false,
                    VciWritten: false,
                    request.DryRun,
                    extract.Plan,
                    (extract.FileResults ?? []).Select(x => ToHashMaintenanceResult(x, request.Mode)).ToArray());
            }

            var sink = new LtfsHashUpdateReadSink(targets, options.Hashes ?? LtfsHashOptions.None);
            sink.ApplyEmptyFileHashes();
            var reserved = false;
            var removalPrevented = false;
            var tapeExecutor = new LtfsTapeCommandExecutor();
            try
            {
                await PreflightAsync(operationId, options, tapeExecutor, cancellationToken).ConfigureAwait(false);
                reserved = true;
                removalPrevented = true;

                if (plan.ReadCommandCount != 0)
                    await new LtfsSequentialReadExecutor(new LtfsExecutorBlockReader(device, tapeExecutor, options.TapeControl)).ExecuteAsync(plan, sink, cancellationToken).ConfigureAwait(false);

                var results = sink.GetResults();
                var failed = results.FirstOrDefault(x => x.UpdateStatus is LtfsHashUpdateStatus.Mismatch or LtfsHashUpdateStatus.NoEnabledHash);
                if (failed is not null)
                    throw new LtfsWriterException($"LTFS hash update failed for '{failed.FileName}': {failed.Message}");

                if (request.DryRun)
                    return new LtfsHashMaintenanceResult(index, sink.BytesRead, sink.FilesCompleted, false, false, false, true, plan, results);

                var dataPartition = InferDataPartition(index);
                await LocateEndOfDataWithExecutorAsync(tapeExecutor, dataPartition, options, cancellationToken).ConfigureAwait(false);
                index = await WriteDataPartitionIndexAsync(operationId, index, options, tapeExecutor, label: null, sources: null, reason: "hash-update", cancellationToken).ConfigureAwait(false);
                var dataIndexWritten = true;
                var indexPartitionRefreshed = false;
                var vciWritten = false;

                if (dataPartition == LtfsPartition.B && options.RefreshIndexPartitionOnComplete)
                {
                    index = await RefreshIndexPartitionAsync(operationId, index, options, tapeExecutor, cancellationToken).ConfigureAwait(false);
                    indexPartitionRefreshed = true;
                    vciWritten = options.WriteVci;
                }
                else if (options.WriteVci)
                {
                    await WriteVciWithWormPolicyAsync(operationId, index, options, tapeExecutor, cancellationToken).ConfigureAwait(false);
                    vciWritten = true;
                }

                Publish(operationId, LtfsWriterStepKind.Completed, "LTFS hash maintenance completed.", sink.BytesRead, sink.TotalBytes, sink.FilesCompleted, targets.Count);
                return new LtfsHashMaintenanceResult(index, sink.BytesRead, sink.FilesCompleted, dataIndexWritten, indexPartitionRefreshed, vciWritten, false, plan, results);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PublishFailure(operationId, LtfsWriterStepKind.Failed, "LTFS hash maintenance failed.", ex);
                throw new LtfsWriterException("LTFS hash maintenance failed.", ex);
            }
            finally
            {
                await sink.DisposeAsync().ConfigureAwait(false);
                await ReleaseDriveAsync(removalPrevented, reserved, options).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask PreflightAsync(string operationId, LtfsWriterOptions options, LtfsTapeCommandExecutor executor, CancellationToken cancellationToken)
    {
        using (ScsiStartupUnitAttentionRetry.SuppressPowerOnReset(scopeName: "LTFS writer preflight"))
        {
            Publish(operationId, LtfsWriterStepKind.Preflight, "Reserve drive and set LTFS block size.");
            var queue = new LtfsTapeCommandQueue();
            queue.Enqueue(new LtfsTapeCommand(LtfsTapeCommandKind.ReserveDrive, ct => ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.Preflight, "Reserve drive", options, innerCt => device.ReserveAsync(innerCt), ct), LtfsTapeCommandPriority.Control, LtfsTapeBarrierKind.SessionBarrier, AffectsPosition: false));
            queue.Enqueue(new LtfsTapeCommand(LtfsTapeCommandKind.PreventRemoval, ct => ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.Preflight, "Prevent medium removal", options, innerCt => device.PreventRemovalAsync(true, innerCt), ct), LtfsTapeCommandPriority.Control, LtfsTapeBarrierKind.SessionBarrier, AffectsPosition: false));
            queue.Enqueue(new LtfsTapeCommand(LtfsTapeCommandKind.TestUnitReady, ct => ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.Preflight, "Test unit ready", options, innerCt => device.TestUnitReadyAsync(innerCt), ct), LtfsTapeCommandPriority.Control, LtfsTapeBarrierKind.SessionBarrier, AffectsPosition: false));
            await executor.ExecuteAsync(queue, options.TapeControl, cancellationToken).ConfigureAwait(false);
            await ApplyEncryptionAsync(operationId, options, cancellationToken).ConfigureAwait(false);
            queue = new LtfsTapeCommandQueue();
            queue.Enqueue(new LtfsTapeCommand(LtfsTapeCommandKind.SetBlockSize, ct => ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.Preflight, "Set LTFS block size", options, innerCt => device.SetBlockSizeAsync(options.BlockSizeBytes, innerCt), ct), LtfsTapeCommandPriority.Control, LtfsTapeBarrierKind.HardBarrier, AffectsPosition: false));
            await executor.ExecuteAsync(queue, options.TapeControl, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask LocateToWritePositionAsync(string operationId, LtfsIndex index, LtfsWriterOptions options, LtfsTapeCommandExecutor executor, CancellationToken cancellationToken)
    {
        Publish(operationId, LtfsWriterStepKind.LocateWritePosition, "Locate LTFS data partition write position.");
        if (index.Location.Partition == LtfsPartition.A)
        {
            var restored = await ReadIndexAtAsync(operationId, index.PreviousGenerationLocation, options, executor, cancellationToken).ConfigureAwait(false);
            index.Location = restored.Location.Clone();
            index.PreviousGenerationLocation = restored.PreviousGenerationLocation.Clone();
            await LocateEndOfDataWithExecutorAsync(executor, LtfsPartition.B, options, cancellationToken).ConfigureAwait(false);
            return;
        }

        await LocateEndOfDataWithExecutorAsync(executor, LtfsPartition.B, options, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ValidateAppendBaselineAsync(
        string operationId,
        LtfsIndex index,
        LtfsLabel? label,
        LtfsWriterOptions options,
        LtfsTapeCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var validation = options.AppendValidation ?? new LtfsAppendValidationOptions();
        if (!validation.Enabled && options.Discovery is null)
            return;

        var result = LtfsIndexValidator.ValidateInternal(index, new LtfsIndexValidationOptions(options.BlockSizeBytes));
        if (!result.IsValid)
            throw new LtfsWriterException("LTFS append baseline index is invalid: " + string.Join("; ", result.Errors));

        if (label is not null)
        {
            if (label.VolumeUuid != Guid.Empty && index.VolumeUuid != Guid.Empty && label.VolumeUuid != index.VolumeUuid)
                throw new LtfsWriterException("LTFS append baseline label and index volume UUID do not match.");
            if (label.BlockSize > 0 && label.BlockSize != options.BlockSizeBytes)
                throw new LtfsWriterException("LTFS append baseline block size does not match writer options.");
        }

        if (index.VolumeLockState == LtfsVolumeLockState.PermLocked && !(options.Discovery?.Worm ?? false))
            throw new LtfsWriterException("LTFS append baseline is permanently locked.");

        var discovery = options.Discovery;
        if (discovery is not null)
        {
            if (discovery.Index.VolumeUuid != Guid.Empty && index.VolumeUuid != Guid.Empty && discovery.Index.VolumeUuid != index.VolumeUuid)
                throw new LtfsWriterException("LTFS discovery result belongs to a different volume.");
            if (discovery.DirtyAppendDetected && validation.DirtyAppendPolicy == LtfsDirtyAppendPolicy.Abort)
                throw new LtfsWriterException("LTFS discovery found unindexed data after the latest stable checkpoint.");
        }

        var expected = discovery?.AppendPoint;
        if (expected is not null)
        {
            var actual = await LocateEndOfDataWithExecutorAsync(executor, LtfsPartition.B, options, cancellationToken).ConfigureAwait(false);
            if (actual.Partition != LtfsPartition.B || actual.Block < expected.Block)
                throw new LtfsWriterException($"LTFS append point is before the latest stable checkpoint. Expected >= B{expected.Block}, actual {actual.Partition}{actual.Block}.");
            if (actual.Block > expected.Block && validation.DirtyAppendPolicy == LtfsDirtyAppendPolicy.Abort)
                throw new LtfsWriterException("LTFS append point contains unindexed data and dirty append is not allowed.");
            Publish(operationId, LtfsWriterStepKind.LocateWritePosition, $"Validated LTFS append point at B{actual.Block}.");
        }
    }

    private async ValueTask<LtfsWritePlanState> WritePlannedSourcesAsync(
        string operationId,
        LtfsIndex index,
        LtfsDirectory targetDirectory,
        IReadOnlyList<LtfsWriteSource> sources,
        bool overwriteExisting,
        LtfsWriterOptions options,
        LtfsTapeCommandExecutor tapeExecutor,
        CancellationToken cancellationToken)
    {
        var plannedSources = NormalizeQueuedSources(sources);
        var totalBytes = plannedSources.Sum(x => x.Length);
        var smallFileThreshold = options.SmallFileThresholdBytes ?? options.BlockSizeBytes;
        var counters = new LtfsIndexCounters(0, 0, DateTimeOffset.UtcNow);
        long bytesWritten = 0;
        long filesWritten = 0;
        var dataIndexWritten = false;
        var writeContext = CreateWritePolicyContext(operationId, options, tapeExecutor);
        var capacityMonitor = new LtfsCapacityMonitor(device, options.CapacityPolicy ?? new LtfsCapacityPolicyOptions());
        var dedupCatalog = LtfsDedupCatalog.Build(index, options.Dedup!);

        for (var i = 0; i < plannedSources.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capacity = await capacityMonitor.SampleAsync(cancellationToken).ConfigureAwait(false);
            if (!capacity.HasReserveFor(plannedSources[i].Length, options.CapacityPolicy!.CompressionRatioEstimate))
            {
                var remaining = BuildRemainingManifest(index, "Capacity reserve reached before starting next file.", plannedSources.Take(i), plannedSources.Skip(i), includeCurrentAsRemaining: false);
                return new LtfsWritePlanState(index, bytesWritten, filesWritten, counters, dataIndexWritten, LtfsWriteCompletionKind.StoppedAtEndOfMedium, remaining);
            }

            var prepared = PreparePendingFile(index, targetDirectory, plannedSources[i], overwriteExisting, options);
            if (prepared is null)
                continue;

            if (await TryApplyDedupAsync(prepared, dedupCatalog, options, cancellationToken).ConfigureAwait(false))
            {
                prepared.Directory.Files.Add(prepared.File);
                filesWritten += 1;
                counters = AddIndexedFile(counters, prepared.Source);
                Publish(operationId, LtfsWriterStepKind.WriteFileCompleted, $"Deduplicated '{prepared.File.Name}'.", bytesWritten, totalBytes, filesWritten, plannedSources.Count);
                (index, counters, dataIndexWritten) = await CheckpointIfNeededAsync(operationId, index, counters, dataIndexWritten, options, writeContext.Executor, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (prepared.Source.Length == 0)
            {
                AddEmptyFileHashes(prepared.File, options);
                prepared.Directory.Files.Add(prepared.File);
                filesWritten += 1;
                counters = AddIndexedFile(counters, prepared.Source);
                Publish(operationId, LtfsWriterStepKind.WriteFileCompleted, $"Wrote '{prepared.File.Name}'.", bytesWritten, totalBytes, filesWritten, plannedSources.Count);
                (index, counters, dataIndexWritten) = await SampleHealthIfNeededAsync(
                    operationId,
                    index,
                    counters,
                    dataIndexWritten,
                    bytesWritten,
                    writeContext,
                    options,
                    checkpointAllowed: true,
                    force: options.HealthSampling!.SampleAfterFile,
                    cancellationToken).ConfigureAwait(false);
                (index, counters, dataIndexWritten) = await CheckpointIfNeededAsync(operationId, index, counters, dataIndexWritten, options, writeContext.Executor, cancellationToken).ConfigureAwait(false);
                if (writeContext.EndOfMediumStopRequested)
                {
                    var remaining = BuildRemainingManifest(index, writeContext.EndOfMediumReason ?? "End of medium reached.", plannedSources.Take(i + 1), plannedSources.Skip(i + 1), includeCurrentAsRemaining: false);
                    return new LtfsWritePlanState(index, bytesWritten, filesWritten, counters, dataIndexWritten, LtfsWriteCompletionKind.StoppedAtEndOfMedium, remaining);
                }
                if (await StopForSessionControlAsync(options, cancellationToken).ConfigureAwait(false))
                {
                    var remaining = BuildRemainingManifest(index, "Soft cancel requested.", plannedSources.Take(i + 1), plannedSources.Skip(i + 1), includeCurrentAsRemaining: false);
                    return new LtfsWritePlanState(index, bytesWritten, filesWritten, counters, dataIndexWritten, LtfsWriteCompletionKind.SoftCanceled, remaining);
                }
                continue;
            }

            if (prepared.Source.Length <= smallFileThreshold && prepared.Source.Length <= options.BlockSizeBytes)
            {
                var pack = new List<LtfsPendingFile> { prepared };
                var packedBytes = prepared.Source.Length;
                while (i + 1 < plannedSources.Count)
                {
                    var nextCandidate = plannedSources[i + 1];
                    if (nextCandidate.Length <= 0 || nextCandidate.Length > smallFileThreshold || nextCandidate.Length > options.BlockSizeBytes)
                        break;
                    if (packedBytes + nextCandidate.Length > options.BlockSizeBytes)
                        break;

                    var next = PreparePendingFile(index, targetDirectory, nextCandidate, overwriteExisting, options);
                    i += 1;
                    if (next is null)
                        continue;

                    if (await TryApplyDedupAsync(next, dedupCatalog, options, cancellationToken).ConfigureAwait(false))
                    {
                        next.Directory.Files.Add(next.File);
                        filesWritten += 1;
                        counters = AddIndexedFile(counters, next.Source);
                        Publish(operationId, LtfsWriterStepKind.WriteFileCompleted, $"Deduplicated '{next.File.Name}'.", bytesWritten, totalBytes, filesWritten, plannedSources.Count);
                        continue;
                    }

                    pack.Add(next);
                    packedBytes += next.Source.Length;
                }

                try
                {
                    await WritePackedSmallFilesAsync(operationId, pack, options, writeContext, cancellationToken).ConfigureAwait(false);
                }
                catch (LtfsEndOfMediumStopException)
                {
                    var remaining = BuildRemainingManifest(index, "End of medium while writing packed small-file block.", plannedSources.Take(i + 1), plannedSources.Skip(i + 1), includeCurrentAsRemaining: true);
                    return new LtfsWritePlanState(index, bytesWritten, filesWritten, counters, dataIndexWritten, LtfsWriteCompletionKind.StoppedAtEndOfMedium, remaining);
                }

                foreach (var item in pack)
                {
                    item.Directory.Files.Add(item.File);
                    bytesWritten += item.Source.Length;
                    filesWritten += 1;
                    counters = AddIndexedFile(counters, item.Source);
                    dedupCatalog.Add(item.File);
                    Publish(operationId, LtfsWriterStepKind.WriteFileCompleted, $"Wrote '{item.File.Name}'.", bytesWritten, totalBytes, filesWritten, plannedSources.Count);
                }

                (index, counters, dataIndexWritten) = await SampleHealthIfNeededAsync(
                    operationId,
                    index,
                    counters,
                    dataIndexWritten,
                    bytesWritten,
                    writeContext,
                    options,
                    checkpointAllowed: true,
                    force: options.HealthSampling!.SampleAfterFile,
                    cancellationToken).ConfigureAwait(false);
                (index, counters, dataIndexWritten) = await CheckpointIfNeededAsync(operationId, index, counters, dataIndexWritten, options, writeContext.Executor, cancellationToken).ConfigureAwait(false);
                if (writeContext.EndOfMediumStopRequested)
                {
                    var remaining = BuildRemainingManifest(index, writeContext.EndOfMediumReason ?? "End of medium reached.", plannedSources.Take(i + 1), plannedSources.Skip(i + 1), includeCurrentAsRemaining: false);
                    return new LtfsWritePlanState(index, bytesWritten, filesWritten, counters, dataIndexWritten, LtfsWriteCompletionKind.StoppedAtEndOfMedium, remaining);
                }
                if (await StopForSessionControlAsync(options, cancellationToken).ConfigureAwait(false))
                {
                    var remaining = BuildRemainingManifest(index, "Soft cancel requested.", plannedSources.Take(i + 1), plannedSources.Skip(i + 1), includeCurrentAsRemaining: false);
                    return new LtfsWritePlanState(index, bytesWritten, filesWritten, counters, dataIndexWritten, LtfsWriteCompletionKind.SoftCanceled, remaining);
                }
                continue;
            }

            var position = writeContext.Executor.ExpectedPosition
                ?? await ReadPositionWithExecutorAsync(writeContext.Executor, options, cancellationToken).ConfigureAwait(false);
            prepared.File.Extents.Add(new LtfsExtent
            {
                Partition = LtfsPartition.B,
                StartBlock = checked((long)position.Block),
                ByteOffset = 0,
                ByteCount = prepared.Source.Length,
                FileOffset = 0,
            });

            Publish(operationId, LtfsWriterStepKind.WriteFileStarted, $"Writing '{prepared.File.Name}'.", bytesWritten, totalBytes, filesWritten, plannedSources.Count);
            try
            {
                await WriteSourceAsync(
                    operationId,
                    prepared.Source,
                    prepared.File,
                    options,
                    writeContext,
                    bytesWritten,
                    async (totalBytesSoFar, ct) =>
                    {
                        var state = await SampleHealthIfNeededAsync(
                            operationId,
                            index,
                            counters,
                            dataIndexWritten,
                            totalBytesSoFar,
                            writeContext,
                            options,
                            checkpointAllowed: false,
                            force: false,
                            ct).ConfigureAwait(false);
                        index = state.Index;
                        counters = state.Counters;
                        dataIndexWritten = state.DataIndexWritten;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (LtfsEndOfMediumStopException ex)
            {
                prepared.File.Extents.Clear();
                var remaining = BuildRemainingManifest(index, ex.Message, plannedSources.Take(i), plannedSources.Skip(i), includeCurrentAsRemaining: true);
                return new LtfsWritePlanState(index, bytesWritten, filesWritten, counters, dataIndexWritten, LtfsWriteCompletionKind.StoppedAtEndOfMedium, remaining);
            }

            prepared.Directory.Files.Add(prepared.File);
            bytesWritten += prepared.Source.Length;
            filesWritten += 1;
            counters = AddIndexedFile(counters, prepared.Source);
            dedupCatalog.Add(prepared.File);
            Publish(operationId, LtfsWriterStepKind.WriteFileCompleted, $"Wrote '{prepared.File.Name}'.", bytesWritten, totalBytes, filesWritten, plannedSources.Count);

            (index, counters, dataIndexWritten) = await SampleHealthIfNeededAsync(
                operationId,
                index,
                counters,
                dataIndexWritten,
                bytesWritten,
                writeContext,
                options,
                checkpointAllowed: true,
                force: options.HealthSampling!.SampleAfterFile,
                cancellationToken).ConfigureAwait(false);
            (index, counters, dataIndexWritten) = await CheckpointIfNeededAsync(operationId, index, counters, dataIndexWritten, options, writeContext.Executor, cancellationToken).ConfigureAwait(false);
            if (writeContext.EndOfMediumStopRequested)
            {
                var remaining = BuildRemainingManifest(index, writeContext.EndOfMediumReason ?? "End of medium reached.", plannedSources.Take(i + 1), plannedSources.Skip(i + 1), includeCurrentAsRemaining: false);
                return new LtfsWritePlanState(index, bytesWritten, filesWritten, counters, dataIndexWritten, LtfsWriteCompletionKind.StoppedAtEndOfMedium, remaining);
            }
            if (await StopForSessionControlAsync(options, cancellationToken).ConfigureAwait(false))
            {
                var remaining = BuildRemainingManifest(index, "Soft cancel requested.", plannedSources.Take(i + 1), plannedSources.Skip(i + 1), includeCurrentAsRemaining: false);
                return new LtfsWritePlanState(index, bytesWritten, filesWritten, counters, dataIndexWritten, LtfsWriteCompletionKind.SoftCanceled, remaining);
            }
        }

        return new LtfsWritePlanState(index, bytesWritten, filesWritten, counters, dataIndexWritten);
    }

    private async ValueTask<bool> StopForSessionControlAsync(LtfsWriterOptions options, CancellationToken cancellationToken)
    {
        var control = options.TapeControl;
        if (control is null)
            return false;

        if (control.PauseRequested && !control.CancelRequested)
            await Task.Run(() => control.WaitIfPaused(cancellationToken), cancellationToken).ConfigureAwait(false);

        return control.CancelRequested && control.CancelMode is LtfsCancelMode.SoftAfterBlock or LtfsCancelMode.SoftAfterFile;
    }

    private async ValueTask WritePackedSmallFilesAsync(
        string operationId,
        IReadOnlyList<LtfsPendingFile> pack,
        LtfsWriterOptions options,
        LtfsWritePolicyContext writeContext,
        CancellationToken cancellationToken)
    {
        var position = writeContext.Executor.ExpectedPosition
            ?? await ReadPositionWithExecutorAsync(writeContext.Executor, options, cancellationToken).ConfigureAwait(false);
        var bufferPool = new LtfsTapeBufferPool(checked((int)options.BlockSizeBytes));
        using var tapeBuffer = bufferPool.Rent();
        var offset = 0;

        foreach (var item in pack)
        {
            Publish(operationId, LtfsWriterStepKind.WriteFileStarted, $"Writing '{item.File.Name}'.");
            await using var input = await item.Source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            var destination = tapeBuffer.Array.AsMemory(offset, checked((int)item.Source.Length));
            await ReadExactlyAsync(input, destination, cancellationToken).ConfigureAwait(false);

            item.File.Extents.Add(new LtfsExtent
            {
                Partition = LtfsPartition.B,
                StartBlock = checked((long)position.Block),
                ByteOffset = offset,
                ByteCount = item.Source.Length,
                FileOffset = 0,
            });

            if (ShouldComputeHashes(options))
            {
                using var hashers = LtfsFileHashSet.Create(options.Hashes ?? LtfsHashOptions.None);
                hashers.Append(destination.Span);
                hashers.ApplyTo(item.File);
            }

            item.File.OpenForWrite = false;
            offset += checked((int)item.Source.Length);
        }

        tapeBuffer.Length = offset;
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.WriteDataRun,
            ct => WriteDataBlockAsync(operationId, $"Write packed block with {pack.Count} file(s).", tapeBuffer.Memory, options, writeContext, position, ct),
            LtfsTapeCommandPriority.Data,
            LtfsTapeBarrierKind.None));

        await writeContext.Executor.ExecuteAsync(queue, options.TapeControl, cancellationToken).ConfigureAwait(false);
        Publish(operationId, LtfsWriterStepKind.WriteBlock, $"Wrote packed block with {pack.Count} file(s).", offset, offset);
    }

    private LtfsWritePolicyContext CreateWritePolicyContext(string operationId, LtfsWriterOptions options, LtfsTapeCommandExecutor executor)
    {
        var healthSampling = options.HealthSampling ?? new LtfsHealthSamplingOptions();
        var reloadPolicy = options.AutoReloadPolicy ?? new LtfsAutoReloadPolicyOptions();
        var throttlePolicy = options.ThrottlePolicy ?? new LtfsThrottlePolicyOptions();
        return new LtfsWritePolicyContext(
            new LtfsSlidingThroughputLimiter(throttlePolicy),
            new LtfsWriteHealthMonitor(reloadPolicy, new LtfsWriteErrorRateSampler(device, healthSampling)),
            healthSampling,
            DateTimeOffset.UtcNow,
            0,
            operationId,
            executor);
    }

    private async ValueTask WriteDataBlockAsync(
        string operationId,
        string message,
        ReadOnlyMemory<byte> block,
        LtfsWriterOptions options,
        LtfsWritePolicyContext writeContext,
        LtfsTapePosition? expectedPosition,
        CancellationToken cancellationToken)
    {
        await writeContext.Throttle.DelayBeforeWriteAsync(block.Length, cancellationToken).ConfigureAwait(false);
        var attempt = 0;
        while (true)
        {
            attempt += 1;
            var executor = writeContext.Executor;
            try
            {
                if (expectedPosition is not null && (!executor.PositionKnown || executor.ExpectedPosition is null || executor.ExpectedPosition.Partition != expectedPosition.Partition || executor.ExpectedPosition.Block != expectedPosition.Block))
                    executor.SetExpectedPosition(expectedPosition);
                var queue = new LtfsTapeCommandQueue();
                queue.Enqueue(new LtfsTapeCommand(
                    LtfsTapeCommandKind.WriteDataBlock,
                    ct => device.WriteBlockAsync(block, ct),
                    LtfsTapeCommandPriority.Data,
                    LtfsTapeBarrierKind.None,
                    ExpectedStartPosition: expectedPosition,
                    ExpectedEndPosition: expectedPosition is null ? null : expectedPosition with { Block = expectedPosition.Block + 1 },
                    ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
                await executor.ExecuteAsync(queue, options.TapeControl, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LtfsTapePosition? currentPosition = executor.ExpectedPosition;
                var kind = ClassifyError(ex);
                if (expectedPosition is not null && currentPosition is null)
                {
                    try
                    {
                        currentPosition = await device.ReadPositionAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception positionException) when (positionException is not OperationCanceledException)
                    {
                        Log.Warning(positionException, "Unable to read tape position after LTFS WRITE failure.");
                    }
                }

                if (currentPosition is not null
                    && expectedPosition is not null
                    && currentPosition.Partition == expectedPosition.Partition
                    && currentPosition.Block > expectedPosition.Block)
                {
                    Publish(operationId, LtfsWriterStepKind.Warning, $"{message} failed after the tape position advanced; treating the WRITE as committed.", severity: KokoOperationSeverity.Warning);
                    return;
                }

                if (kind is LtfsWriterErrorKind.EarlyWarningEndOfMedium)
                {
                    writeContext.EndOfMediumStopRequested = true;
                    writeContext.EndOfMediumReason = "Early warning end-of-medium reached.";
                    if (currentPosition is not null && expectedPosition is not null && currentPosition.Partition == expectedPosition.Partition && currentPosition.Block > expectedPosition.Block)
                        return;
                }

                if (kind is LtfsWriterErrorKind.EndOfMedium or LtfsWriterErrorKind.VolumeOverflow)
                    throw new LtfsEndOfMediumStopException(kind.ToString(), committedCurrentBlock: currentPosition is not null && expectedPosition is not null && currentPosition.Partition == expectedPosition.Partition && currentPosition.Block > expectedPosition.Block, ex);

                var decision = await ResolvePolicyDecisionAsync(operationId, LtfsWriterStepKind.WriteBlock, message, ex, attempt, options, currentPosition, cancellationToken).ConfigureAwait(false);
                PublishPolicyDecision(operationId, LtfsWriterStepKind.WriteBlock, ClassifyError(ex), decision, attempt);
                if (decision.Action == LtfsWriterRecoveryAction.Retry)
                    continue;
                if (decision.Action == LtfsWriterRecoveryAction.Ignore)
                    return;
                if (decision.Action == LtfsWriterRecoveryAction.ReloadThenRetry)
                {
                    await ReloadDriveAtDataEodAsync(operationId, LtfsWriteHealthAction.Reload, options, writeContext.Executor, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw;
            }
        }
    }

    private async ValueTask<(LtfsIndex Index, LtfsIndexCounters Counters, bool DataIndexWritten)> SampleHealthIfNeededAsync(
        string operationId,
        LtfsIndex index,
        LtfsIndexCounters counters,
        bool dataIndexWritten,
        long bytesWritten,
        LtfsWritePolicyContext writeContext,
        LtfsWriterOptions options,
        bool checkpointAllowed,
        bool force,
        CancellationToken cancellationToken)
    {
        var sampling = writeContext.HealthSampling;
        var reloadPolicy = options.AutoReloadPolicy ?? new LtfsAutoReloadPolicyOptions();
        if (!reloadPolicy.Enabled && sampling.CustomSampler is null)
            return (index, counters, dataIndexWritten);

        var now = DateTimeOffset.UtcNow;
        var byteIntervalReached = sampling.LargeFileByteInterval is > 0
            && bytesWritten - writeContext.LastHealthSampleBytes >= sampling.LargeFileByteInterval.Value;
        var timeIntervalReached = sampling.LargeFileTimeInterval is { } interval
            && interval > TimeSpan.Zero
            && now - writeContext.LastHealthSampleTime >= interval;

        if (!force && !byteIntervalReached && !timeIntervalReached)
            return (index, counters, dataIndexWritten);

        writeContext.LastHealthSampleBytes = bytesWritten;
        writeContext.LastHealthSampleTime = now;

        var decision = writeContext.HealthMonitor.TryConsumePendingReload()
            ?? await writeContext.HealthMonitor.SampleAsync(operationId, bytesWritten, cancellationToken).ConfigureAwait(false);

        if (decision.Action == LtfsWriteHealthAction.Continue)
            return (index, counters, dataIndexWritten);

        PublishHealthDecision(operationId, decision);

        if (decision.Action == LtfsWriteHealthAction.Abort)
            throw new LtfsWriterException(decision.Reason);

        if (decision.Action is LtfsWriteHealthAction.Reload or LtfsWriteHealthAction.CleanReload)
        {
            if (checkpointAllowed && reloadPolicy.CheckpointBeforeReload && counters.UnindexedBytes != 0)
            {
                index = await WriteDataPartitionIndexAsync(operationId, index, options, writeContext.Executor, cancellationToken).ConfigureAwait(false);
                counters = new LtfsIndexCounters(0, 0, DateTimeOffset.UtcNow);
                dataIndexWritten = true;
            }

            await ReloadDriveAtDataEodAsync(operationId, decision.Action, options, writeContext.Executor, cancellationToken).ConfigureAwait(false);
            return (index, counters, dataIndexWritten);
        }

        if (decision.Action == LtfsWriteHealthAction.Flush)
        {
            await ExecuteHealthFlushAsync(operationId, decision, options, writeContext.Executor, cancellationToken).ConfigureAwait(false);
            if (writeContext.HealthMonitor.RecordCapacityLossFlushSucceeded(decision) is { } pending)
            {
                PublishHealthDecision(operationId, pending);
                if (pending.Action == LtfsWriteHealthAction.Abort)
                    throw new LtfsWriterException(pending.Reason);
            }
        }

        return (index, counters, dataIndexWritten);
    }

    private async ValueTask ExecuteHealthFlushAsync(
        string operationId,
        LtfsWriteHealthDecision decision,
        LtfsWriterOptions options,
        LtfsTapeCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.ReadPosition,
            async ct => { _ = await device.ReadPositionAsync(ct).ConfigureAwait(false); },
            LtfsTapeCommandPriority.Health,
            LtfsTapeBarrierKind.HardBarrier,
            AffectsPosition: false));
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.ReadWriteErrorCounters,
            async ct => { _ = await device.ReadLogSenseAsync((options.HealthSampling ?? new LtfsHealthSamplingOptions()).EffectiveLogPage, ct).ConfigureAwait(false); },
            LtfsTapeCommandPriority.Health,
            LtfsTapeBarrierKind.HardBarrier,
            AffectsPosition: false));
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.Flush,
            ct => ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.HealthPolicy, decision.Reason, options, innerCt => device.FlushAsync(innerCt), ct),
            LtfsTapeCommandPriority.Health,
            LtfsTapeBarrierKind.HardBarrier,
            AffectsPosition: false));
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.RefreshCapacity,
            async ct =>
            {
                var capacityPolicy = options.CapacityPolicy ?? new LtfsCapacityPolicyOptions();
                if (capacityPolicy.Enabled)
                    _ = await new LtfsCapacityMonitor(device, capacityPolicy).SampleAsync(ct).ConfigureAwait(false);
            },
            LtfsTapeCommandPriority.Health,
            LtfsTapeBarrierKind.HardBarrier,
            AffectsPosition: false));
        await executor.ExecuteAsync(queue, options.TapeControl, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<LtfsTapePosition> LocateEndOfDataWithExecutorAsync(
        LtfsTapeCommandExecutor executor,
        LtfsPartition partition,
        LtfsWriterOptions options,
        CancellationToken cancellationToken)
    {
        var position = default(LtfsTapePosition);
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.LocateEod,
            ct => device.LocateEndOfDataAsync(partition, ct),
            LtfsTapeCommandPriority.Control,
            LtfsTapeBarrierKind.HardBarrier,
            ExpectedEndPosition: executor.ExpectedPosition?.Partition == partition ? executor.ExpectedPosition : null,
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.ReadPosition,
            async ct => position = await device.ReadPositionAsync(ct).ConfigureAwait(false),
            LtfsTapeCommandPriority.Control,
            LtfsTapeBarrierKind.HardBarrier,
            AffectsPosition: false,
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        await executor.ExecuteAsync(queue, options.TapeControl, cancellationToken).ConfigureAwait(false);
        if (position is not null)
            executor.SetExpectedPosition(position);
        return position ?? executor.ExpectedPosition ?? throw new LtfsWriterException("LTFS locate EOD did not produce a tape position.");
    }

    private async ValueTask LocateFilemarkWithExecutorAsync(
        LtfsTapeCommandExecutor executor,
        LtfsPartition partition,
        ulong filemark,
        LtfsWriterOptions options,
        CancellationToken cancellationToken)
    {
        var target = new LtfsTapePosition(partition, filemark, filemark);
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.LocateFilemark,
            ct => device.LocateFilemarkAsync(partition, filemark, ct),
            LtfsTapeCommandPriority.Control,
            LtfsTapeBarrierKind.HardBarrier,
            ExpectedEndPosition: target,
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        await executor.ExecuteAsync(queue, options.TapeControl, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<LtfsTapePosition> ReadPositionWithExecutorAsync(
        LtfsTapeCommandExecutor executor,
        LtfsWriterOptions options,
        CancellationToken cancellationToken)
    {
        LtfsTapePosition? position = null;
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.ReadPosition,
            async ct => position = await device.ReadPositionAsync(ct).ConfigureAwait(false),
            LtfsTapeCommandPriority.Control,
            LtfsTapeBarrierKind.HardBarrier,
            AffectsPosition: false,
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        await executor.ExecuteAsync(queue, options.TapeControl, cancellationToken).ConfigureAwait(false);
        if (position is null)
            throw new LtfsWriterException("LTFS READ POSITION did not return a position.");
        executor.SetExpectedPosition(position);
        return position;
    }

    private async ValueTask ExecuteFilemarksWithExecutorAsync(
        LtfsTapeCommandExecutor executor,
        uint count,
        LtfsWriterOptions options,
        CancellationToken cancellationToken)
    {
        var start = executor.ExpectedPosition;
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.WriteFilemark,
            ct => device.WriteFilemarksAsync(count, ct),
            LtfsTapeCommandPriority.Control,
            LtfsTapeBarrierKind.HardBarrier,
            ExpectedStartPosition: start,
            ExpectedEndPosition: start is null ? null : start with { Block = start.Block + count },
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        await executor.ExecuteAsync(queue, options.TapeControl, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ExecuteWriteBlockWithExecutorAsync(
        LtfsTapeCommandExecutor executor,
        ReadOnlyMemory<byte> data,
        LtfsTapePosition expectedPosition,
        LtfsWriterOptions options,
        CancellationToken cancellationToken)
    {
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.WriteDataBlock,
            ct => device.WriteBlockAsync(data, ct),
            LtfsTapeCommandPriority.Data,
            LtfsTapeBarrierKind.None,
            ExpectedStartPosition: expectedPosition,
            ExpectedEndPosition: expectedPosition with { Block = expectedPosition.Block + 1 },
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        if (!executor.PositionKnown || executor.ExpectedPosition is null || executor.ExpectedPosition.Partition != expectedPosition.Partition || executor.ExpectedPosition.Block != expectedPosition.Block)
            executor.SetExpectedPosition(expectedPosition);
        await executor.ExecuteAsync(queue, options.TapeControl, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<byte[]> ReadToFilemarkWithExecutorAsync(
        LtfsTapeCommandExecutor executor,
        LtfsPartition partition,
        ulong block,
        LtfsWriterOptions options,
        CancellationToken cancellationToken)
    {
        byte[]? payload = null;
        var target = new LtfsTapePosition(partition, block);
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.LocateBlock,
            ct => device.LocateAsync(partition, block, ct),
            LtfsTapeCommandPriority.Control,
            LtfsTapeBarrierKind.HardBarrier,
            ExpectedEndPosition: target,
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.ReadDataBlock,
            async ct => payload = await device.ReadToFilemarkAsync(options.BlockSizeBytes, ct).ConfigureAwait(false),
            LtfsTapeCommandPriority.Data,
            LtfsTapeBarrierKind.HardBarrier,
            ExpectedStartPosition: target,
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        await executor.ExecuteAsync(queue, options.TapeControl, cancellationToken).ConfigureAwait(false);
        return payload ?? throw new LtfsWriterException($"LTFS read-to-filemark at {partition}{block} returned no payload.");
    }

    private async ValueTask ReloadDriveAtDataEodAsync(
        string operationId,
        LtfsWriteHealthAction action,
        LtfsWriterOptions options,
        LtfsTapeCommandExecutor? executor,
        CancellationToken cancellationToken)
    {
        executor ??= new LtfsTapeCommandExecutor();
        var actionText = action == LtfsWriteHealthAction.CleanReload ? "CleanReload reload cycle" : "reload";
        Publish(operationId, LtfsWriterStepKind.HealthPolicy, $"LTFS health policy requested {actionText}; flushing and reloading drive.");
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(LtfsTapeCommandKind.Flush, ct => ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.HealthPolicy, "Flush before LTFS health reload", options, innerCt => device.FlushAsync(innerCt), ct), LtfsTapeCommandPriority.Health, LtfsTapeBarrierKind.HardBarrier, AffectsPosition: false));
        queue.Enqueue(new LtfsTapeCommand(LtfsTapeCommandKind.AllowRemoval, ct => ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.HealthPolicy, "Allow medium removal before LTFS health reload", options, innerCt => device.PreventRemovalAsync(false, innerCt), ct), LtfsTapeCommandPriority.Health, LtfsTapeBarrierKind.HardBarrier, AffectsPosition: false));
        queue.Enqueue(new LtfsTapeCommand(LtfsTapeCommandKind.LoadUnload, ct => ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.HealthPolicy, "Unload before LTFS health reload", options, innerCt => device.LoadUnloadAsync(false, innerCt), ct), LtfsTapeCommandPriority.Health, LtfsTapeBarrierKind.HardBarrier, AffectsPosition: false));
        queue.Enqueue(new LtfsTapeCommand(LtfsTapeCommandKind.LoadUnload, ct => ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.HealthPolicy, "Load after LTFS health reload", options, innerCt => device.LoadUnloadAsync(true, innerCt), ct), LtfsTapeCommandPriority.Health, LtfsTapeBarrierKind.HardBarrier, AffectsPosition: false));
        queue.Enqueue(new LtfsTapeCommand(LtfsTapeCommandKind.TestUnitReady, ct => ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.HealthPolicy, "Test unit ready after LTFS health reload", options, innerCt => device.TestUnitReadyAsync(innerCt), ct), LtfsTapeCommandPriority.Health, LtfsTapeBarrierKind.HardBarrier, AffectsPosition: false));
        await executor.ExecuteAsync(queue, options.TapeControl, cancellationToken).ConfigureAwait(false);
        await ApplyEncryptionAsync(operationId, options, cancellationToken).ConfigureAwait(false);
        queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(LtfsTapeCommandKind.SetBlockSize, ct => ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.HealthPolicy, "Set LTFS block size after health reload", options, innerCt => device.SetBlockSizeAsync(options.BlockSizeBytes, innerCt), ct), LtfsTapeCommandPriority.Health, LtfsTapeBarrierKind.HardBarrier, AffectsPosition: false));
        queue.Enqueue(new LtfsTapeCommand(LtfsTapeCommandKind.PreventRemoval, ct => ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.HealthPolicy, "Prevent medium removal after LTFS health reload", options, innerCt => device.PreventRemovalAsync(true, innerCt), ct), LtfsTapeCommandPriority.Health, LtfsTapeBarrierKind.HardBarrier, AffectsPosition: false));
        await executor.ExecuteAsync(queue, options.TapeControl, cancellationToken).ConfigureAwait(false);
        await LocateEndOfDataWithExecutorAsync(executor, LtfsPartition.B, options, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ApplyEncryptionAsync(string operationId, LtfsWriterOptions options, CancellationToken cancellationToken)
    {
        var encryption = options.Encryption ?? new LtfsEncryptionOptions();
        if (encryption.Mode == LtfsEncryptionMode.Disabled)
            return;

        if (device is not ILtfsEncryptionCapableDevice encryptionDevice)
            throw new LtfsWriterException("LTFS encryption was requested but the writer device does not support encryption.");

        if (encryption.KeyProvider is null)
            throw new LtfsWriterException("LTFS encryption key provider is required when encryption is enabled.");

        var material = await encryption.KeyProvider.ResolveKeyAsync(
            new LtfsEncryptionKeyRequest(operationId, encryption.Mode, encryption.KeyId),
            cancellationToken).ConfigureAwait(false);
        if (material is null)
            throw new LtfsWriterException("LTFS encryption key provider did not return key material.");
        if (material.Key.Length != 32)
            throw new LtfsWriterException("LTFS encryption key must be exactly 32 bytes.");
        if (material.Key.Span.ToArray().All(x => x == 0))
            throw new LtfsWriterException("LTFS encryption key cannot be all zero bytes.");

        await ExecuteWithPolicyAsync(
            operationId,
            LtfsWriterStepKind.Preflight,
            "Set LTFS encryption key",
            options,
            ct => encryptionDevice.SetEncryptionAsync(material.Key, ct),
            cancellationToken).ConfigureAwait(false);
        eventBus.Publish(new LtfsEncryptionEvent(operationId, "LTFS encryption key applied.", material.KeyFingerprint));
    }

    private async ValueTask TryExportAutosaveAsync(
        string operationId,
        string reason,
        LtfsIndex index,
        LtfsLabel? label,
        IReadOnlyList<LtfsWriteSource>? sources,
        LtfsWriterOptions options,
        CancellationToken cancellationToken,
        LtfsRemainingManifest? remainingManifest = null)
    {
        var autosave = options.Autosave ?? new LtfsAutosaveOptions();
        if (!autosave.Enabled)
            return;

        try
        {
            await new LtfsAutosaveExporter(eventBus).ExportAsync(
                new LtfsAutosaveRequest(
                    operationId,
                    reason,
                    index.Clone(),
                    label?.Clone(),
                    autosave,
                    sources,
                    device as ILtfsMetadataExportDevice,
                    remainingManifest),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Publish(operationId, LtfsWriterStepKind.Warning, $"LTFS autosave/export failed: {ex.Message}", severity: KokoOperationSeverity.Warning);
        }
    }

    private async ValueTask<IReadOnlyList<string>> TryExportAutosaveAndReturnAsync(
        string operationId,
        string reason,
        LtfsIndex index,
        LtfsLabel? label,
        IReadOnlyList<LtfsWriteSource>? sources,
        LtfsWriterOptions options,
        CancellationToken cancellationToken,
        LtfsRemainingManifest? remainingManifest = null)
    {
        var autosave = options.Autosave ?? new LtfsAutosaveOptions();
        if (!autosave.Enabled)
            return Array.Empty<string>();

        try
        {
            return await new LtfsAutosaveExporter(eventBus).ExportAsync(
                new LtfsAutosaveRequest(
                    operationId,
                    reason,
                    index.Clone(),
                    label?.Clone(),
                    autosave,
                    sources,
                    device as ILtfsMetadataExportDevice,
                    remainingManifest),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Publish(operationId, LtfsWriterStepKind.Warning, $"LTFS autosave/export failed: {ex.Message}", severity: KokoOperationSeverity.Warning);
            return Array.Empty<string>();
        }
    }

    private void PublishHealthDecision(string operationId, LtfsWriteHealthDecision decision)
    {
        eventBus.Publish(new LtfsWriteHealthPolicyEvent(
            operationId,
            decision.Reason,
            decision.CurrentSpeedMiBPerSecond,
            decision.ErrorRate,
            decision.ReloadCount,
            decision.Action));
        Publish(operationId, LtfsWriterStepKind.HealthPolicy, $"{decision.Action}: {decision.Reason}");
    }

    private async ValueTask<(LtfsIndex Index, LtfsIndexCounters Counters, bool DataIndexWritten)> CheckpointIfNeededAsync(
        string operationId,
        LtfsIndex index,
        LtfsIndexCounters counters,
        bool dataIndexWritten,
        LtfsWriterOptions options,
        LtfsTapeCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        if (!LtfsIndexRepository.ShouldCheckpoint(counters, options.CheckpointPolicy ?? new LtfsCheckpointPolicy(), DateTimeOffset.UtcNow))
            return (index, counters, dataIndexWritten);

        index = await WriteDataPartitionIndexAsync(operationId, index, options, executor, cancellationToken).ConfigureAwait(false);
        return (index, new LtfsIndexCounters(0, 0, DateTimeOffset.UtcNow), true);
    }

    private static LtfsIndexCounters AddIndexedFile(LtfsIndexCounters counters, LtfsWriteSource source)
    {
        return counters with
        {
            UnindexedBytes = counters.UnindexedBytes + Math.Max(1, source.Length),
            UnindexedFiles = counters.UnindexedFiles + 1,
        };
    }

    private static LtfsRemainingManifest BuildRemainingManifest(
        LtfsIndex index,
        string reason,
        IEnumerable<LtfsWriteSource> completedSources,
        IEnumerable<LtfsWriteSource> remainingSources,
        bool includeCurrentAsRemaining)
    {
        var completed = completedSources
            .Select(x => new LtfsRemainingManifestItem(x.Name, x.SourcePath, x.DestinationPath, x.Length, "Completed"))
            .ToArray();
        var remaining = remainingSources
            .Select((x, n) => new LtfsRemainingManifestItem(
                x.Name,
                x.SourcePath,
                x.DestinationPath,
                x.Length,
                includeCurrentAsRemaining && n == 0 ? "Interrupted" : "Pending",
                includeCurrentAsRemaining && n == 0 ? reason : null))
            .ToArray();
        var interrupted = includeCurrentAsRemaining ? remaining.FirstOrDefault() : null;

        return new LtfsRemainingManifest(
            index.VolumeUuid,
            index.GenerationNumber,
            index.Location.Clone(),
            reason,
            DateTimeOffset.UtcNow,
            completed,
            remaining,
            VolumeSetId: Guid.NewGuid(),
            NextAction: remaining.Length == 0 ? null : "ContinueOnNextVolume",
            InterruptedFile: interrupted);
    }

    private static LtfsPendingFile? PreparePendingFile(
        LtfsIndex index,
        LtfsDirectory targetDirectory,
        LtfsWriteSource source,
        bool overwriteExisting,
        LtfsWriterOptions options)
    {
        var currentSource = RefreshSourceSnapshot(source, options);
        if (currentSource is null)
            return null;

        var destinationPath = NormalizeLtfsRelativePath(currentSource.DestinationPath ?? currentSource.Name);
        var (directoryPath, fileName) = SplitDestination(destinationPath);
        var directory = EnsureDirectoryChain(index, targetDirectory, directoryPath);
        var same = directory.Files.FirstOrDefault(x => string.Equals(x.Name, fileName, StringComparison.OrdinalIgnoreCase));
        if (same is not null)
        {
            if (overwriteExisting && !IsSameFile(currentSource, same))
                RemoveExistingFile(directory, fileName);
            else
                return null;
        }

        var file = CreateIndexFile(currentSource with { Name = fileName }, ++index.HighestFileUid);
        return new LtfsPendingFile(currentSource with { Name = fileName, DestinationPath = destinationPath }, directory, file);
    }

    private static IReadOnlyList<LtfsWriteSource> NormalizeQueuedSources(IReadOnlyList<LtfsWriteSource> sources)
    {
        var ordered = new List<LtfsWriteSource>();
        var byDestination = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            if (ShouldSkipLegacySource(source))
                continue;

            var destination = NormalizeLtfsRelativePath(source.DestinationPath ?? source.Name);
            if (string.IsNullOrWhiteSpace(destination))
                continue;

            var normalized = source with { DestinationPath = destination };
            if (byDestination.TryGetValue(destination, out var existingIndex))
            {
                ordered[existingIndex] = normalized;
                continue;
            }

            byDestination.Add(destination, ordered.Count);
            ordered.Add(normalized);
        }

        return ordered;
    }

    private static bool ShouldSkipLegacySource(LtfsWriteSource source)
    {
        var name = source.DestinationPath ?? source.Name;
        if (name.EndsWith(".xattr", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(source.SourcePath))
        {
            try
            {
                if (File.Exists(source.SourcePath))
                {
                    var attributes = File.GetAttributes(source.SourcePath);
                    return (attributes & FileAttributes.ReparsePoint) != 0;
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }

    private static async ValueTask<bool> TryApplyDedupAsync(
        LtfsPendingFile pending,
        LtfsDedupCatalog catalog,
        LtfsWriterOptions options,
        CancellationToken cancellationToken)
    {
        if (!catalog.Enabled || pending.Source.Length == 0 || !catalog.HasCandidates(pending.Source.Length))
            return false;

        var sourceHash = await ComputeSingleHashAsync(pending.Source, catalog.Algorithm, cancellationToken).ConfigureAwait(false);
        if (!catalog.TryFind(pending.Source.Length, sourceHash, out var existing))
            return false;

        pending.File.Extents.AddRange(existing.Extents.Select(x => x.Clone()));
        foreach (var attribute in existing.ExtendedAttributes.Where(x => LtfsHashMetadata.IsHashKey(x.Key)))
            pending.File.SetExtendedAttribute(attribute.Key, attribute.Value);
        pending.File.OpenForWrite = false;
        catalog.Add(pending.File);
        _ = options;
        return true;
    }

    private static async ValueTask<string> ComputeSingleHashAsync(
        LtfsWriteSource source,
        LtfsHashAlgorithmKind algorithm,
        CancellationToken cancellationToken)
    {
        await using var input = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        using var hashers = LtfsFileHashSet.Create(HashOptionsForSingleAlgorithm(algorithm));
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                hashers.Append(buffer.AsSpan(0, read));
            }

            return LtfsHashMetadata.NormalizeHash(hashers.GetHex(algorithm));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static LtfsHashOptions HashOptionsForSingleAlgorithm(LtfsHashAlgorithmKind algorithm)
    {
        return algorithm switch
        {
            LtfsHashAlgorithmKind.Blake3 => new LtfsHashOptions(Blake3: true, Sha512: false, Sha256: false, XxHash128: false, XxHash64: false, Sha1: false, Md5: false, Crc32: false),
            LtfsHashAlgorithmKind.Sha512 => new LtfsHashOptions(Blake3: false, Sha512: true, Sha256: false, XxHash128: false, XxHash64: false, Sha1: false, Md5: false, Crc32: false),
            LtfsHashAlgorithmKind.Sha256 => new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: true, XxHash128: false, XxHash64: false, Sha1: false, Md5: false, Crc32: false),
            LtfsHashAlgorithmKind.XxHash128 => new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: false, XxHash128: true, XxHash64: false, Sha1: false, Md5: false, Crc32: false),
            LtfsHashAlgorithmKind.XxHash64 => new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: false, XxHash128: false, XxHash64: true, Sha1: false, Md5: false, Crc32: false),
            LtfsHashAlgorithmKind.Sha1 => new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: false, XxHash128: false, XxHash64: false, Sha1: true, Md5: false, Crc32: false),
            LtfsHashAlgorithmKind.Md5 => new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: false, XxHash128: false, XxHash64: false, Sha1: false, Md5: true, Crc32: false),
            LtfsHashAlgorithmKind.Crc32 => new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: false, XxHash128: false, XxHash64: false, Sha1: false, Md5: false, Crc32: true),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };
    }

    private static LtfsWriteSource? RefreshSourceSnapshot(LtfsWriteSource source, LtfsWriterOptions options)
    {
        if (string.IsNullOrWhiteSpace(source.SourcePath) || !File.Exists(source.SourcePath))
            return source;

        var info = new FileInfo(source.SourcePath);
        var initialLength = source.InitialLength ?? source.Length;
        var initialModifyTime = source.InitialModifyTime ?? source.ModifyTime;
        if (info.Length == initialLength && info.LastWriteTimeUtc == initialModifyTime.UtcDateTime)
            return source;

        return options.SourceChangePolicy switch
        {
            LtfsSourceChangePolicy.UpdateBeforeWrite => source with
            {
                Length = info.Length,
                CreationTime = info.CreationTimeUtc,
                ModifyTime = info.LastWriteTimeUtc,
                AccessTime = info.LastAccessTimeUtc,
                ReadOnly = info.IsReadOnly,
                InitialLength = info.Length,
                InitialModifyTime = info.LastWriteTimeUtc,
            },
            LtfsSourceChangePolicy.Skip => null,
            LtfsSourceChangePolicy.Abort => throw new LtfsWriterException($"Source file changed before write: {source.SourcePath}."),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    private static bool IsSameFile(LtfsWriteSource source, LtfsFile file)
    {
        return string.Equals(source.Name, file.Name, StringComparison.OrdinalIgnoreCase)
            && source.Length == file.Length
            && string.Equals(LtfsIndex.FormatLtfsTime(source.ModifyTime), file.ModifyTime, StringComparison.Ordinal);
    }

    private static LtfsDirectory EnsureDirectoryChain(LtfsIndex index, LtfsDirectory root, string directoryPath)
    {
        var current = root;
        if (string.IsNullOrWhiteSpace(directoryPath))
            return current;

        foreach (var part in directoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = current.Directories.FirstOrDefault(x => string.Equals(x.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                var now = DateTimeOffset.UtcNow;
                next = new LtfsDirectory
                {
                    Name = part,
                    FileUid = ++index.HighestFileUid,
                    CreationTime = LtfsIndex.FormatLtfsTime(now),
                    ChangeTime = LtfsIndex.FormatLtfsTime(now),
                    ModifyTime = LtfsIndex.FormatLtfsTime(now),
                    AccessTime = LtfsIndex.FormatLtfsTime(now),
                    BackupTime = LtfsIndex.FormatLtfsTime(now),
                };
                current.Directories.Add(next);
            }

            current = next;
        }

        return current;
    }

    private static (string DirectoryPath, string FileName) SplitDestination(string destinationPath)
    {
        var normalized = NormalizeLtfsRelativePath(destinationPath);
        var slash = normalized.LastIndexOf('/');
        if (slash < 0)
            return (string.Empty, normalized);

        return (normalized[..slash], normalized[(slash + 1)..]);
    }

    private static string NormalizeLtfsRelativePath(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }

    private async ValueTask WriteSourceAsync(
        string operationId,
        LtfsWriteSource source,
        LtfsFile file,
        LtfsWriterOptions options,
        LtfsWritePolicyContext writeContext,
        long bytesWrittenBeforeFile,
        Func<long, CancellationToken, ValueTask> sampleHealthAsync,
        CancellationToken cancellationToken)
    {
        if (source.Length == 0)
        {
            AddEmptyFileHashes(file, options);
            return;
        }

        var hashers = ShouldComputeHashes(options) ? LtfsFileHashSet.Create(options.Hashes ?? LtfsHashOptions.None) : null;
        var pipeline = new LtfsSourceBlockPipeline(source, checked((int)options.BlockSizeBytes), options.MemoryCacheLimitBytes, options.WriteStartWatermarkRatio, options.WriteStopWatermarkRatio);
        try
        {
            await using var input = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            var producer = pipeline.StartAsync(input, cancellationToken);
            long fileOffset = 0;
            while (await pipeline.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceBlock = await pipeline.ReadAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var block = sourceBlock.Memory;
                    await WriteDataBlockAsync(
                        operationId,
                        $"Write block for '{source.Name}' at offset {fileOffset}.",
                        block,
                        options,
                        writeContext,
                        new LtfsTapePosition(LtfsPartition.B, checked((ulong)(file.Extents[0].StartBlock + fileOffset / options.BlockSizeBytes))),
                        cancellationToken).ConfigureAwait(false);

                    hashers?.Append(block.Span);
                    fileOffset += sourceBlock.Length;
                    file.OpenForWrite = false;
                    Publish(operationId, LtfsWriterStepKind.WriteBlock, $"Wrote block for '{source.Name}'.", fileOffset, source.Length);
                    await sampleHealthAsync(bytesWrittenBeforeFile + fileOffset, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    sourceBlock.Dispose();
                }
            }

            await producer.ConfigureAwait(false);
            hashers?.ApplyTo(file);
        }
        finally
        {
            hashers?.Dispose();
            pipeline.Dispose();
        }
    }

    private ValueTask<LtfsIndex> WriteDataPartitionIndexAsync(string operationId, LtfsIndex current, LtfsWriterOptions options, LtfsTapeCommandExecutor executor, CancellationToken cancellationToken)
    {
        return WriteDataPartitionIndexAsync(operationId, current, options, executor, label: null, sources: null, reason: "checkpoint", cancellationToken);
    }

    private async ValueTask<LtfsIndex> WriteDataPartitionIndexAsync(
        string operationId,
        LtfsIndex current,
        LtfsWriterOptions options,
        LtfsTapeCommandExecutor executor,
        LtfsLabel? label,
        IReadOnlyList<LtfsWriteSource>? sources,
        string reason,
        CancellationToken cancellationToken)
    {
        Publish(operationId, LtfsWriterStepKind.WriteDataPartitionIndex, "Write data partition checkpoint index.");
        await ExecuteFilemarksWithExecutorAsync(executor, 1, options, cancellationToken).ConfigureAwait(false);
        var position = await ReadPositionWithExecutorAsync(executor, options, cancellationToken).ConfigureAwait(false);
        var checkpoint = LtfsIndexUpdater.CreateDataPartitionCheckpoint(current, InferDataPartition(current), position.Block, DateTimeOffset.UtcNow);
        await WriteIndexPayloadAsync(checkpoint, options, position, executor, cancellationToken).ConfigureAwait(false);
        await ExecuteFilemarksWithExecutorAsync(executor, 1, options, cancellationToken).ConfigureAwait(false);
        await TryExportAutosaveAsync(operationId, reason, checkpoint, label, sources, options, cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    private async ValueTask<LtfsIndex> RefreshIndexPartitionAsync(string operationId, LtfsIndex current, LtfsWriterOptions options, LtfsTapeCommandExecutor executor, CancellationToken cancellationToken)
    {
        Publish(operationId, LtfsWriterStepKind.RefreshIndexPartition, "Refresh index partition copy.");
        var dataBlock = current.Location.Partition == LtfsPartition.B
            ? current.Location.StartBlock
            : current.PreviousGenerationLocation.StartBlock;

        if ((options.Discovery?.Worm ?? false) || current.VolumeLockState == LtfsVolumeLockState.PermLocked)
            await LocateEndOfDataWithExecutorAsync(executor, LtfsPartition.A, options, cancellationToken).ConfigureAwait(false);
        else
            await LocateFilemarkWithExecutorAsync(executor, LtfsPartition.A, 3, options, cancellationToken).ConfigureAwait(false);
        await ExecuteFilemarksWithExecutorAsync(executor, 1, options, cancellationToken).ConfigureAwait(false);
        var position = await ReadPositionWithExecutorAsync(executor, options, cancellationToken).ConfigureAwait(false);
        var refreshed = LtfsIndexUpdater.CreateIndexPartitionRefresh(current, position.Block, DateTimeOffset.UtcNow);
        await WriteIndexPayloadAsync(refreshed, options, position, executor, cancellationToken).ConfigureAwait(false);
        await ExecuteFilemarksWithExecutorAsync(executor, 1, options, cancellationToken).ConfigureAwait(false);

        if (options.WriteVci)
            await WriteVciWithWormPolicyAsync(operationId, refreshed, dataBlock, options, executor, cancellationToken).ConfigureAwait(false);

        return refreshed;
    }

    private async ValueTask WriteVciWithWormPolicyAsync(string operationId, LtfsIndex index, LtfsWriterOptions options, LtfsTapeCommandExecutor executor, CancellationToken cancellationToken)
    {
        var dataBlock = index.Location.Partition == LtfsPartition.B
            || InferDataPartition(index) == LtfsPartition.A
            ? index.Location.StartBlock
            : index.PreviousGenerationLocation.StartBlock;
        await WriteVciWithWormPolicyAsync(operationId, index, dataBlock, options, executor, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteVciWithWormPolicyAsync(string operationId, LtfsIndex index, ulong dataBlock, LtfsWriterOptions options, LtfsTapeCommandExecutor executor, CancellationToken cancellationToken)
    {
        try
        {
            await WriteVciAsync(operationId, index, dataBlock, executor, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ((options.Discovery?.Worm ?? false) || index.VolumeLockState == LtfsVolumeLockState.PermLocked) && options.WormPolicy!.AllowVciFailureWarning)
        {
            Publish(operationId, LtfsWriterStepKind.Warning, $"WORM VCI update failed after stable index write: {ex.Message}", severity: KokoOperationSeverity.Warning);
        }
    }

    private async ValueTask WriteVciAsync(string operationId, LtfsIndex index, LtfsTapeCommandExecutor executor, LtfsWriterOptions options, CancellationToken cancellationToken)
    {
        var dataBlock = index.Location.Partition == LtfsPartition.B
            || InferDataPartition(index) == LtfsPartition.A
            ? index.Location.StartBlock
            : index.PreviousGenerationLocation.StartBlock;
        await WriteVciAsync(operationId, index, dataBlock, executor, options, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteVciAsync(string operationId, LtfsIndex index, ulong dataBlock, LtfsTapeCommandExecutor executor, LtfsWriterOptions options, CancellationToken cancellationToken)
    {
        Publish(operationId, LtfsWriterStepKind.WriteVci, "Write LTFS VCI MAM attributes.");
        var indexBlock = index.Location.Partition == LtfsPartition.A ? index.Location.StartBlock : (ulong?)null;
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.WriteVolumeCoherencyInformation,
            ct => device.WriteVciAsync(index.GenerationNumber, indexBlock, dataBlock, index.VolumeUuid, ct),
            LtfsTapeCommandPriority.Control,
            LtfsTapeBarrierKind.HardBarrier,
            AffectsPosition: false));
        await executor.ExecuteAsync(queue, options.TapeControl, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<LtfsIndex> ReadIndexAtAsync(string operationId, LtfsLocation location, LtfsWriterOptions options, LtfsTapeCommandExecutor executor, CancellationToken cancellationToken)
    {
        Publish(operationId, LtfsWriterStepKind.ReadStarted, $"Read LTFS index at {location.Partition}{location.StartBlock}.");
        byte[] payload;
        try
        {
            payload = await ReadToFilemarkWithExecutorAsync(executor, location.Partition, location.StartBlock, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsEncryptionRelated(ex))
        {
            await ApplyEncryptionAsync(operationId, options, cancellationToken).ConfigureAwait(false);
            payload = await ReadToFilemarkWithExecutorAsync(executor, location.Partition, location.StartBlock, options, cancellationToken).ConfigureAwait(false);
        }

        using var stream = new MemoryStream(payload, writable: false);
        return LtfsSchemaReader.Read(stream);
    }

    private async ValueTask WriteIndexPayloadAsync(LtfsIndex index, LtfsWriterOptions options, LtfsTapePosition startPosition, LtfsTapeCommandExecutor executor, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        LtfsSchemaWriter.Write(stream, index, new LtfsSchemaWriterOptions(LeaveOpen: true));
        var payload = stream.ToArray();
        var offset = 0;
        var expected = startPosition;
        while (offset < payload.Length)
        {
            var count = Math.Min(checked((int)options.BlockSizeBytes), payload.Length - offset);
            await ExecuteWriteBlockWithExecutorAsync(
                executor,
                payload.AsMemory(offset, count),
                expected,
                options,
                cancellationToken).ConfigureAwait(false);
            offset += count;
            expected = expected with { Block = expected.Block + 1 };
        }
    }

    private async ValueTask ExecuteWithPolicyAsync(
        string operationId,
        LtfsWriterStepKind step,
        string message,
        LtfsWriterOptions options,
        Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            attempt += 1;
            try
            {
                await action(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "LTFS operation failed. OperationId={OperationId}, Step={Step}, Attempt={Attempt}, Message={Message}", operationId, step, attempt, message);
                var decision = await ResolvePolicyDecisionAsync(operationId, step, message, ex, attempt, options, null, cancellationToken).ConfigureAwait(false);

                PublishPolicyDecision(operationId, step, ClassifyError(ex), decision, attempt);
                if (decision.Action == LtfsWriterRecoveryAction.Retry)
                    continue;
                if (decision.Action == LtfsWriterRecoveryAction.Ignore)
                    return;
                if (decision.Action == LtfsWriterRecoveryAction.ReloadThenRetry)
                {
                    await ReloadDriveAtDataEodAsync(operationId, LtfsWriteHealthAction.Reload, options, executor: null, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw;
            }
        }
    }

    private async ValueTask<LtfsWriterPolicyDecision> ResolvePolicyDecisionAsync(
        string operationId,
        LtfsWriterStepKind step,
        string message,
        Exception exception,
        int attempt,
        LtfsWriterOptions options,
        LtfsTapePosition? tapePosition,
        CancellationToken cancellationToken)
    {
        var kind = ClassifyError(exception);
        if (options.PolicyHandler is not null)
        {
            return await options.PolicyHandler(
                new LtfsWriterPolicyContext(operationId, step, message, exception, kind, attempt, tapePosition),
                cancellationToken).ConfigureAwait(false);
        }

        if (options.ErrorHandler is not null)
        {
            var legacy = await options.ErrorHandler(
                new LtfsWriterErrorContext(operationId, step, message, exception, attempt),
                cancellationToken).ConfigureAwait(false);
            return legacy switch
            {
                LtfsWriterErrorDecision.Retry => LtfsWriterPolicyDecision.Retry("Legacy error handler requested retry."),
                LtfsWriterErrorDecision.Ignore => LtfsWriterPolicyDecision.Ignore("Legacy error handler requested ignore."),
                _ => LtfsWriterPolicyDecision.Abort("Legacy error handler requested abort."),
            };
        }

        return kind is LtfsWriterErrorKind.EarlyWarningEndOfMedium or LtfsWriterErrorKind.EndOfMedium or LtfsWriterErrorKind.VolumeOverflow
            ? LtfsWriterPolicyDecision.Abort("End of medium or volume overflow reached.")
            : LtfsWriterPolicyDecision.Abort("No LTFS error policy handler is configured.");
    }

    private void PublishPolicyDecision(
        string operationId,
        LtfsWriterStepKind step,
        LtfsWriterErrorKind kind,
        LtfsWriterPolicyDecision decision,
        int attempt)
    {
        eventBus.Publish(new LtfsWriterPolicyDecisionEvent(operationId, step, kind, decision.Action, decision.Reason, attempt));
        Publish(operationId, LtfsWriterStepKind.Warning, $"{step} failed on attempt {attempt}. ErrorKind={kind}; Action={decision.Action}; {decision.Reason}", severity: KokoOperationSeverity.Warning);
    }

    private static LtfsWriterErrorKind ClassifyError(Exception exception)
    {
        if (exception is LtfsScsiCommandException scsi)
        {
            if (scsi.WriteProtected)
                return LtfsWriterErrorKind.WriteProtected;
            if (scsi.VolumeOverflow)
                return LtfsWriterErrorKind.VolumeOverflow;
            if (scsi.EarlyWarningEndOfMedium)
                return LtfsWriterErrorKind.EarlyWarningEndOfMedium;
            if (scsi.EndOfMedium)
                return LtfsWriterErrorKind.EndOfMedium;
            return scsi.TransportOk ? LtfsWriterErrorKind.ScsiCheckCondition : LtfsWriterErrorKind.Transport;
        }

        return exception switch
        {
            IOException or EndOfStreamException => LtfsWriterErrorKind.SourceRead,
            LtfsWriterException writer when writer.Message.Contains("autosave", StringComparison.OrdinalIgnoreCase) => LtfsWriterErrorKind.Autosave,
            LtfsWriterException writer when writer.Message.Contains("encryption", StringComparison.OrdinalIgnoreCase) => LtfsWriterErrorKind.Encryption,
            _ => LtfsWriterErrorKind.Unknown,
        };
    }

    private async ValueTask ReleaseDriveAsync(bool removalPrevented, bool reserved, LtfsWriterOptions options)
    {
        var executor = new LtfsTapeCommandExecutor();
        var encryption = options.Encryption ?? new LtfsEncryptionOptions();
        if (encryption.ClearDeviceKeyOnRelease && device is ILtfsEncryptionCapableDevice encryptionDevice)
        {
            try
            {
                var queue = new LtfsTapeCommandQueue();
                queue.Enqueue(new LtfsTapeCommand(
                    LtfsTapeCommandKind.SetEncryption,
                    ct => encryptionDevice.SetEncryptionAsync(null, ct),
                    LtfsTapeCommandPriority.Control,
                    LtfsTapeBarrierKind.HardBarrier,
                    AffectsPosition: false));
                await executor.ExecuteAsync(queue, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to clear LTFS encryption key during cleanup.");
            }
        }

        if (removalPrevented)
        {
            try
            {
                var queue = new LtfsTapeCommandQueue();
                queue.Enqueue(new LtfsTapeCommand(
                    LtfsTapeCommandKind.AllowRemoval,
                    ct => device.PreventRemovalAsync(false, ct),
                    LtfsTapeCommandPriority.Control,
                    LtfsTapeBarrierKind.HardBarrier,
                    AffectsPosition: false));
                await executor.ExecuteAsync(queue, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to allow LTFS medium removal during cleanup.");
            }
        }

        if (reserved)
        {
            try
            {
                var queue = new LtfsTapeCommandQueue();
                queue.Enqueue(new LtfsTapeCommand(
                    LtfsTapeCommandKind.ReleaseDrive,
                    ct => device.ReleaseAsync(ct),
                    LtfsTapeCommandPriority.Control,
                    LtfsTapeBarrierKind.SessionBarrier,
                    AffectsPosition: false));
                await executor.ExecuteAsync(queue, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to release LTFS drive during cleanup.");
            }
        }
    }

    private static bool IsEncryptionRelated(Exception exception)
    {
        if (exception is LtfsScsiCommandException scsi)
            return scsi.AdditionalSenseCode == 0x74 || (scsi.WriteProtected && scsi.AdditionalSenseCode is 0x2A or 0x74);

        return exception.InnerException is not null && IsEncryptionRelated(exception.InnerException);
    }

    private static bool IsHashMismatch(Exception exception)
    {
        if (exception is InvalidOperationException && exception.Message.Contains("LTFS verification failed", StringComparison.Ordinal))
            return true;

        return exception.InnerException is not null && IsHashMismatch(exception.InnerException);
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        await ReadExactlyForPipelineAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask ReadExactlyForPipelineAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[filled..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException($"Source stream ended after {filled} bytes; expected {buffer.Length} bytes.");
            filled += read;
        }
    }

    private static LtfsFile CreateIndexFile(LtfsWriteSource source, long fileUid)
    {
        return new LtfsFile
        {
            Name = source.Name,
            Length = source.Length,
            ReadOnly = source.ReadOnly,
            OpenForWrite = false,
            CreationTime = LtfsIndex.FormatLtfsTime(source.CreationTime),
            ChangeTime = LtfsIndex.FormatLtfsTime(source.ModifyTime),
            ModifyTime = LtfsIndex.FormatLtfsTime(source.ModifyTime),
            AccessTime = LtfsIndex.FormatLtfsTime(source.AccessTime),
            BackupTime = LtfsIndex.FormatLtfsTime(DateTimeOffset.UtcNow),
            FileUid = fileUid,
        };
    }

    private static void RemoveExistingFile(LtfsDirectory directory, string name)
    {
        directory.Files.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static LtfsDirectory? FindDirectoryClone(LtfsIndex index, long fileUid)
    {
        foreach (var directory in index.RootDirectories)
        {
            var match = FindDirectory(directory, fileUid);
            if (match is not null)
                return match;
        }

        return null;
    }

    private static LtfsFile? FindFile(LtfsIndex index, long fileUid)
    {
        foreach (var file in index.RootFiles)
        {
            if (file.FileUid == fileUid)
                return file;
        }

        foreach (var directory in index.RootDirectories)
        {
            var match = FindFile(directory, fileUid);
            if (match is not null)
                return match;
        }

        return null;
    }

    private static LtfsFile? FindFile(LtfsDirectory directory, long fileUid)
    {
        foreach (var file in directory.Files)
        {
            if (file.FileUid == fileUid)
                return file;
        }

        foreach (var child in directory.Directories)
        {
            var match = FindFile(child, fileUid);
            if (match is not null)
                return match;
        }

        return null;
    }

    private static LtfsPartition InferDataPartition(LtfsIndex index)
    {
        return index.Location.Partition == LtfsPartition.A
            && index.PreviousGenerationLocation.Partition == LtfsPartition.A
            ? LtfsPartition.A
            : LtfsPartition.B;
    }

    private static LtfsDirectory? FindDirectory(LtfsDirectory directory, long fileUid)
    {
        if (directory.FileUid == fileUid)
            return directory;

        foreach (var child in directory.Directories)
        {
            var match = FindDirectory(child, fileUid);
            if (match is not null)
                return match;
        }

        return null;
    }

    private static LtfsWriterOptions ValidateOptions(LtfsWriterOptions options)
    {
        if (options.BlockSizeBytes <= 0 || options.BlockSizeBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), "LTFS block size must be greater than zero and fit a single SCSI transfer buffer.");

        if (options.MemoryCacheLimitBytes < LtfsWriterOptions.MinimumMemoryCacheLimitBytes || options.MemoryCacheLimitBytes > LtfsWriterOptions.MaximumMemoryCacheLimitBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"LTFS memory cache limit must be between {LtfsWriterOptions.MinimumMemoryCacheLimitBytes} and {LtfsWriterOptions.MaximumMemoryCacheLimitBytes} bytes.");
        }

        if (options.SourceReadBufferBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Source read buffer size must be greater than zero.");
        if (options.SmallFileThresholdBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Small file threshold must be greater than zero when set.");
        if (options.WriteStopWatermarkRatio < 0 || options.WriteStartWatermarkRatio <= options.WriteStopWatermarkRatio || options.WriteStartWatermarkRatio > 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Write cache watermarks must satisfy 0 <= stop < start <= 1.");

        var autoReload = options.AutoReloadPolicy ?? new LtfsAutoReloadPolicyOptions();
        if (autoReload.LowSpeedMiBPerSecond < 0 || autoReload.HighSpeedMiBPerSecond < autoReload.LowSpeedMiBPerSecond)
            throw new ArgumentOutOfRangeException(nameof(options), "Auto reload speed band must satisfy 0 <= low <= high.");
        if (autoReload.EffectiveSustainedDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Auto reload sustained duration cannot be negative.");
        if (autoReload.EffectiveCooldown < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Auto reload cooldown cannot be negative.");
        if (autoReload.EffectiveFlushCooldown < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Auto reload flush cooldown cannot be negative.");
        if (autoReload.MaxReloadCount is < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Auto reload maximum count cannot be negative.");
        if (autoReload.CleanReloadEvery < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Clean/reload cycle cannot be negative.");
        if (autoReload.ReloadAfterFlushCount is < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Reload-after-flush count cannot be negative.");

        var throttle = options.ThrottlePolicy ?? new LtfsThrottlePolicyOptions();
        if (throttle.LimitMiBPerSecond < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Throttle limit cannot be negative.");
        if (throttle.EffectiveWindowDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Throttle window duration must be greater than zero.");
        if (throttle.EffectiveDelayGranularity <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Throttle delay granularity must be greater than zero.");

        var healthSampling = options.HealthSampling ?? new LtfsHealthSamplingOptions();
        if (healthSampling.LargeFileByteInterval is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Large-file health byte interval must be greater than zero when set.");
        if (healthSampling.LargeFileTimeInterval is { } interval && interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Large-file health time interval must be greater than zero when set.");

        var encryption = options.Encryption ?? new LtfsEncryptionOptions();
        if (encryption.Mode != LtfsEncryptionMode.Disabled && encryption.KeyProvider is null)
            throw new ArgumentException("LTFS encryption key provider is required when encryption is enabled.", nameof(options));

        var autosave = options.Autosave ?? new LtfsAutosaveOptions();
        if (autosave.Enabled && string.IsNullOrWhiteSpace(autosave.RootDirectory))
            throw new ArgumentException("LTFS autosave root directory is required when autosave is enabled.", nameof(options));
        if (autosave.RetainLastPerVolume < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "LTFS autosave retention cannot be negative.");

        var appendValidation = options.AppendValidation ?? new LtfsAppendValidationOptions();
        var eomPolicy = options.EomPolicy ?? new LtfsEomPolicyOptions();
        var wormPolicy = options.WormPolicy ?? new LtfsWormPolicyOptions();
        var capacityPolicy = options.CapacityPolicy ?? new LtfsCapacityPolicyOptions();
        var dedup = options.Dedup ?? new LtfsDedupOptions();
        if (capacityPolicy.SafetyReserveBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "LTFS capacity safety reserve cannot be negative.");
        if (capacityPolicy.CompressionRatioEstimate <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "LTFS capacity compression ratio estimate must be greater than zero.");

        return options with
        {
            CheckpointPolicy = options.CheckpointPolicy ?? new LtfsCheckpointPolicy(),
            Hashes = options.Hashes ?? LtfsHashOptions.All,
            AutoReloadPolicy = autoReload,
            ThrottlePolicy = throttle,
            HealthSampling = healthSampling,
            Encryption = encryption,
            Autosave = autosave,
            AppendValidation = appendValidation,
            EomPolicy = eomPolicy,
            WormPolicy = wormPolicy,
            CapacityPolicy = capacityPolicy,
            Dedup = dedup,
        };
    }

    private static LtfsWriterOptions ResolveOptions(LtfsWriterOptions? options, LtfsLabel? label = null)
    {
        var resolved = options ?? new LtfsWriterOptions();
        if (options is null && label?.BlockSize is > 0)
            resolved = resolved with { BlockSizeBytes = label.BlockSize };
        return ValidateOptions(resolved);
    }

    public static LtfsWriterOptions ResolvePublicOptions(LtfsWriterOptions? options, LtfsLabel? label = null) => ResolveOptions(options, label);

    private static void ValidateWriteRequest(LtfsWriteRequest request, LtfsWriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(request.Index);
        ArgumentNullException.ThrowIfNull(request.TargetDirectory);
        ArgumentNullException.ThrowIfNull(request.Sources);
        _ = options;

        foreach (var source in request.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Name))
                throw new ArgumentException("LTFS source name is required.", nameof(request));
            if (source.Length < 0)
                throw new ArgumentOutOfRangeException(nameof(request), "LTFS source length cannot be negative.");
        }
    }

    private static void ValidateExtractRequest(LtfsExtractRequest request, LtfsWriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(request.Targets);
        if (request.Targets.Any(x => x.File is null))
            throw new ArgumentException("All LTFS read targets must include a file.", nameof(request));
        if (request.Targets.Any(x => x.Operation == LtfsReadOperation.UpdateOnly))
            throw new ArgumentException("Use RunHashMaintenanceAsync for LTFS hash update targets.", nameof(request));
        _ = options;
    }

    private static void ValidateHashMaintenanceRequest(LtfsHashMaintenanceRequest request, LtfsWriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(request.Index);
        ArgumentNullException.ThrowIfNull(request.Targets);
        if (request.Targets.Any(x => x.File is null))
            throw new ArgumentException("All LTFS hash maintenance targets must include a file.", nameof(request));

        if (request.Mode == LtfsHashMaintenanceMode.UpdateOnly && !(options.Hashes?.AnyEnabled ?? false))
            throw new ArgumentException("LTFS hash update requires at least one enabled hash algorithm.", nameof(request));

        if (request.Mode is LtfsHashMaintenanceMode.ExtractOnly or LtfsHashMaintenanceMode.ExtractAndVerify
            && request.Targets.Any(x => string.IsNullOrWhiteSpace(x.DestinationPath)))
            throw new ArgumentException("LTFS extract hash maintenance requires destination paths.", nameof(request));
    }

    private static IReadOnlyList<LtfsReadTarget> BuildMaintenanceTargets(
        LtfsIndex index,
        IReadOnlyList<LtfsReadTarget> targets,
        LtfsHashMaintenanceMode mode)
    {
        var operation = mode switch
        {
            LtfsHashMaintenanceMode.VerifyOnly => LtfsReadOperation.VerifyOnly,
            LtfsHashMaintenanceMode.ExtractOnly => LtfsReadOperation.ExtractOnly,
            LtfsHashMaintenanceMode.ExtractAndVerify => LtfsReadOperation.ExtractAndVerify,
            LtfsHashMaintenanceMode.UpdateOnly => LtfsReadOperation.UpdateOnly,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        return targets
            .Select(target =>
            {
                var file = FindFile(index, target.File.FileUid)
                    ?? throw new ArgumentException($"LTFS hash maintenance target file UID {target.File.FileUid} is not present in the supplied index.", nameof(targets));
                return new LtfsReadTarget(file, target.DestinationPath, operation);
            })
            .ToArray();
    }

    private static LtfsHashMaintenanceFileResult ToHashMaintenanceResult(LtfsExtractFileResult result, LtfsHashMaintenanceMode mode)
    {
        return new LtfsHashMaintenanceFileResult(
            result.FileUid,
            result.FileName,
            mode,
            LtfsHashUpdateStatus.NotRequested,
            result.VerificationStatus,
            result.ExtractStatus,
            result.VerifiedAlgorithms,
            result.Message);
    }

    private static IReadOnlyList<LtfsReadTarget> ApplyExtractConflictPolicy(
        IReadOnlyList<LtfsReadTarget> targets,
        LtfsExtractOptions options,
        List<LtfsExtractFileResult> skippedResults)
    {
        var effective = new List<LtfsReadTarget>(targets.Count);
        foreach (var target in targets)
        {
            if (target.File.Symlink is not null && target.Operation != LtfsReadOperation.VerifyOnly)
            {
                if (HandleSymlinkTarget(target, options, skippedResults))
                    continue;
            }

            if (target.Operation == LtfsReadOperation.VerifyOnly || !File.Exists(target.DestinationPath))
            {
                effective.Add(target);
                continue;
            }

            if (options.ConflictPolicy == LtfsExtractConflictPolicy.Skip)
            {
                skippedResults.Add(CreateExtractResult(target, LtfsExtractVerificationStatus.Skipped, [], LtfsExtractFileStatus.Skipped, "Destination exists."));
                continue;
            }

            if (options.ConflictPolicy == LtfsExtractConflictPolicy.SkipIfSameLengthAndTimestamp)
            {
                if (IsSameExtractTarget(target.File, target.DestinationPath))
                {
                    skippedResults.Add(CreateExtractResult(target, LtfsExtractVerificationStatus.Skipped, [], LtfsExtractFileStatus.Skipped, "Destination length and timestamp match."));
                    continue;
                }

                throw new IOException($"LTFS extract destination exists but length/timestamp differs: {target.DestinationPath}.");
            }

            if (options.ConflictPolicy == LtfsExtractConflictPolicy.RenameWithSuffix)
            {
                effective.Add(target with { DestinationPath = NextAvailableSuffixPath(target.DestinationPath) });
                continue;
            }

            effective.Add(target);
        }

        return effective;
    }

    private static bool HandleSymlinkTarget(
        LtfsReadTarget target,
        LtfsExtractOptions options,
        List<LtfsExtractFileResult> results)
    {
        if (options.SymlinkPolicy == LtfsSymlinkRestorePolicy.Skip)
        {
            results.Add(CreateExtractResult(target, LtfsExtractVerificationStatus.NotRequested, [], LtfsExtractFileStatus.Skipped, "Symlink skipped by policy."));
            return true;
        }

        try
        {
            var directory = Path.GetDirectoryName(target.DestinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            if (options.SymlinkPolicy == LtfsSymlinkRestorePolicy.WriteTextReport)
            {
                File.WriteAllText(target.DestinationPath + ".symlink.txt", target.File.Symlink);
                results.Add(CreateExtractResult(target, LtfsExtractVerificationStatus.NotRequested, [], LtfsExtractFileStatus.Extracted, "Symlink target written as text report."));
                return true;
            }

            if (File.Exists(target.DestinationPath))
            {
                if (options.ConflictPolicy == LtfsExtractConflictPolicy.Fail)
                    throw new IOException($"LTFS symlink destination exists: {target.DestinationPath}.");
                if (options.ConflictPolicy == LtfsExtractConflictPolicy.Skip || options.ConflictPolicy == LtfsExtractConflictPolicy.SkipIfSameLengthAndTimestamp)
                {
                    results.Add(CreateExtractResult(target, LtfsExtractVerificationStatus.NotRequested, [], LtfsExtractFileStatus.Skipped, "Symlink destination exists."));
                    return true;
                }
                File.Delete(target.DestinationPath);
            }

            File.CreateSymbolicLink(target.DestinationPath, target.File.Symlink!);
            results.Add(CreateExtractResult(target, LtfsExtractVerificationStatus.NotRequested, [], LtfsExtractFileStatus.Extracted, "Symlink created."));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (options.TargetWriteErrorPolicy == LtfsTargetWriteErrorPolicy.SkipFileAndContinue)
            {
                results.Add(CreateExtractResult(target, LtfsExtractVerificationStatus.NotRequested, [], LtfsExtractFileStatus.Failed, ex.Message));
                return true;
            }

            throw;
        }
    }

    private static bool IsSameExtractTarget(LtfsFile file, string destinationPath)
    {
        var info = new FileInfo(destinationPath);
        if (!info.Exists || info.Length != file.Length)
            return false;

        if (string.IsNullOrWhiteSpace(file.ModifyTime) || !DateTimeOffset.TryParse(file.ModifyTime, out var modifyTime))
            return false;

        return info.LastWriteTimeUtc == modifyTime.UtcDateTime;
    }

    private static string NextAvailableSuffixPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; i < int.MaxValue; i++)
        {
            var candidate = Path.Combine(directory ?? string.Empty, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        throw new IOException($"Could not find available extract destination for '{path}'.");
    }

    private static LtfsExtractFileResult CreateExtractResult(
        LtfsReadTarget target,
        LtfsExtractVerificationStatus verificationStatus,
        IReadOnlyList<LtfsHashAlgorithmKind> verifiedAlgorithms,
        LtfsExtractFileStatus extractStatus,
        string? message)
    {
        return new LtfsExtractFileResult(
            target.File.FileUid,
            target.File.Name,
            target.DestinationPath,
            target.Operation,
            verificationStatus,
            verifiedAlgorithms,
            extractStatus,
            message);
    }

    private static void AddEmptyFileHashes(LtfsFile file, LtfsWriterOptions options)
    {
        if (!ShouldComputeHashes(options))
            return;

        using var hashers = LtfsFileHashSet.Create(options.Hashes ?? LtfsHashOptions.None);
        hashers.ApplyTo(file);
    }

    private static bool ShouldComputeHashes(LtfsWriterOptions options)
    {
        return options.ComputeHashes && (options.Hashes?.AnyEnabled ?? false);
    }

    private void Publish(
        string operationId,
        LtfsWriterStepKind step,
        string message,
        long? bytesProcessed = null,
        long? totalBytes = null,
        long? filesProcessed = null,
        long? totalFiles = null,
        KokoOperationSeverity severity = KokoOperationSeverity.Info)
    {
        var progress = totalBytes is > 0 && bytesProcessed is not null
            ? Math.Clamp((double)bytesProcessed.Value / totalBytes.Value, 0, 1)
            : (double?)null;
        eventBus.Publish(new LtfsWriterStepEvent(operationId, step, message, bytesProcessed, totalBytes, filesProcessed, totalFiles));
        eventBus.Publish(new KokoOperationEvent(operationId, step.ToString(), message, severity, progress));
    }

    private void PublishFailure(string operationId, LtfsWriterStepKind step, string message, Exception exception)
    {
        Log.Error(exception, "{Message} OperationId={OperationId}", message, operationId);
        Publish(operationId, step, $"{message} {exception.Message}", severity: KokoOperationSeverity.Error);
    }

    private sealed record LtfsPendingFile(LtfsWriteSource Source, LtfsDirectory Directory, LtfsFile File);

    private sealed record LtfsWritePlanState(
        LtfsIndex Index,
        long BytesWritten,
        long FilesWritten,
        LtfsIndexCounters Counters,
        bool DataPartitionIndexWritten,
        LtfsWriteCompletionKind CompletionKind = LtfsWriteCompletionKind.Completed,
        LtfsRemainingManifest? RemainingManifest = null);

    private sealed class LtfsWritePolicyContext
    {
        public LtfsWritePolicyContext(
            LtfsSlidingThroughputLimiter throttle,
            LtfsWriteHealthMonitor healthMonitor,
            LtfsHealthSamplingOptions healthSampling,
            DateTimeOffset lastHealthSampleTime,
            long lastHealthSampleBytes,
            string operationId,
            LtfsTapeCommandExecutor executor)
        {
            Throttle = throttle;
            HealthMonitor = healthMonitor;
            HealthSampling = healthSampling;
            LastHealthSampleTime = lastHealthSampleTime;
            LastHealthSampleBytes = lastHealthSampleBytes;
            OperationId = operationId;
            Executor = executor;
        }

        public LtfsSlidingThroughputLimiter Throttle { get; }

        public LtfsWriteHealthMonitor HealthMonitor { get; }

        public LtfsHealthSamplingOptions HealthSampling { get; }

        public DateTimeOffset LastHealthSampleTime { get; set; }

        public long LastHealthSampleBytes { get; set; }

        public string OperationId { get; }

        public LtfsTapeCommandExecutor Executor { get; }

        public bool EndOfMediumStopRequested { get; set; }

        public string? EndOfMediumReason { get; set; }
    }

    private sealed class LtfsTapeBlockWriteStream : Stream
    {
        private readonly ILtfsWriterDevice device;
        private readonly byte[] buffer;
        private readonly CancellationToken cancellationToken;
        private LtfsTapePosition expectedPosition;
        private int buffered;
        private bool completed;

        public LtfsTapeBlockWriteStream(ILtfsWriterDevice device, int blockSizeBytes, LtfsTapePosition startPosition, CancellationToken cancellationToken)
        {
            this.device = device;
            buffer = ArrayPool<byte>.Shared.Rent(blockSizeBytes);
            expectedPosition = startPosition;
            this.cancellationToken = cancellationToken;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public async ValueTask CompleteAsync()
        {
            if (completed)
                return;

            if (buffered > 0)
            {
                await WriteBufferedBlockAsync(cancellationToken).ConfigureAwait(false);
                buffered = 0;
            }

            completed = true;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> source)
        {
            while (source.Length > 0)
            {
                var copy = Math.Min(this.buffer.Length - buffered, source.Length);
                source[..copy].CopyTo(this.buffer.AsSpan(buffered, copy));
                buffered += copy;
                source = source[copy..];

                if (buffered != this.buffer.Length)
                    continue;

                WriteBufferedBlockAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
                buffered = 0;
            }
        }

        private async ValueTask WriteBufferedBlockAsync(CancellationToken cancellationToken)
        {
            var start = expectedPosition;
            var end = start with { Block = start.Block + 1 };
            var queue = new LtfsTapeCommandQueue();
            queue.Enqueue(new LtfsTapeCommand(
                LtfsTapeCommandKind.WriteDataBlock,
                async ct => await device.WriteBlockAsync(buffer.AsMemory(0, buffered), ct).ConfigureAwait(false),
                LtfsTapeCommandPriority.Control,
                LtfsTapeBarrierKind.HardBarrier,
                ExpectedStartPosition: start,
                ExpectedEndPosition: end,
                ReadPositionAsync: device.ReadPositionAsync));
            var executor = new LtfsTapeCommandExecutor();
            executor.SetExpectedPosition(start);
            await executor.ExecuteAsync(queue, cancellationToken).ConfigureAwait(false);
            expectedPosition = executor.ExpectedPosition ?? end;
        }

        protected override void Dispose(bool disposing)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await CompleteAsync().ConfigureAwait(false);
            ArrayPool<byte>.Shared.Return(buffer);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class LtfsSourceBlockPipeline : IDisposable
{
    private readonly LtfsWriteSource source;
    private readonly int blockSizeBytes;
    private readonly ArrayPool<byte> pool = ArrayPool<byte>.Shared;
    private readonly Channel<LtfsSourceBlock> channel;
    private readonly SemaphoreSlim occupancyChanged = new(0);
    private readonly long writeStartBytes;
    private readonly long writeStopBytes;
    private long bufferedBytes;
    private bool started;
    private volatile bool producerCompleted;

    public LtfsSourceBlockPipeline(
        LtfsWriteSource source,
        int blockSizeBytes,
        long memoryCacheLimitBytes,
        double writeStartWatermarkRatio,
        double writeStopWatermarkRatio)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        if (blockSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSizeBytes));
        if (memoryCacheLimitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(memoryCacheLimitBytes));

        this.blockSizeBytes = blockSizeBytes;
        var capacityBlocks = checked((int)Math.Max(1, Math.Min(int.MaxValue, memoryCacheLimitBytes / blockSizeBytes)));
        channel = Channel.CreateBounded<LtfsSourceBlock>(new BoundedChannelOptions(capacityBlocks)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        writeStartBytes = Math.Min(source.Length, Math.Max(1, (long)(memoryCacheLimitBytes * writeStartWatermarkRatio)));
        writeStopBytes = Math.Min(writeStartBytes, Math.Max(0, (long)(memoryCacheLimitBytes * writeStopWatermarkRatio)));
    }

    public Task StartAsync(Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Task.Run(() => ProduceAsync(input, cancellationToken), cancellationToken);
    }

    public async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken)
    {
        await WaitForGateAsync(cancellationToken).ConfigureAwait(false);
        return await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<LtfsSourceBlock> ReadAsync(CancellationToken cancellationToken)
    {
        var block = await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref bufferedBytes, -block.Length);
        SignalOccupancyChanged();
        return block;
    }

    public void Dispose()
    {
        occupancyChanged.Dispose();
        while (channel.Reader.TryRead(out var block))
            block.Dispose();
    }

    private async Task ProduceAsync(Stream input, CancellationToken cancellationToken)
    {
        try
        {
            long remaining = source.Length;
            long offset = 0;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = checked((int)Math.Min(blockSizeBytes, remaining));
                var buffer = pool.Rent(blockSizeBytes);
                try
                {
                    await LtfsWriterService.ReadExactlyForPipelineAsync(input, buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                    var block = new LtfsSourceBlock(pool, buffer, length, offset);
                    buffer = null!;
                    await channel.Writer.WriteAsync(block, cancellationToken).ConfigureAwait(false);
                    Interlocked.Add(ref bufferedBytes, length);
                    SignalOccupancyChanged();
                    remaining -= length;
                    offset += length;
                }
                finally
                {
                    if (buffer is not null)
                        pool.Return(buffer);
                }
            }

            producerCompleted = true;
            SignalOccupancyChanged();
            channel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            producerCompleted = true;
            SignalOccupancyChanged();
            channel.Writer.TryComplete(ex);
        }
    }

    private async ValueTask WaitForGateAsync(CancellationToken cancellationToken)
    {
        if (!started)
        {
            while (!producerCompleted && Interlocked.Read(ref bufferedBytes) < writeStartBytes)
                await occupancyChanged.WaitAsync(cancellationToken).ConfigureAwait(false);
            started = true;
            return;
        }

        while (!producerCompleted && Interlocked.Read(ref bufferedBytes) < writeStopBytes)
            await occupancyChanged.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void SignalOccupancyChanged()
    {
        try
        {
            occupancyChanged.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

internal sealed class LtfsSourceBlock : IDisposable
{
    private readonly ArrayPool<byte> pool;
    private byte[]? buffer;

    public LtfsSourceBlock(ArrayPool<byte> pool, byte[] buffer, int length, long fileOffset)
    {
        this.pool = pool;
        this.buffer = buffer;
        Length = length;
        FileOffset = fileOffset;
    }

    public int Length { get; }

    public long FileOffset { get; }

    public Memory<byte> Memory => (buffer ?? throw new ObjectDisposedException(nameof(LtfsSourceBlock))).AsMemory(0, Length);

    public void Dispose()
    {
        var rented = Interlocked.Exchange(ref buffer, null);
        if (rented is not null)
            pool.Return(rented);
    }
}

internal sealed class LtfsExecutorBlockReader : ILtfsBlockReader
{
    private readonly ILtfsWriterDevice device;
    private readonly LtfsTapeCommandExecutor executor;
    private readonly LtfsTapeSessionControl? control;
    private LtfsTapePosition? expectedPosition;

    public LtfsExecutorBlockReader(ILtfsWriterDevice device, LtfsTapeCommandExecutor executor, LtfsTapeSessionControl? control = null)
    {
        this.device = device ?? throw new ArgumentNullException(nameof(device));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.control = control;
    }

    public async ValueTask LocateAsync(LtfsPartition partition, long block, CancellationToken cancellationToken = default)
    {
        if (block < 0)
            throw new ArgumentOutOfRangeException(nameof(block));

        var target = new LtfsTapePosition(partition, checked((ulong)block));
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.LocateBlock,
            ct => device.LocateAsync(partition, checked((ulong)block), ct),
            LtfsTapeCommandPriority.Control,
            LtfsTapeBarrierKind.HardBarrier,
            ExpectedStartPosition: expectedPosition,
            ExpectedEndPosition: target,
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));

        if (expectedPosition is not null)
            executor.SetExpectedPosition(expectedPosition);
        await executor.ExecuteAsync(queue, control, cancellationToken).ConfigureAwait(false);
        expectedPosition = executor.ExpectedPosition ?? target;
    }

    public async ValueTask<int> ReadBlockAsync(
        LtfsPartition partition,
        long block,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (expectedPosition is null || expectedPosition.Partition != partition || expectedPosition.Block != checked((ulong)block))
            await LocateAsync(partition, block, cancellationToken).ConfigureAwait(false);

        var bytesRead = 0;
        var start = new LtfsTapePosition(partition, checked((ulong)block));
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.ReadDataBlock,
            async ct =>
            {
                var data = await device.ReadBlockAsync(buffer.Length, ct).ConfigureAwait(false);
                data.AsMemory(0, Math.Min(data.Length, buffer.Length)).CopyTo(buffer);
                bytesRead = data.Length;
            },
            LtfsTapeCommandPriority.Data,
            LtfsTapeBarrierKind.None,
            ExpectedStartPosition: start,
            ExpectedEndPosition: start with { Block = start.Block + 1 },
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));

        executor.SetExpectedPosition(start);
        await executor.ExecuteAsync(queue, control, cancellationToken).ConfigureAwait(false);
        expectedPosition = executor.ExpectedPosition;
        return bytesRead;
    }
}

public static class LtfsHashMetadata
{
    public const string Blake3Key = "ltfs.hash.blake3sum";
    public const string Sha512Key = "ltfs.hash.sha512sum";
    public const string Sha256Key = "ltfs.hash.sha256sum";
    public const string XxHash128Key = "ltfs.hash.xxhash128sum";
    public const string XxHash64Key = "ltfs.hash.xxhash3sum";
    public const string Sha1Key = "ltfs.hash.sha1sum";
    public const string Md5Key = "ltfs.hash.md5sum";
    public const string Crc32Key = "ltfs.hash.crc32sum";

    private static readonly LtfsHashAlgorithmKind[] VerificationPriority =
    [
        LtfsHashAlgorithmKind.Blake3,
        LtfsHashAlgorithmKind.Sha512,
        LtfsHashAlgorithmKind.Sha256,
        LtfsHashAlgorithmKind.XxHash128,
        LtfsHashAlgorithmKind.XxHash64,
        LtfsHashAlgorithmKind.Sha1,
        LtfsHashAlgorithmKind.Md5,
        LtfsHashAlgorithmKind.Crc32,
    ];

    public static bool TrySelectVerificationHash(
        LtfsFile file,
        LtfsHashOptions options,
        out LtfsHashAlgorithmKind algorithm,
        out string expected)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(options);

        foreach (var candidate in VerificationPriority)
        {
            if (!options.IsEnabled(candidate))
                continue;

            var value = file.GetExtendedAttribute(GetKey(candidate));
            if (!string.IsNullOrWhiteSpace(value))
            {
                algorithm = candidate;
                expected = NormalizeHash(value);
                return true;
            }
        }

        algorithm = default;
        expected = string.Empty;
        return false;
    }

    public static string GetKey(LtfsHashAlgorithmKind algorithm) => algorithm switch
    {
        LtfsHashAlgorithmKind.Blake3 => Blake3Key,
        LtfsHashAlgorithmKind.Sha512 => Sha512Key,
        LtfsHashAlgorithmKind.Sha256 => Sha256Key,
        LtfsHashAlgorithmKind.XxHash128 => XxHash128Key,
        LtfsHashAlgorithmKind.XxHash64 => XxHash64Key,
        LtfsHashAlgorithmKind.Sha1 => Sha1Key,
        LtfsHashAlgorithmKind.Md5 => Md5Key,
        LtfsHashAlgorithmKind.Crc32 => Crc32Key,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    public static bool IsHashKey(string key)
    {
        return string.Equals(key, Blake3Key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, Sha512Key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, Sha256Key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, XxHash128Key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, XxHash64Key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, Sha1Key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, Md5Key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, Crc32Key, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<(LtfsHashAlgorithmKind Algorithm, string Expected)> GetExpectedHashes(
        LtfsFile file,
        LtfsHashOptions options)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(options);

        var hashes = new List<(LtfsHashAlgorithmKind Algorithm, string Expected)>();
        foreach (var candidate in VerificationPriority)
        {
            if (!options.IsEnabled(candidate))
                continue;

            var value = file.GetExtendedAttribute(GetKey(candidate));
            if (!string.IsNullOrWhiteSpace(value))
                hashes.Add((candidate, NormalizeHash(value)));
        }

        return hashes;
    }

    public static string NormalizeHash(string value)
    {
        return value.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("|", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();
    }
}

internal sealed class LtfsDedupCatalog
{
    private readonly Dictionary<long, List<LtfsFile>> filesByLength = [];

    private LtfsDedupCatalog(LtfsDedupOptions options)
    {
        Enabled = options.Enabled;
        Algorithm = options.Algorithm;
    }

    public bool Enabled { get; }

    public LtfsHashAlgorithmKind Algorithm { get; }

    public static LtfsDedupCatalog Build(LtfsIndex index, LtfsDedupOptions options)
    {
        var catalog = new LtfsDedupCatalog(options);
        if (!catalog.Enabled)
            return catalog;

        foreach (var file in EnumerateFiles(index))
            catalog.Add(file);

        return catalog;
    }

    public bool HasCandidates(long length)
    {
        return filesByLength.TryGetValue(length, out var files) && files.Count != 0;
    }

    public void Add(LtfsFile file)
    {
        if (!Enabled || file.Length <= 0 || file.Symlink is not null)
            return;

        var hash = file.GetExtendedAttribute(LtfsHashMetadata.GetKey(Algorithm));
        if (string.IsNullOrWhiteSpace(hash) || file.Extents.Count == 0)
            return;

        if (!filesByLength.TryGetValue(file.Length, out var files))
        {
            files = [];
            filesByLength.Add(file.Length, files);
        }

        files.Add(file);
    }

    public bool TryFind(long length, string hash, out LtfsFile file)
    {
        if (filesByLength.TryGetValue(length, out var files))
        {
            foreach (var candidate in files)
            {
                var candidateHash = candidate.GetExtendedAttribute(LtfsHashMetadata.GetKey(Algorithm));
                if (candidateHash is not null && string.Equals(LtfsHashMetadata.NormalizeHash(candidateHash), hash, StringComparison.OrdinalIgnoreCase))
                {
                    file = candidate;
                    return true;
                }
            }
        }

        file = null!;
        return false;
    }

    private static IEnumerable<LtfsFile> EnumerateFiles(LtfsIndex index)
    {
        foreach (var file in index.RootFiles)
            yield return file;

        foreach (var directory in index.RootDirectories)
        {
            foreach (var file in EnumerateFiles(directory))
                yield return file;
        }
    }

    private static IEnumerable<LtfsFile> EnumerateFiles(LtfsDirectory directory)
    {
        foreach (var file in directory.Files)
            yield return file;

        foreach (var child in directory.Directories)
        {
            foreach (var file in EnumerateFiles(child))
                yield return file;
        }
    }
}

public sealed class LtfsFileHashSet : IDisposable
{
    private readonly LtfsHashOptions options;
    private readonly Hasher blake3;
    private readonly IncrementalHash? sha512;
    private readonly IncrementalHash? sha256;
    private readonly XxHash128? xxHash128;
    private readonly XxHash3? xxHash64;
    private readonly IncrementalHash? sha1;
    private readonly IncrementalHash? md5;
    private readonly Crc32? crc32;

    private LtfsFileHashSet(LtfsHashOptions options)
    {
        this.options = options;
        blake3 = options.Blake3 ? Hasher.New() : default;
        sha512 = options.Sha512 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA512) : null;
        sha256 = options.Sha256 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        xxHash128 = options.XxHash128 ? new XxHash128() : null;
        xxHash64 = options.XxHash64 ? new XxHash3() : null;
        sha1 = options.Sha1 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
        md5 = options.Md5 ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
        crc32 = options.Crc32 ? new Crc32() : null;
    }

    public static LtfsFileHashSet Create(LtfsHashOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new LtfsFileHashSet(options);
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (options.Blake3)
            blake3.Update(data);
        sha512?.AppendData(data);
        sha256?.AppendData(data);
        xxHash128?.Append(data);
        xxHash64?.Append(data);
        sha1?.AppendData(data);
        md5?.AppendData(data);
        crc32?.Append(data);
    }

    public void ApplyTo(LtfsFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (options.Blake3)
            file.SetExtendedAttribute(LtfsHashMetadata.Blake3Key, GetHex(LtfsHashAlgorithmKind.Blake3));
        if (options.Sha512)
            file.SetExtendedAttribute(LtfsHashMetadata.Sha512Key, GetHex(LtfsHashAlgorithmKind.Sha512));
        if (options.Sha256)
            file.SetExtendedAttribute(LtfsHashMetadata.Sha256Key, GetHex(LtfsHashAlgorithmKind.Sha256));
        if (options.XxHash128)
            file.SetExtendedAttribute(LtfsHashMetadata.XxHash128Key, GetHex(LtfsHashAlgorithmKind.XxHash128));
        if (options.XxHash64)
            file.SetExtendedAttribute(LtfsHashMetadata.XxHash64Key, GetHex(LtfsHashAlgorithmKind.XxHash64));
        if (options.Sha1)
            file.SetExtendedAttribute(LtfsHashMetadata.Sha1Key, GetHex(LtfsHashAlgorithmKind.Sha1));
        if (options.Md5)
            file.SetExtendedAttribute(LtfsHashMetadata.Md5Key, GetHex(LtfsHashAlgorithmKind.Md5));
        if (options.Crc32)
            file.SetExtendedAttribute(LtfsHashMetadata.Crc32Key, GetHex(LtfsHashAlgorithmKind.Crc32));
    }

    public string GetHex(LtfsHashAlgorithmKind algorithm)
    {
        return algorithm switch
        {
            LtfsHashAlgorithmKind.Blake3 => GetBlake3Hex(),
            LtfsHashAlgorithmKind.Sha512 => Convert.ToHexString(sha512?.GetHashAndReset() ?? throw MissingHasher(algorithm)),
            LtfsHashAlgorithmKind.Sha256 => Convert.ToHexString(sha256?.GetHashAndReset() ?? throw MissingHasher(algorithm)),
            LtfsHashAlgorithmKind.XxHash128 => Convert.ToHexString(xxHash128?.GetCurrentHash() ?? throw MissingHasher(algorithm)),
            LtfsHashAlgorithmKind.XxHash64 => Convert.ToHexString(xxHash64?.GetCurrentHash() ?? throw MissingHasher(algorithm)),
            LtfsHashAlgorithmKind.Sha1 => Convert.ToHexString(sha1?.GetHashAndReset() ?? throw MissingHasher(algorithm)),
            LtfsHashAlgorithmKind.Md5 => Convert.ToHexString(md5?.GetHashAndReset() ?? throw MissingHasher(algorithm)),
            LtfsHashAlgorithmKind.Crc32 => Convert.ToHexString(crc32?.GetCurrentHash() ?? throw MissingHasher(algorithm)),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };
    }

    public void Dispose()
    {
        if (options.Blake3)
            blake3.Dispose();
        sha512?.Dispose();
        sha256?.Dispose();
        sha1?.Dispose();
        md5?.Dispose();
    }

    private string GetBlake3Hex()
    {
        Span<byte> hash = stackalloc byte[Hash.Size];
        blake3.Finalize(hash);
        return Convert.ToHexString(hash);
    }

    private static InvalidOperationException MissingHasher(LtfsHashAlgorithmKind algorithm)
    {
        return new InvalidOperationException($"Hash algorithm {algorithm} is not enabled.");
    }
}

public sealed class LtfsHashUpdateReadSink : ILtfsReadSink, IAsyncDisposable
{
    private readonly IReadOnlyList<LtfsReadTarget> targets;
    private readonly LtfsHashOptions hashOptions;
    private readonly Dictionary<long, LtfsFile> files;
    private readonly Dictionary<long, LtfsFileHashSet> hashers = [];
    private readonly Dictionary<long, LtfsHashMaintenanceFileResult> results = [];
    private readonly HashSet<long> completedFiles = [];

    public LtfsHashUpdateReadSink(IReadOnlyList<LtfsReadTarget> targets, LtfsHashOptions hashOptions)
    {
        this.targets = targets ?? throw new ArgumentNullException(nameof(targets));
        this.hashOptions = hashOptions ?? throw new ArgumentNullException(nameof(hashOptions));
        files = targets.ToDictionary(x => x.File.FileUid, x => x.File);
        TotalBytes = targets.Where(x => x.File.Symlink is null).Sum(x => x.File.Length);

        foreach (var target in targets)
        {
            var file = target.File;
            if (file.Symlink is not null)
            {
                results[file.FileUid] = CreateResult(target, LtfsHashUpdateStatus.Skipped, [], "Symlink hash update is skipped.");
                completedFiles.Add(file.FileUid);
                continue;
            }

            if (!hashOptions.AnyEnabled)
            {
                results[file.FileUid] = CreateResult(target, LtfsHashUpdateStatus.NoEnabledHash, [], "No hash algorithm is enabled.");
                completedFiles.Add(file.FileUid);
                continue;
            }

            results[file.FileUid] = CreateResult(target, LtfsHashUpdateStatus.NotRequested, [], null);
            if (file.Length > 0)
                hashers[file.FileUid] = LtfsFileHashSet.Create(hashOptions);
        }
    }

    public long BytesRead { get; private set; }
    public long TotalBytes { get; }
    public long FilesCompleted => completedFiles.Count;

    public void ApplyEmptyFileHashes()
    {
        foreach (var target in targets)
        {
            if (target.File.Length != 0 || target.File.Symlink is not null || completedFiles.Contains(target.File.FileUid))
                continue;

            using var hasher = LtfsFileHashSet.Create(hashOptions);
            CompleteFile(target, hasher);
        }
    }

    public ValueTask ReceiveAsync(LtfsSliceConsumer consumer, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!hashers.TryGetValue(consumer.FileUid, out var hasher))
            return ValueTask.CompletedTask;

        hasher.Append(data.Span);
        BytesRead += data.Length;
        if (files.TryGetValue(consumer.FileUid, out var file) && consumer.FileOffset + data.Length >= file.Length)
        {
            var target = targets.Single(x => x.File.FileUid == consumer.FileUid);
            CompleteFile(target, hasher);
            hashers.Remove(consumer.FileUid);
            hasher.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void CompleteFile(LtfsReadTarget target, LtfsFileHashSet hasher)
    {
        var file = target.File;
        if (!completedFiles.Add(file.FileUid))
            return;

        var algorithms = EnabledAlgorithms(hashOptions);
        var updated = false;
        var verified = new List<LtfsHashAlgorithmKind>();
        foreach (var algorithm in algorithms)
        {
            var key = LtfsHashMetadata.GetKey(algorithm);
            var actual = LtfsHashMetadata.NormalizeHash(hasher.GetHex(algorithm));
            var existing = file.GetExtendedAttribute(key);
            if (existing is not null && !string.Equals(LtfsHashMetadata.NormalizeHash(existing), actual, StringComparison.OrdinalIgnoreCase))
            {
                results[file.FileUid] = CreateResult(target, LtfsHashUpdateStatus.Mismatch, verified, $"Existing {algorithm} hash does not match tape data.");
                return;
            }

            verified.Add(algorithm);
            if (existing is null)
            {
                file.SetExtendedAttribute(key, actual);
                updated = true;
            }
        }

        results[file.FileUid] = CreateResult(target, updated ? LtfsHashUpdateStatus.Updated : LtfsHashUpdateStatus.VerifiedExisting, verified, updated ? "Hash xattr updated." : "Enabled hash xattrs already match.");
    }

    public ValueTask DisposeAsync()
    {
        foreach (var hasher in hashers.Values)
            hasher.Dispose();
        hashers.Clear();
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<LtfsHashMaintenanceFileResult> GetResults()
    {
        return results.Values.OrderBy(x => x.FileUid).ToArray();
    }

    private static LtfsHashMaintenanceFileResult CreateResult(
        LtfsReadTarget target,
        LtfsHashUpdateStatus status,
        IReadOnlyList<LtfsHashAlgorithmKind> algorithms,
        string? message)
    {
        return new LtfsHashMaintenanceFileResult(
            target.File.FileUid,
            target.File.Name,
            LtfsHashMaintenanceMode.UpdateOnly,
            status,
            LtfsExtractVerificationStatus.NotRequested,
            LtfsExtractFileStatus.VerifiedOnly,
            algorithms,
            message);
    }

    private static IReadOnlyList<LtfsHashAlgorithmKind> EnabledAlgorithms(LtfsHashOptions options)
    {
        return Enum.GetValues<LtfsHashAlgorithmKind>()
            .Where(options.IsEnabled)
            .ToArray();
    }
}

public sealed class FileSystemLtfsReadSink : ILtfsReadSink, IAsyncDisposable
{
    private readonly string operationId;
    private readonly IKokoEventBus eventBus;
    private readonly LtfsExtractOptions options;
    private readonly Dictionary<long, FileStream> streams = [];
    private readonly Dictionary<long, LtfsFile> files;
    private readonly Dictionary<long, LtfsReadTarget> targets;
    private readonly Dictionary<long, string> activePaths = [];
    private readonly HashSet<long> completedFiles = [];
    private readonly HashSet<long> failedExtractFiles = [];
    private readonly Dictionary<long, VerificationState> verifiers = [];
    private readonly Dictionary<long, LtfsExtractFileResult> results = [];

    public FileSystemLtfsReadSink(string operationId, IKokoEventBus eventBus, IReadOnlyList<LtfsReadTarget> targets, LtfsHashOptions hashOptions, LtfsExtractOptions? options = null)
    {
        this.operationId = operationId;
        this.eventBus = eventBus;
        this.options = options ?? new LtfsExtractOptions();
        this.targets = targets.ToDictionary(x => x.File.FileUid);
        files = targets.ToDictionary(x => x.File.FileUid, x => x.File);
        TotalBytes = targets.Sum(x => x.File.Length);

        foreach (var target in targets)
        {
            var expectedHashes = target.Operation != LtfsReadOperation.ExtractOnly && target.File.Length > 0
                ? LtfsHashMetadata.GetExpectedHashes(target.File, hashOptions)
                : [];
            if (target.Operation != LtfsReadOperation.ExtractOnly && target.File.Length > 0 && expectedHashes.Count == 0)
            {
                results[target.File.FileUid] = CreateResult(
                    target,
                    LtfsExtractVerificationStatus.NoExpectedHash,
                    [],
                    target.Operation == LtfsReadOperation.VerifyOnly ? LtfsExtractFileStatus.VerifiedOnly : LtfsExtractFileStatus.Pending,
                    "No enabled expected hash is present.");
                continue;
            }

            if (expectedHashes.Count != 0)
            {
                verifiers[target.File.FileUid] = new VerificationState(
                    expectedHashes,
                    LtfsFileHashSet.Create(HashOptionsFor(expectedHashes.Select(x => x.Algorithm))));
            }

            results[target.File.FileUid] = CreateResult(
                target,
                target.Operation == LtfsReadOperation.ExtractOnly ? LtfsExtractVerificationStatus.NotRequested : LtfsExtractVerificationStatus.Verified,
                [],
                target.Operation == LtfsReadOperation.VerifyOnly ? LtfsExtractFileStatus.VerifiedOnly : LtfsExtractFileStatus.Pending,
                null);
        }
    }

    public long BytesRead { get; private set; }
    public long TotalBytes { get; }
    public long FilesCompleted => completedFiles.Count;

    public async ValueTask ReceiveAsync(LtfsSliceConsumer consumer, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (consumer.Operation != LtfsReadOperation.VerifyOnly)
        {
            if (!failedExtractFiles.Contains(consumer.FileUid))
            {
                try
                {
                    var stream = GetStream(consumer, files[consumer.FileUid]);
                    await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsTargetWriteException(ex) && options.TargetWriteErrorPolicy == LtfsTargetWriteErrorPolicy.SkipFileAndContinue)
                {
                    MarkExtractFailed(consumer.FileUid, ex.Message);
                }
            }
        }

        if (verifiers.TryGetValue(consumer.FileUid, out var verifier))
            verifier.HashSet.Append(data.Span);

        BytesRead += data.Length;
        if (files.TryGetValue(consumer.FileUid, out var file) && consumer.FileOffset + data.Length >= file.Length)
        {
            if (!completedFiles.Add(consumer.FileUid))
                return;

            if (verifiers.TryGetValue(consumer.FileUid, out verifier))
            {
                var verified = new List<LtfsHashAlgorithmKind>();
                foreach (var expectedHash in verifier.ExpectedHashes)
                {
                    var actual = LtfsHashMetadata.NormalizeHash(verifier.HashSet.GetHex(expectedHash.Algorithm));
                    if (!string.Equals(actual, expectedHash.Expected, StringComparison.OrdinalIgnoreCase))
                    {
                        results[consumer.FileUid] = CreateResult(
                            targets[consumer.FileUid],
                            LtfsExtractVerificationStatus.Mismatch,
                            verified,
                            consumer.Operation == LtfsReadOperation.VerifyOnly ? LtfsExtractFileStatus.VerifiedOnly : LtfsExtractFileStatus.Pending,
                            "Hash mismatch.");
                        throw new InvalidOperationException(
                            $"LTFS verification failed for '{file.Name}' using {expectedHash.Algorithm}: expected {expectedHash.Expected}, actual {actual}.");
                    }

                    verified.Add(expectedHash.Algorithm);
                }

                var current = results[consumer.FileUid];
                results[consumer.FileUid] = current with
                {
                    VerificationStatus = LtfsExtractVerificationStatus.Verified,
                    VerifiedAlgorithms = verified
                };
            }

            if (streams.TryGetValue(consumer.FileUid, out var stream))
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
                streams.Remove(consumer.FileUid);
                try
                {
                    CompleteExtractFile(consumer.FileUid, stream.Name, file);
                    if (!failedExtractFiles.Contains(consumer.FileUid))
                    {
                        var target = targets[consumer.FileUid];
                        var current = results[consumer.FileUid];
                        results[consumer.FileUid] = current with { ExtractStatus = LtfsExtractFileStatus.Extracted, DestinationPath = target.DestinationPath };
                    }
                }
                catch (Exception ex) when (IsTargetWriteException(ex) && options.TargetWriteErrorPolicy == LtfsTargetWriteErrorPolicy.SkipFileAndContinue)
                {
                    MarkExtractFailed(consumer.FileUid, ex.Message);
                }
            }
        }

        var progress = TotalBytes > 0 ? Math.Clamp((double)BytesRead / TotalBytes, 0, 1) : (double?)null;
        eventBus.Publish(new KokoOperationEvent(operationId, LtfsWriterStepKind.ReadStarted.ToString(), $"Read '{consumer.FileName}' offset {consumer.FileOffset}.", Progress: progress));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var stream in streams.Values)
            await stream.DisposeAsync().ConfigureAwait(false);
        streams.Clear();

        if (!options.KeepPartial)
        {
            foreach (var path in activePaths.Values)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        activePaths.Clear();

        foreach (var verifier in verifiers.Values)
            verifier.HashSet.Dispose();
        verifiers.Clear();
    }

    public IReadOnlyList<LtfsExtractFileResult> GetResults()
    {
        return results.Values.OrderBy(x => x.FileUid).ToArray();
    }

    private static LtfsHashOptions HashOptionsFor(IEnumerable<LtfsHashAlgorithmKind> algorithms)
    {
        var set = algorithms.ToHashSet();
        return new LtfsHashOptions(
            Blake3: set.Contains(LtfsHashAlgorithmKind.Blake3),
            Sha512: set.Contains(LtfsHashAlgorithmKind.Sha512),
            Sha256: set.Contains(LtfsHashAlgorithmKind.Sha256),
            XxHash128: set.Contains(LtfsHashAlgorithmKind.XxHash128),
            XxHash64: set.Contains(LtfsHashAlgorithmKind.XxHash64),
            Sha1: set.Contains(LtfsHashAlgorithmKind.Sha1),
            Md5: set.Contains(LtfsHashAlgorithmKind.Md5),
            Crc32: set.Contains(LtfsHashAlgorithmKind.Crc32));
    }

    private FileStream GetStream(LtfsSliceConsumer consumer, LtfsFile file)
    {
        if (streams.TryGetValue(consumer.FileUid, out var stream))
            return stream;

        var directory = Path.GetDirectoryName(consumer.DestinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(consumer.DestinationPath))
        {
            if (options.ConflictPolicy == LtfsExtractConflictPolicy.Skip)
                throw new InvalidOperationException($"LTFS extract destination already exists and skip policy was requested: {consumer.DestinationPath}.");
            if (options.ConflictPolicy == LtfsExtractConflictPolicy.Fail)
                throw new IOException($"LTFS extract destination already exists: {consumer.DestinationPath}.");
        }

        var path = CreateStagingPath(consumer.DestinationPath);
        activePaths[consumer.FileUid] = path;
        stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        stream.SetLength(file.Length);
        streams.Add(consumer.FileUid, stream);
        return stream;
    }

    private void CompleteExtractFile(long fileUid, string activePath, LtfsFile file)
    {
        var destinationPath = targets[fileUid].DestinationPath;
        if (options.RestoreTimestamps || options.RestoreReadOnly)
            ApplyFileMetadata(activePath, file, options);

        if (!string.Equals(activePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.Move(activePath, destinationPath, overwrite: options.ConflictPolicy == LtfsExtractConflictPolicy.Overwrite);
        }

        activePaths.Remove(fileUid);
    }

    private string CreateStagingPath(string destinationPath)
    {
        var stagingRoot = string.IsNullOrWhiteSpace(options.StagingDirectory)
            ? Path.GetDirectoryName(destinationPath)
            : options.StagingDirectory;
        if (string.IsNullOrWhiteSpace(stagingRoot))
            stagingRoot = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(stagingRoot);
        return Path.Combine(stagingRoot, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.partial");
    }

    private static void ApplyFileMetadata(string path, LtfsFile file, LtfsExtractOptions options)
    {
        var info = new FileInfo(path);
        if (options.RestoreTimestamps)
        {
            if (!string.IsNullOrWhiteSpace(file.CreationTime) && DateTimeOffset.TryParse(file.CreationTime, out var creation))
                info.CreationTimeUtc = creation.UtcDateTime;
            if (!string.IsNullOrWhiteSpace(file.ModifyTime) && DateTimeOffset.TryParse(file.ModifyTime, out var modify))
                info.LastWriteTimeUtc = modify.UtcDateTime;
            if (!string.IsNullOrWhiteSpace(file.AccessTime) && DateTimeOffset.TryParse(file.AccessTime, out var access))
                info.LastAccessTimeUtc = access.UtcDateTime;
        }

        if (options.RestoreReadOnly)
            info.IsReadOnly = file.ReadOnly;
    }

    private static LtfsExtractFileResult CreateResult(
        LtfsReadTarget target,
        LtfsExtractVerificationStatus status,
        IReadOnlyList<LtfsHashAlgorithmKind> verifiedAlgorithms,
        LtfsExtractFileStatus extractStatus,
        string? message)
    {
        return new LtfsExtractFileResult(
            target.File.FileUid,
            target.File.Name,
            target.DestinationPath,
            target.Operation,
            status,
            verifiedAlgorithms,
            extractStatus,
            message);
    }

    private void MarkExtractFailed(long fileUid, string message)
    {
        failedExtractFiles.Add(fileUid);
        if (streams.Remove(fileUid, out var stream))
        {
            var path = stream.Name;
            stream.Dispose();
            if (!options.KeepPartial && File.Exists(path))
                File.Delete(path);
            activePaths.Remove(fileUid);
        }

        var target = targets[fileUid];
        var current = results.TryGetValue(fileUid, out var existing)
            ? existing
            : CreateResult(target, LtfsExtractVerificationStatus.NotRequested, [], LtfsExtractFileStatus.Pending, null);
        results[fileUid] = current with { ExtractStatus = LtfsExtractFileStatus.Failed, Message = message };
    }

    private static bool IsTargetWriteException(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or NotSupportedException;
    }

    private sealed record VerificationState(IReadOnlyList<(LtfsHashAlgorithmKind Algorithm, string Expected)> ExpectedHashes, LtfsFileHashSet HashSet);
}

public sealed class ScsiLtfsWriterDevice : ILtfsWriterDevice, ILtfsEncryptionCapableDevice, ILtfsMetadataExportDevice, ILtfsModeSenseDevice
{
    private readonly IScsiDrive drive;

    public ScsiLtfsWriterDevice(IScsiDrive drive)
    {
        this.drive = drive ?? throw new ArgumentNullException(nameof(drive));
    }

    public ValueTask ReserveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ensure(ReserveUnitCommand.TryExecute(drive, new ReserveUnitCommand(Use10Byte: false), out var result), result, "RESERVE UNIT failed.");
        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ensure(ReleaseUnitCommand.TryExecute(drive, new ReleaseUnitCommand(Use10Byte: false), out var result), result, "RELEASE UNIT failed.");
        return ValueTask.CompletedTask;
    }

    public ValueTask PreventRemovalAsync(bool prevent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ensure(PreventAllowMediumRemovalCommand.TryExecute(drive, new PreventAllowMediumRemovalCommand(prevent), out var result), result, "PREVENT/ALLOW MEDIUM REMOVAL failed.");
        return ValueTask.CompletedTask;
    }

    public ValueTask TestUnitReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ensure(TestUnitReadyCommand.TryExecute(drive, new TestUnitReadyCommand(), out var result), result, "TEST UNIT READY failed.");
        return ValueTask.CompletedTask;
    }

    public ValueTask SetBlockSizeAsync(long blockSizeBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (blockSizeBytes < 0 || blockSizeBytes > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(blockSizeBytes));

        var parameterList = new byte[12];
        parameterList[2] = 0x10;
        parameterList[3] = 8;
        parameterList[9] = (byte)(blockSizeBytes >> 16);
        parameterList[10] = (byte)(blockSizeBytes >> 8);
        parameterList[11] = (byte)blockSizeBytes;

        Ensure(ModeSelectCommand.TryExecute(drive, new ModeSelectCommand(false, true, false, parameterList), out var result), result, "MODE SELECT block size failed.");
        return ValueTask.CompletedTask;
    }

    public ValueTask<LtfsPartitionModeSense> ReadPartitionModeSenseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ensure(ModeSenseCommand.TryExecute(
            drive,
            new ModeSenseCommand(false, false, ModePageControl.CurrentValues, 0x11, 0, 64),
            out var result,
            out var data), result, "MODE SENSE partition page failed.");

        return ValueTask.FromResult(ScsiLtfsFormatDevice.ToLtfsPartitionModeSense(ModeSenseDataParser.Parse6(data)));
    }

    public ValueTask LocateAsync(LtfsPartition partition, ulong block, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (block > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(block), "10-byte LOCATE supports up to 32-bit block addresses.");

        Ensure(LocateCommand.TryExecute(drive, new LocateCommand(false, false, true, ToPartitionNumber(partition), (uint)block, LocateDestinationType.LogicalObjectIdentifier, 0), out var result), result, "LOCATE failed.");
        return ValueTask.CompletedTask;
    }

    async ValueTask ILtfsBlockReader.LocateAsync(LtfsPartition partition, long block, CancellationToken cancellationToken)
    {
        if (block < 0)
            throw new ArgumentOutOfRangeException(nameof(block));
        await LocateAsync(partition, (ulong)block, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask LocateEndOfDataAsync(LtfsPartition partition, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ensure(LocateCommand.TryExecute(drive, new LocateCommand(true, false, true, ToPartitionNumber(partition), 0, LocateDestinationType.EndOfData, 0, 3600), out var result), result, "LOCATE EOD failed.");
        return ValueTask.CompletedTask;
    }

    public ValueTask LocateFilemarkAsync(LtfsPartition partition, ulong filemark, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ensure(LocateCommand.TryExecute(drive, new LocateCommand(true, false, true, ToPartitionNumber(partition), 0, LocateDestinationType.LogicalFileIdentifier, filemark, 3600), out var result), result, "LOCATE filemark failed.");
        return ValueTask.CompletedTask;
    }

    public ValueTask<LtfsTapePosition> ReadPositionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadPositionCommand.TryExecute(drive, new ReadPositionCommand(ReadPositionServiceAction.LongForm), out var longResult, out var longResponse)
            && longResult.IsGood
            && longResponse.LongForm is { } longForm
            && longForm.LogicalObjectNumberValid)
        {
            return ValueTask.FromResult(new LtfsTapePosition(FromPartitionNumber((byte)Math.Min(longForm.PartitionNumber, byte.MaxValue)), longForm.BlockNumber, longForm.FileNumber));
        }

        Ensure(ReadPositionCommand.TryExecute(drive, new ReadPositionCommand(ReadPositionServiceAction.ShortForm), out var result, out var response), result, "READ POSITION failed.");
        var shortForm = response.ShortForm ?? throw new InvalidOperationException("READ POSITION short form response was not parseable.");
        return ValueTask.FromResult(new LtfsTapePosition(FromPartitionNumber(shortForm.PartitionNumber), shortForm.FirstBlockLocation));
    }

    public ValueTask<byte[]> ReadBlockAsync(long maximumBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes <= 0 || maximumBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        Ensure(ReadCommand.TryExecute(drive, new ReadCommand(false, false, (uint)maximumBytes), out var result, out var data), result, "READ failed.");
        return ValueTask.FromResult(data);
    }

    public ValueTask AdvancePastFilemarkAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReadCommand.TryExecute(drive, new ReadCommand(false, false, 1), out var result, out var data))
            throw new InvalidOperationException("READ filemark failed at transport level.");

        if (IsFilemark(result) || data.Length == 0)
            return ValueTask.CompletedTask;

        throw new LtfsWriterException("Expected a filemark at the current tape position.");
    }

    public async ValueTask<int> ReadBlockAsync(LtfsPartition partition, long block, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await LocateAsync(partition, checked((ulong)block), cancellationToken).ConfigureAwait(false);
        var data = await ReadBlockAsync(buffer.Length, cancellationToken).ConfigureAwait(false);
        data.AsMemory(0, Math.Min(data.Length, buffer.Length)).CopyTo(buffer);
        return data.Length;
    }

    public async ValueTask<byte[]> ReadToFilemarkAsync(long blockSizeBytes, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReadCommand.TryExecute(drive, new ReadCommand(false, false, (uint)blockSizeBytes), out var result, out var data))
                throw new InvalidOperationException("READ failed at transport level.");

            if (result.IsGood && data.Length > 0)
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (IsFilemark(result) || data.Length == 0)
                break;

            Ensure(true, result, "READ failed.");
        }

        return stream.ToArray();
    }

    public ValueTask WriteBlockAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ensure(WriteCommand.TryExecute(drive, new WriteCommand(false, 0), data, out var result), result, "WRITE failed.");
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteFilemarksAsync(uint count, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ensure(WriteFilemarksCommand.TryExecute(drive, new WriteFilemarksCommand(false, count), out var result), result, "WRITE FILEMARKS failed.");
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        return WriteFilemarksAsync(0, cancellationToken);
    }

    public ValueTask LoadUnloadAsync(bool load, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ensure(LoadUnloadCommand.TryExecute(drive, new LoadUnloadCommand(false, false, false, load, 3600), out var result), result, "LOAD/UNLOAD failed.");
        return ValueTask.CompletedTask;
    }

    public ValueTask<LogSenseResponse> ReadLogSenseAsync(LogPageCode pageCode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ensure(LogSenseCommand.TryExecute(drive, new LogSenseCommand(pageCode), out var result, out var response), result, "LOG SENSE failed.");
        return ValueTask.FromResult(response);
    }

    public ValueTask SetEncryptionAsync(ReadOnlyMemory<byte>? key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = LtfsEncryptionPayloadBuilder.BuildSetEncryptionPayload(key);
        Ensure(SecurityProtocolOutCommand.TryExecute(drive, new SecurityProtocolOutCommand(0x20, 0x0010, payload), out var result), result, "SECURITY PROTOCOL OUT set encryption failed.");
        return ValueTask.CompletedTask;
    }

    public async ValueTask WriteVciAsync(ulong generation, ulong? indexPartitionBlock, ulong dataPartitionBlock, Guid volumeUuid, CancellationToken cancellationToken = default)
    {
        await WriteMamAttributesAsync(LtfsPartition.B, [new LtfsVolumeCoherencyInformation(generation, dataPartitionBlock, volumeUuid).ToMamAttribute()], cancellationToken).ConfigureAwait(false);
        if (indexPartitionBlock is { } block)
            await WriteMamAttributesAsync(LtfsPartition.A, [new LtfsVolumeCoherencyInformation(generation, block, volumeUuid).ToMamAttribute()], cancellationToken).ConfigureAwait(false);
    }

    private ValueTask WriteMamAttributesAsync(LtfsPartition partition, IReadOnlyList<MamAttribute> attributes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parameterList = WriteAttributeCommand.BuildParameterList(attributes);
        Ensure(WriteAttributeCommand.TryExecute(drive, new WriteAttributeCommand(0, ToPartitionNumber(partition), parameterList), out var result), result, "WRITE ATTRIBUTE failed.");
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<MamAttribute>> ReadMamAttributesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ensure(ReadAttributeCommand.TryExecute(
            drive,
            new ReadAttributeCommand(ServiceAction: 0, VolumeNumber: 0, PartitionNumber: 0, FirstAttributeId: 0, AllocationLength: ushort.MaxValue),
            out var result,
            out var data), result, "READ ATTRIBUTE failed.");
        return ValueTask.FromResult<IReadOnlyList<MamAttribute>>(ParseMamAttributes(data));
    }

    private static bool IsFilemark(ScsiCommandResult result)
    {
        return result.SenseData.Length >= 3 && (result.SenseData[2] & 0x80) != 0;
    }

    private static byte ToPartitionNumber(LtfsPartition partition) => partition == LtfsPartition.A ? (byte)0 : (byte)1;

    private static LtfsPartition FromPartitionNumber(byte partition) => partition == 0 ? LtfsPartition.A : LtfsPartition.B;

    private static IReadOnlyList<MamAttribute> ParseMamAttributes(ReadOnlySpan<byte> data)
    {
        var attributes = new List<MamAttribute>();
        var offset = data.Length >= 4 ? 4 : 0;
        while (offset + 5 <= data.Length)
        {
            var id = ScsiCdbWriter.ReadUInt16BigEndian(data, offset);
            var flags = data[offset + 2];
            var length = ScsiCdbWriter.ReadUInt16BigEndian(data, offset + 3);
            var valueOffset = offset + 5;
            var next = valueOffset + length;
            if (next > data.Length)
                break;

            attributes.Add(new MamAttribute(
                id,
                (MamAttributeFormat)(flags & 0x03),
                data.Slice(valueOffset, length).ToArray(),
                (flags & 0x80) != 0));
            offset = next;
        }

        return attributes;
    }

    private static void Ensure(bool transportOk, ScsiCommandResult result, string message)
    {
        if (!transportOk || !result.IsGood)
            throw new LtfsScsiCommandException(message, transportOk, result);
    }
}
