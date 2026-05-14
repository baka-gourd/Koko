using System.Buffers;
using System.IO.Hashing;
using System.Security.Cryptography;

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
    Md5
}

public sealed record LtfsHashOptions(
    bool Blake3 = true,
    bool Sha512 = true,
    bool Sha256 = true,
    bool XxHash128 = true,
    bool XxHash64 = true,
    bool Sha1 = true,
    bool Md5 = true)
{
    public static LtfsHashOptions All { get; } = new();
    public static LtfsHashOptions None { get; } = new(false, false, false, false, false, false, false);

    public bool AnyEnabled => Blake3 || Sha512 || Sha256 || XxHash128 || XxHash64 || Sha1 || Md5;

    public bool IsEnabled(LtfsHashAlgorithmKind algorithm) => algorithm switch
    {
        LtfsHashAlgorithmKind.Blake3 => Blake3,
        LtfsHashAlgorithmKind.Sha512 => Sha512,
        LtfsHashAlgorithmKind.Sha256 => Sha256,
        LtfsHashAlgorithmKind.XxHash128 => XxHash128,
        LtfsHashAlgorithmKind.XxHash64 => XxHash64,
        LtfsHashAlgorithmKind.Sha1 => Sha1,
        LtfsHashAlgorithmKind.Md5 => Md5,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };
}

public sealed record LtfsAutoReloadPolicyOptions(
    bool Enabled = false,
    double LowSpeedMiBPerSecond = 60,
    double HighSpeedMiBPerSecond = 87,
    TimeSpan? SustainedDuration = null,
    double ErrorRateThreshold = -3.7,
    TimeSpan? Cooldown = null,
    int? MaxReloadCount = null,
    int CleanReloadEvery = 3,
    bool CheckpointBeforeReload = true)
{
    public TimeSpan EffectiveSustainedDuration => SustainedDuration ?? TimeSpan.FromSeconds(3);

    public TimeSpan EffectiveCooldown => Cooldown ?? TimeSpan.Zero;
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
    CleanReload,
    Abort
}

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
    bool DryRun);

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
    bool DryRun = false);

public sealed record LtfsExtractResult(
    long BytesRead,
    long FilesRead,
    LtfsSequentialReadPlan Plan,
    bool DryRun);

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
    private DateTimeOffset? lastReloadTime;
    private int reloadCount;

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

        if (lastReloadTime is not null && now - lastReloadTime.Value < reloadOptions.EffectiveCooldown)
            return LtfsWriteHealthDecision.Continue(speed, errorRate, reloadCount);

        if (reloadOptions.MaxReloadCount is not null && reloadCount >= reloadOptions.MaxReloadCount.Value)
        {
            return new LtfsWriteHealthDecision(
                LtfsWriteHealthAction.Abort,
                "LTFS auto reload maximum count was reached.",
                speed,
                errorRate,
                reloadCount);
        }

        reloadCount += 1;
        lastReloadTime = now;
        inBandSince = null;
        var action = reloadOptions.CleanReloadEvery > 0 && reloadCount % reloadOptions.CleanReloadEvery == 0
            ? LtfsWriteHealthAction.CleanReload
            : LtfsWriteHealthAction.Reload;

        return new LtfsWriteHealthDecision(
            action,
            $"Sustained write speed {speed:F2} MiB/s and error rate {errorRate:F4} crossed auto reload policy.",
            speed,
            errorRate,
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

            try
            {
                await PreflightAsync(operationId, options, cancellationToken).ConfigureAwait(false);
                reserved = true;
                removalPrevented = true;

                await LocateToWritePositionAsync(operationId, index, options, cancellationToken).ConfigureAwait(false);

                var writeState = await WritePlannedSourcesAsync(
                    operationId,
                    index,
                    targetDirectory,
                    request.Sources,
                    request.OverwriteExisting,
                    options,
                    cancellationToken).ConfigureAwait(false);
                index = writeState.Index;
                bytesWritten = writeState.BytesWritten;
                filesWritten = writeState.FilesWritten;
                counters = writeState.Counters;
                dataIndexWritten = writeState.DataPartitionIndexWritten;

                if (options.WriteDataPartitionIndexOnComplete && counters.UnindexedBytes != 0)
                {
                    index = await WriteDataPartitionIndexAsync(operationId, index, options, request.Label, request.Sources, "checkpoint-final-data", cancellationToken).ConfigureAwait(false);
                    dataIndexWritten = true;
                }

                if (options.RefreshIndexPartitionOnComplete)
                {
                    index = await RefreshIndexPartitionAsync(operationId, index, options, cancellationToken).ConfigureAwait(false);
                    indexPartitionRefreshed = true;
                    vciWritten = options.WriteVci;
                }
                else if (options.WriteVci)
                {
                    await WriteVciAsync(operationId, index, cancellationToken).ConfigureAwait(false);
                    vciWritten = true;
                }

                await TryExportAutosaveAsync(operationId, "final", index, request.Label, request.Sources, options, cancellationToken).ConfigureAwait(false);
                Publish(operationId, LtfsWriterStepKind.Completed, "LTFS write completed.", bytesWritten, bytesWritten, filesWritten, request.Sources.Count);
                Log.Information("LTFS write completed. OperationId={OperationId}, BytesWritten={BytesWritten}, FilesWritten={FilesWritten}", operationId, bytesWritten, filesWritten);
                return new LtfsWriteResult(index, bytesWritten, filesWritten, dataIndexWritten, indexPartitionRefreshed, vciWritten, DryRun: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await TryExportAutosaveAsync(operationId, "safe-abort", index, request.Label, request.Sources, options, CancellationToken.None).ConfigureAwait(false);
                PublishFailure(operationId, LtfsWriterStepKind.Failed, "LTFS write failed.", ex);
                throw new LtfsWriterException("LTFS write failed.", ex);
            }
            finally
            {
                await ReleaseDriveAsync(removalPrevented, reserved).ConfigureAwait(false);
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
            try
            {
                await PreflightAsync(operationId, options, cancellationToken).ConfigureAwait(false);
                reserved = true;
                removalPrevented = true;

                var rolledBack = await ReadIndexAtAsync(operationId, to, options, cancellationToken).ConfigureAwait(false);
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
                await ReleaseDriveAsync(removalPrevented, reserved).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask<LtfsExtractResult> ExtractAsync(LtfsExtractRequest request, CancellationToken cancellationToken = default)
    {
        using (Log.PushMethod())
        {
            ArgumentNullException.ThrowIfNull(request);
            var options = ResolveOptions(request.Options);
            ValidateExtractRequest(request, options);

            var operationId = Guid.NewGuid().ToString("N");
            var plan = LtfsSequentialReadPlanner.CreatePlan(
                request.Targets,
                new LtfsSequentialReadPlanOptions(options.BlockSizeBytes, options.MemoryCacheLimitBytes));

            Publish(operationId, LtfsWriterStepKind.ReadStarted, $"Reading {request.Targets.Count} LTFS file(s). Memory cache limit={options.MemoryCacheLimitBytes} bytes.", totalFiles: request.Targets.Count);
            Log.Information("LTFS read started. OperationId={OperationId}, FileCount={FileCount}, CacheLimit={CacheLimit}, UsesMemorySpool={UsesMemorySpool}, UsesLocateReplay={UsesLocateReplay}", operationId, request.Targets.Count, options.MemoryCacheLimitBytes, plan.UsesMemorySpool, plan.UsesLocateReplay);

            if (request.DryRun)
                return new LtfsExtractResult(0, 0, plan, DryRun: true);

            var reserved = false;
            var removalPrevented = false;
            var sink = new FileSystemLtfsReadSink(operationId, eventBus, request.Targets, options.Hashes ?? LtfsHashOptions.None);
            try
            {
                await PreflightAsync(operationId, options, cancellationToken).ConfigureAwait(false);
                reserved = true;
                removalPrevented = true;

                await new LtfsSequentialReadExecutor(device).ExecuteAsync(plan, sink, cancellationToken).ConfigureAwait(false);
                Publish(operationId, LtfsWriterStepKind.ReadCompleted, "LTFS read completed.", sink.BytesRead, sink.TotalBytes, sink.FilesCompleted, request.Targets.Count);
                Log.Information("LTFS read completed. OperationId={OperationId}, BytesRead={BytesRead}, FilesRead={FilesRead}", operationId, sink.BytesRead, sink.FilesCompleted);
                return new LtfsExtractResult(sink.BytesRead, sink.FilesCompleted, plan, DryRun: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PublishFailure(operationId, LtfsWriterStepKind.Failed, "LTFS read failed.", ex);
                throw new LtfsWriterException("LTFS read failed.", ex);
            }
            finally
            {
                await sink.DisposeAsync().ConfigureAwait(false);
                await ReleaseDriveAsync(removalPrevented, reserved).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask PreflightAsync(string operationId, LtfsWriterOptions options, CancellationToken cancellationToken)
    {
        Publish(operationId, LtfsWriterStepKind.Preflight, "Reserve drive and set LTFS block size.");
        await ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.Preflight, "Reserve drive", options, ct => device.ReserveAsync(ct), cancellationToken).ConfigureAwait(false);
        await ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.Preflight, "Prevent medium removal", options, ct => device.PreventRemovalAsync(true, ct), cancellationToken).ConfigureAwait(false);
        await ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.Preflight, "Test unit ready", options, ct => device.TestUnitReadyAsync(ct), cancellationToken).ConfigureAwait(false);
        await ApplyEncryptionAsync(operationId, options, cancellationToken).ConfigureAwait(false);
        await ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.Preflight, "Set LTFS block size", options, ct => device.SetBlockSizeAsync(options.BlockSizeBytes, ct), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask LocateToWritePositionAsync(string operationId, LtfsIndex index, LtfsWriterOptions options, CancellationToken cancellationToken)
    {
        Publish(operationId, LtfsWriterStepKind.LocateWritePosition, "Locate LTFS data partition write position.");
        if (index.Location.Partition == LtfsPartition.A)
        {
            var restored = await ReadIndexAtAsync(operationId, index.PreviousGenerationLocation, options, cancellationToken).ConfigureAwait(false);
            index.Location = restored.Location.Clone();
            index.PreviousGenerationLocation = restored.PreviousGenerationLocation.Clone();
            await device.LocateEndOfDataAsync(LtfsPartition.B, cancellationToken).ConfigureAwait(false);
            return;
        }

        await device.LocateEndOfDataAsync(LtfsPartition.B, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<LtfsWritePlanState> WritePlannedSourcesAsync(
        string operationId,
        LtfsIndex index,
        LtfsDirectory targetDirectory,
        IReadOnlyList<LtfsWriteSource> sources,
        bool overwriteExisting,
        LtfsWriterOptions options,
        CancellationToken cancellationToken)
    {
        var totalBytes = sources.Sum(x => x.Length);
        var smallFileThreshold = options.SmallFileThresholdBytes ?? options.BlockSizeBytes;
        var counters = new LtfsIndexCounters(0, 0, DateTimeOffset.UtcNow);
        long bytesWritten = 0;
        long filesWritten = 0;
        var dataIndexWritten = false;
        var writeContext = CreateWritePolicyContext(operationId, options);

        for (var i = 0; i < sources.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = PreparePendingFile(index, targetDirectory, sources[i], overwriteExisting, options);
            if (prepared is null)
                continue;

            if (prepared.Source.Length == 0)
            {
                AddEmptyFileHashes(prepared.File, options);
                prepared.Directory.Files.Add(prepared.File);
                filesWritten += 1;
                counters = AddIndexedFile(counters, prepared.Source);
                Publish(operationId, LtfsWriterStepKind.WriteFileCompleted, $"Wrote '{prepared.File.Name}'.", bytesWritten, totalBytes, filesWritten, sources.Count);
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
                (index, counters, dataIndexWritten) = await CheckpointIfNeededAsync(operationId, index, counters, dataIndexWritten, options, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (prepared.Source.Length <= smallFileThreshold && prepared.Source.Length <= options.BlockSizeBytes)
            {
                var pack = new List<LtfsPendingFile> { prepared };
                var packedBytes = prepared.Source.Length;
                while (i + 1 < sources.Count)
                {
                    var nextCandidate = sources[i + 1];
                    if (nextCandidate.Length <= 0 || nextCandidate.Length > smallFileThreshold || nextCandidate.Length > options.BlockSizeBytes)
                        break;
                    if (packedBytes + nextCandidate.Length > options.BlockSizeBytes)
                        break;

                    var next = PreparePendingFile(index, targetDirectory, nextCandidate, overwriteExisting, options);
                    i += 1;
                    if (next is null)
                        continue;

                    pack.Add(next);
                    packedBytes += next.Source.Length;
                }

                await WritePackedSmallFilesAsync(operationId, pack, options, writeContext, cancellationToken).ConfigureAwait(false);
                foreach (var item in pack)
                {
                    item.Directory.Files.Add(item.File);
                    bytesWritten += item.Source.Length;
                    filesWritten += 1;
                    counters = AddIndexedFile(counters, item.Source);
                    Publish(operationId, LtfsWriterStepKind.WriteFileCompleted, $"Wrote '{item.File.Name}'.", bytesWritten, totalBytes, filesWritten, sources.Count);
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
                (index, counters, dataIndexWritten) = await CheckpointIfNeededAsync(operationId, index, counters, dataIndexWritten, options, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var position = await device.ReadPositionAsync(cancellationToken).ConfigureAwait(false);
            prepared.File.Extents.Add(new LtfsExtent
            {
                Partition = LtfsPartition.B,
                StartBlock = checked((long)position.Block),
                ByteOffset = 0,
                ByteCount = prepared.Source.Length,
                FileOffset = 0,
            });

            Publish(operationId, LtfsWriterStepKind.WriteFileStarted, $"Writing '{prepared.File.Name}'.", bytesWritten, totalBytes, filesWritten, sources.Count);
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

            prepared.Directory.Files.Add(prepared.File);
            bytesWritten += prepared.Source.Length;
            filesWritten += 1;
            counters = AddIndexedFile(counters, prepared.Source);
            Publish(operationId, LtfsWriterStepKind.WriteFileCompleted, $"Wrote '{prepared.File.Name}'.", bytesWritten, totalBytes, filesWritten, sources.Count);

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
            (index, counters, dataIndexWritten) = await CheckpointIfNeededAsync(operationId, index, counters, dataIndexWritten, options, cancellationToken).ConfigureAwait(false);
        }

        return new LtfsWritePlanState(index, bytesWritten, filesWritten, counters, dataIndexWritten);
    }

    private async ValueTask WritePackedSmallFilesAsync(
        string operationId,
        IReadOnlyList<LtfsPendingFile> pack,
        LtfsWriterOptions options,
        LtfsWritePolicyContext writeContext,
        CancellationToken cancellationToken)
    {
        var position = await device.ReadPositionAsync(cancellationToken).ConfigureAwait(false);
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

        await new LtfsTapeCommandExecutor().ExecuteAsync(queue, cancellationToken).ConfigureAwait(false);
        Publish(operationId, LtfsWriterStepKind.WriteBlock, $"Wrote packed block with {pack.Count} file(s).", offset, offset);
    }

    private LtfsWritePolicyContext CreateWritePolicyContext(string operationId, LtfsWriterOptions options)
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
            operationId);
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
            try
            {
                await device.WriteBlockAsync(block, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LtfsTapePosition? currentPosition = null;
                if (expectedPosition is not null)
                {
                    try
                    {
                        currentPosition = await device.ReadPositionAsync(cancellationToken).ConfigureAwait(false);
                        if (currentPosition.Partition == expectedPosition.Partition
                            && currentPosition.Block > expectedPosition.Block)
                        {
                            Publish(operationId, LtfsWriterStepKind.Warning, $"{message} failed after the tape position advanced; treating the WRITE as committed.", severity: KokoOperationSeverity.Warning);
                            return;
                        }
                    }
                    catch (Exception positionException) when (positionException is not OperationCanceledException)
                    {
                        Log.Warning(positionException, "Unable to read tape position after LTFS WRITE failure.");
                    }
                }

                var decision = await ResolvePolicyDecisionAsync(operationId, LtfsWriterStepKind.WriteBlock, message, ex, attempt, options, currentPosition, cancellationToken).ConfigureAwait(false);
                PublishPolicyDecision(operationId, LtfsWriterStepKind.WriteBlock, ClassifyError(ex), decision, attempt);
                if (decision.Action == LtfsWriterRecoveryAction.Retry)
                    continue;
                if (decision.Action == LtfsWriterRecoveryAction.Ignore)
                    return;
                if (decision.Action == LtfsWriterRecoveryAction.ReloadThenRetry)
                {
                    await ReloadDriveAtDataEodAsync(operationId, LtfsWriteHealthAction.Reload, options, cancellationToken).ConfigureAwait(false);
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

        var decision = await writeContext.HealthMonitor.SampleAsync(operationId, bytesWritten, cancellationToken).ConfigureAwait(false);
        if (decision.Action == LtfsWriteHealthAction.Continue)
            return (index, counters, dataIndexWritten);

        PublishHealthDecision(operationId, decision);

        if (decision.Action == LtfsWriteHealthAction.Abort)
            throw new LtfsWriterException(decision.Reason);

        if (decision.Action is LtfsWriteHealthAction.Reload or LtfsWriteHealthAction.CleanReload)
        {
            if (checkpointAllowed && reloadPolicy.CheckpointBeforeReload && counters.UnindexedBytes != 0)
            {
                index = await WriteDataPartitionIndexAsync(operationId, index, options, cancellationToken).ConfigureAwait(false);
                counters = new LtfsIndexCounters(0, 0, DateTimeOffset.UtcNow);
                dataIndexWritten = true;
            }

            await ReloadDriveAtDataEodAsync(operationId, decision.Action, options, cancellationToken).ConfigureAwait(false);
            return (index, counters, dataIndexWritten);
        }

        if (decision.Action == LtfsWriteHealthAction.Flush)
            await ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.HealthPolicy, decision.Reason, options, ct => device.FlushAsync(ct), cancellationToken).ConfigureAwait(false);

        return (index, counters, dataIndexWritten);
    }

    private async ValueTask ReloadDriveAtDataEodAsync(
        string operationId,
        LtfsWriteHealthAction action,
        LtfsWriterOptions options,
        CancellationToken cancellationToken)
    {
        var actionText = action == LtfsWriteHealthAction.CleanReload ? "CleanReload reload cycle" : "reload";
        Publish(operationId, LtfsWriterStepKind.HealthPolicy, $"LTFS health policy requested {actionText}; flushing and reloading drive.");
        await ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.HealthPolicy, "Flush before LTFS health reload", options, ct => device.FlushAsync(ct), cancellationToken).ConfigureAwait(false);
        await ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.HealthPolicy, "Unload before LTFS health reload", options, ct => device.LoadUnloadAsync(false, ct), cancellationToken).ConfigureAwait(false);
        await ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.HealthPolicy, "Load after LTFS health reload", options, ct => device.LoadUnloadAsync(true, ct), cancellationToken).ConfigureAwait(false);
        await ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.HealthPolicy, "Test unit ready after LTFS health reload", options, ct => device.TestUnitReadyAsync(ct), cancellationToken).ConfigureAwait(false);
        await ApplyEncryptionAsync(operationId, options, cancellationToken).ConfigureAwait(false);
        await ExecuteWithPolicyAsync(operationId, LtfsWriterStepKind.HealthPolicy, "Set LTFS block size after health reload", options, ct => device.SetBlockSizeAsync(options.BlockSizeBytes, ct), cancellationToken).ConfigureAwait(false);
        await device.LocateEndOfDataAsync(LtfsPartition.B, cancellationToken).ConfigureAwait(false);
        await device.ReadPositionAsync(cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
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
                    device as ILtfsMetadataExportDevice),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Publish(operationId, LtfsWriterStepKind.Warning, $"LTFS autosave/export failed: {ex.Message}", severity: KokoOperationSeverity.Warning);
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
        CancellationToken cancellationToken)
    {
        if (!LtfsIndexRepository.ShouldCheckpoint(counters, options.CheckpointPolicy ?? new LtfsCheckpointPolicy(), DateTimeOffset.UtcNow))
            return (index, counters, dataIndexWritten);

        index = await WriteDataPartitionIndexAsync(operationId, index, options, cancellationToken).ConfigureAwait(false);
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

        await using var input = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(checked((int)options.BlockSizeBytes));
        var hashers = ShouldComputeHashes(options) ? LtfsFileHashSet.Create(options.Hashes ?? LtfsHashOptions.None) : null;
        try
        {
            long remaining = source.Length;
            long fileOffset = 0;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = checked((int)Math.Min(options.BlockSizeBytes, remaining));
                await ReadExactlyAsync(input, buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);

                var block = buffer.AsMemory(0, toRead);
                await WriteDataBlockAsync(
                    operationId,
                    $"Write block for '{source.Name}' at offset {fileOffset}.",
                    block,
                    options,
                    writeContext,
                    new LtfsTapePosition(LtfsPartition.B, checked((ulong)(file.Extents[0].StartBlock + fileOffset / options.BlockSizeBytes))),
                    cancellationToken).ConfigureAwait(false);

                hashers?.Append(block.Span);
                fileOffset += toRead;
                remaining -= toRead;
                file.OpenForWrite = false;
                Publish(operationId, LtfsWriterStepKind.WriteBlock, $"Wrote block for '{source.Name}'.", fileOffset, source.Length);
                await sampleHealthAsync(bytesWrittenBeforeFile + fileOffset, cancellationToken).ConfigureAwait(false);
            }

            hashers?.ApplyTo(file);
        }
        finally
        {
            hashers?.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private ValueTask<LtfsIndex> WriteDataPartitionIndexAsync(string operationId, LtfsIndex current, LtfsWriterOptions options, CancellationToken cancellationToken)
    {
        return WriteDataPartitionIndexAsync(operationId, current, options, label: null, sources: null, reason: "checkpoint", cancellationToken);
    }

    private async ValueTask<LtfsIndex> WriteDataPartitionIndexAsync(
        string operationId,
        LtfsIndex current,
        LtfsWriterOptions options,
        LtfsLabel? label,
        IReadOnlyList<LtfsWriteSource>? sources,
        string reason,
        CancellationToken cancellationToken)
    {
        Publish(operationId, LtfsWriterStepKind.WriteDataPartitionIndex, "Write data partition checkpoint index.");
        await device.WriteFilemarksAsync(1, cancellationToken).ConfigureAwait(false);
        var position = await device.ReadPositionAsync(cancellationToken).ConfigureAwait(false);
        var checkpoint = LtfsIndexUpdater.CreateDataPartitionCheckpoint(current, position.Block, DateTimeOffset.UtcNow);
        await WriteIndexPayloadAsync(checkpoint, options, cancellationToken).ConfigureAwait(false);
        await device.WriteFilemarksAsync(1, cancellationToken).ConfigureAwait(false);
        await TryExportAutosaveAsync(operationId, reason, checkpoint, label, sources, options, cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    private async ValueTask<LtfsIndex> RefreshIndexPartitionAsync(string operationId, LtfsIndex current, LtfsWriterOptions options, CancellationToken cancellationToken)
    {
        Publish(operationId, LtfsWriterStepKind.RefreshIndexPartition, "Refresh index partition copy.");
        var dataBlock = current.Location.Partition == LtfsPartition.B
            ? current.Location.StartBlock
            : current.PreviousGenerationLocation.StartBlock;

        await device.LocateFilemarkAsync(LtfsPartition.A, 3, cancellationToken).ConfigureAwait(false);
        await device.WriteFilemarksAsync(1, cancellationToken).ConfigureAwait(false);
        var position = await device.ReadPositionAsync(cancellationToken).ConfigureAwait(false);
        var refreshed = LtfsIndexUpdater.CreateIndexPartitionRefresh(current, position.Block, DateTimeOffset.UtcNow);
        await WriteIndexPayloadAsync(refreshed, options, cancellationToken).ConfigureAwait(false);
        await device.WriteFilemarksAsync(1, cancellationToken).ConfigureAwait(false);

        if (options.WriteVci)
            await WriteVciAsync(operationId, refreshed, dataBlock, cancellationToken).ConfigureAwait(false);

        return refreshed;
    }

    private async ValueTask WriteVciAsync(string operationId, LtfsIndex index, CancellationToken cancellationToken)
    {
        var dataBlock = index.Location.Partition == LtfsPartition.B
            ? index.Location.StartBlock
            : index.PreviousGenerationLocation.StartBlock;
        await WriteVciAsync(operationId, index, dataBlock, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteVciAsync(string operationId, LtfsIndex index, ulong dataBlock, CancellationToken cancellationToken)
    {
        Publish(operationId, LtfsWriterStepKind.WriteVci, "Write LTFS VCI MAM attributes.");
        var indexBlock = index.Location.Partition == LtfsPartition.A ? index.Location.StartBlock : (ulong?)null;
        await device.WriteVciAsync(index.GenerationNumber, indexBlock, dataBlock, index.VolumeUuid, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<LtfsIndex> ReadIndexAtAsync(string operationId, LtfsLocation location, LtfsWriterOptions options, CancellationToken cancellationToken)
    {
        Publish(operationId, LtfsWriterStepKind.ReadStarted, $"Read LTFS index at {location.Partition}{location.StartBlock}.");
        await device.LocateAsync(location.Partition, location.StartBlock, cancellationToken).ConfigureAwait(false);
        var payload = await device.ReadToFilemarkAsync(options.BlockSizeBytes, cancellationToken).ConfigureAwait(false);
        using var stream = new MemoryStream(payload, writable: false);
        return LtfsSchemaReader.Read(stream);
    }

    private async ValueTask WriteIndexPayloadAsync(LtfsIndex index, LtfsWriterOptions options, CancellationToken cancellationToken)
    {
        await using var stream = new LtfsTapeBlockWriteStream(device, checked((int)options.BlockSizeBytes), cancellationToken);
        LtfsSchemaWriter.Write(stream, index, new LtfsSchemaWriterOptions(LeaveOpen: true));
        await stream.CompleteAsync().ConfigureAwait(false);
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
                    await ReloadDriveAtDataEodAsync(operationId, LtfsWriteHealthAction.Reload, options, cancellationToken).ConfigureAwait(false);
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

        return kind is LtfsWriterErrorKind.EndOfMedium or LtfsWriterErrorKind.VolumeOverflow
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
            if (scsi.VolumeOverflow)
                return LtfsWriterErrorKind.VolumeOverflow;
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

    private async ValueTask ReleaseDriveAsync(bool removalPrevented, bool reserved)
    {
        if (removalPrevented)
        {
            try
            {
                await device.PreventRemovalAsync(false, CancellationToken.None).ConfigureAwait(false);
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
                await device.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to release LTFS drive during cleanup.");
            }
        }
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
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
        if (autoReload.MaxReloadCount is < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Auto reload maximum count cannot be negative.");
        if (autoReload.CleanReloadEvery < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Clean/reload cycle cannot be negative.");

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

        return options with
        {
            CheckpointPolicy = options.CheckpointPolicy ?? new LtfsCheckpointPolicy(),
            Hashes = options.Hashes ?? LtfsHashOptions.All,
            AutoReloadPolicy = autoReload,
            ThrottlePolicy = throttle,
            HealthSampling = healthSampling,
            Encryption = encryption,
            Autosave = autosave,
        };
    }

    private static LtfsWriterOptions ResolveOptions(LtfsWriterOptions? options, LtfsLabel? label = null)
    {
        var resolved = options ?? new LtfsWriterOptions();
        if (options is null && label?.BlockSize is > 0)
            resolved = resolved with { BlockSizeBytes = label.BlockSize };
        return ValidateOptions(resolved);
    }

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
        _ = options;
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
        bool DataPartitionIndexWritten);

    private sealed class LtfsWritePolicyContext
    {
        public LtfsWritePolicyContext(
            LtfsSlidingThroughputLimiter throttle,
            LtfsWriteHealthMonitor healthMonitor,
            LtfsHealthSamplingOptions healthSampling,
            DateTimeOffset lastHealthSampleTime,
            long lastHealthSampleBytes,
            string operationId)
        {
            Throttle = throttle;
            HealthMonitor = healthMonitor;
            HealthSampling = healthSampling;
            LastHealthSampleTime = lastHealthSampleTime;
            LastHealthSampleBytes = lastHealthSampleBytes;
            OperationId = operationId;
        }

        public LtfsSlidingThroughputLimiter Throttle { get; }

        public LtfsWriteHealthMonitor HealthMonitor { get; }

        public LtfsHealthSamplingOptions HealthSampling { get; }

        public DateTimeOffset LastHealthSampleTime { get; set; }

        public long LastHealthSampleBytes { get; set; }

        public string OperationId { get; }
    }

    private sealed class LtfsTapeBlockWriteStream : Stream
    {
        private readonly ILtfsWriterDevice device;
        private readonly byte[] buffer;
        private readonly CancellationToken cancellationToken;
        private int buffered;
        private bool completed;

        public LtfsTapeBlockWriteStream(ILtfsWriterDevice device, int blockSizeBytes, CancellationToken cancellationToken)
        {
            this.device = device;
            buffer = ArrayPool<byte>.Shared.Rent(blockSizeBytes);
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
                await device.WriteBlockAsync(buffer.AsMemory(0, buffered), cancellationToken).ConfigureAwait(false);
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

                device.WriteBlockAsync(this.buffer.AsMemory(0, buffered), cancellationToken).AsTask().GetAwaiter().GetResult();
                buffered = 0;
            }
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

public static class LtfsHashMetadata
{
    public const string Blake3Key = "ltfs.hash.blake3sum";
    public const string Sha512Key = "ltfs.hash.sha512sum";
    public const string Sha256Key = "ltfs.hash.sha256sum";
    public const string XxHash128Key = "ltfs.hash.xxhash128sum";
    public const string XxHash64Key = "ltfs.hash.xxhash3sum";
    public const string Sha1Key = "ltfs.hash.sha1sum";
    public const string Md5Key = "ltfs.hash.md5sum";

    private static readonly LtfsHashAlgorithmKind[] VerificationPriority =
    [
        LtfsHashAlgorithmKind.Blake3,
        LtfsHashAlgorithmKind.Sha512,
        LtfsHashAlgorithmKind.Sha256,
        LtfsHashAlgorithmKind.XxHash128,
        LtfsHashAlgorithmKind.XxHash64,
        LtfsHashAlgorithmKind.Sha1,
        LtfsHashAlgorithmKind.Md5,
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
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    private static string NormalizeHash(string value)
    {
        return value.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("|", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();
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

public sealed class FileSystemLtfsReadSink : ILtfsReadSink, IAsyncDisposable
{
    private readonly string operationId;
    private readonly IKokoEventBus eventBus;
    private readonly Dictionary<long, FileStream> streams = [];
    private readonly Dictionary<long, LtfsFile> files;
    private readonly HashSet<long> completedFiles = [];
    private readonly Dictionary<long, VerificationState> verifiers = [];

    public FileSystemLtfsReadSink(string operationId, IKokoEventBus eventBus, IReadOnlyList<LtfsReadTarget> targets, LtfsHashOptions hashOptions)
    {
        this.operationId = operationId;
        this.eventBus = eventBus;
        files = targets.ToDictionary(x => x.File.FileUid, x => x.File);
        TotalBytes = targets.Sum(x => x.File.Length);

        foreach (var target in targets.Where(x => x.Operation != LtfsReadOperation.ExtractOnly && x.File.Length > 0))
        {
            if (!LtfsHashMetadata.TrySelectVerificationHash(target.File, hashOptions, out var algorithm, out var expected))
                throw new InvalidOperationException($"File '{target.File.Name}' does not contain an enabled verification hash.");

            verifiers[target.File.FileUid] = new VerificationState(
                algorithm,
                expected,
                LtfsFileHashSet.Create(HashOptionsFor(algorithm)));
        }
    }

    public long BytesRead { get; private set; }
    public long TotalBytes { get; }
    public long FilesCompleted => completedFiles.Count;

    public async ValueTask ReceiveAsync(LtfsSliceConsumer consumer, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (consumer.Operation != LtfsReadOperation.VerifyOnly)
        {
            var stream = GetStream(consumer, files[consumer.FileUid]);
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
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
                var actual = verifier.HashSet.GetHex(verifier.Algorithm);
                if (!string.Equals(actual, verifier.Expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"LTFS verification failed for '{file.Name}' using {verifier.Algorithm}: expected {verifier.Expected}, actual {actual}.");
                }
            }

            if (streams.TryGetValue(consumer.FileUid, out var stream))
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                ApplyFileTimes(stream.Name, file);
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

        foreach (var verifier in verifiers.Values)
            verifier.HashSet.Dispose();
        verifiers.Clear();
    }

    private static LtfsHashOptions HashOptionsFor(LtfsHashAlgorithmKind algorithm)
    {
        return algorithm switch
        {
            LtfsHashAlgorithmKind.Blake3 => new LtfsHashOptions(Blake3: true, Sha512: false, Sha256: false, XxHash128: false, XxHash64: false, Sha1: false, Md5: false),
            LtfsHashAlgorithmKind.Sha512 => new LtfsHashOptions(Blake3: false, Sha512: true, Sha256: false, XxHash128: false, XxHash64: false, Sha1: false, Md5: false),
            LtfsHashAlgorithmKind.Sha256 => new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: true, XxHash128: false, XxHash64: false, Sha1: false, Md5: false),
            LtfsHashAlgorithmKind.XxHash128 => new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: false, XxHash128: true, XxHash64: false, Sha1: false, Md5: false),
            LtfsHashAlgorithmKind.XxHash64 => new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: false, XxHash128: false, XxHash64: true, Sha1: false, Md5: false),
            LtfsHashAlgorithmKind.Sha1 => new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: false, XxHash128: false, XxHash64: false, Sha1: true, Md5: false),
            LtfsHashAlgorithmKind.Md5 => new LtfsHashOptions(Blake3: false, Sha512: false, Sha256: false, XxHash128: false, XxHash64: false, Sha1: false, Md5: true),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };
    }

    private FileStream GetStream(LtfsSliceConsumer consumer, LtfsFile file)
    {
        if (streams.TryGetValue(consumer.FileUid, out var stream))
            return stream;

        var directory = Path.GetDirectoryName(consumer.DestinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        stream = new FileStream(consumer.DestinationPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        stream.SetLength(file.Length);
        streams.Add(consumer.FileUid, stream);
        return stream;
    }

    private static void ApplyFileTimes(string path, LtfsFile file)
    {
        var info = new FileInfo(path);
        if (!string.IsNullOrWhiteSpace(file.CreationTime) && DateTimeOffset.TryParse(file.CreationTime, out var creation))
            info.CreationTimeUtc = creation.UtcDateTime;
        if (!string.IsNullOrWhiteSpace(file.ModifyTime) && DateTimeOffset.TryParse(file.ModifyTime, out var modify))
            info.LastWriteTimeUtc = modify.UtcDateTime;
        if (!string.IsNullOrWhiteSpace(file.AccessTime) && DateTimeOffset.TryParse(file.AccessTime, out var access))
            info.LastAccessTimeUtc = access.UtcDateTime;
        info.IsReadOnly = file.ReadOnly;
    }

    private sealed record VerificationState(LtfsHashAlgorithmKind Algorithm, string Expected, LtfsFileHashSet HashSet);
}

public sealed class ScsiLtfsWriterDevice : ILtfsWriterDevice, ILtfsEncryptionCapableDevice
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

    private static bool IsFilemark(ScsiCommandResult result)
    {
        return result.SenseData.Length >= 3 && (result.SenseData[2] & 0x80) != 0;
    }

    private static byte ToPartitionNumber(LtfsPartition partition) => partition == LtfsPartition.A ? (byte)0 : (byte)1;

    private static LtfsPartition FromPartitionNumber(byte partition) => partition == 0 ? LtfsPartition.A : LtfsPartition.B;

    private static void Ensure(bool transportOk, ScsiCommandResult result, string message)
    {
        if (!transportOk || !result.IsGood)
            throw new LtfsScsiCommandException(message, transportOk, result);
    }
}
