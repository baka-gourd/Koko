using System.Text;

using Koko.Core.Events;
using Koko.Core.Scsi;
using Koko.Core.Scsi.Commands;

namespace Koko.Core.Ltfs;

public enum LtfsPartitionMode
{
    TwoPartition,
    PartitionlessLegacy
}

public sealed record LtfsFormatRequest(
    string VolumeName,
    string? Barcode = null,
    Guid? VolumeUuid = null,
    long BlockSizeBytes = 512 * 1024,
    LtfsPartitionMode PartitionMode = LtfsPartitionMode.TwoPartition,
    bool CompressionEnabled = true,
    bool Worm = false,
    bool WriteInitialIndexPartition = true,
    bool WriteVci = true,
    bool DryRun = false,
    string? DestructiveConfirmationToken = null,
    ushort Capacity = 0xFFFF,
    ushort P0Size = 1,
    ushort P1Size = 0xFFFF,
    string Creator = "Koko.Core",
    LtfsEncryptionOptions? Encryption = null,
    LtfsAutosaveOptions? Autosave = null,
    LtfsWormPolicyOptions? WormPolicy = null);

public sealed record LtfsTapePosition(LtfsPartition Partition, ulong Block, ulong? FileNumber = null);

public sealed record LtfsFormatResult(
    LtfsLabel Label,
    LtfsIndex Index,
    ulong DataPartitionIndexBlock,
    ulong? IndexPartitionIndexBlock,
    bool VciWritten,
    bool DryRun);

public enum LtfsFormatStepKind
{
    Started,
    Preflight,
    FormatMedium,
    PartitionMedium,
    WriteMam,
    WriteDataPartitionLabel,
    WriteDataPartitionIndex,
    WriteIndexPartitionLabel,
    WriteIndexPartitionIndex,
    WriteVci,
    Completed,
    Failed
}

public sealed record LtfsFormatStepEvent(
    string OperationId,
    LtfsFormatStepKind Step,
    string Message,
    DateTimeOffset? TimestampOverride = null) : IKokoEvent
{
    public DateTimeOffset Timestamp { get; } = TimestampOverride ?? DateTimeOffset.UtcNow;
}

public interface ILtfsFormatDevice
{
    ValueTask ReserveAsync(CancellationToken cancellationToken = default);

    ValueTask ReleaseAsync(CancellationToken cancellationToken = default);

    ValueTask PreventRemovalAsync(bool prevent, CancellationToken cancellationToken = default);

    ValueTask TestUnitReadyAsync(CancellationToken cancellationToken = default);

    ValueTask<long> ReadMaximumBlockSizeAsync(CancellationToken cancellationToken = default);

    ValueTask<byte> ReadMaximumExtraPartitionCountAsync(CancellationToken cancellationToken = default);

    ValueTask SetCapacityAsync(ushort capacity, CancellationToken cancellationToken = default);

    ValueTask ConfigureTwoPartitionAsync(ushort p0Size, ushort p1Size, CancellationToken cancellationToken = default);

    ValueTask FormatMediumAsync(byte formatCode, CancellationToken cancellationToken = default);

    ValueTask SetBlockSizeAsync(long blockSizeBytes, CancellationToken cancellationToken = default);

    ValueTask LocateAsync(LtfsPartition partition, ulong block, CancellationToken cancellationToken = default);

    ValueTask<LtfsTapePosition> ReadPositionAsync(CancellationToken cancellationToken = default);

    ValueTask WriteBlockAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    ValueTask WriteFilemarksAsync(uint count, CancellationToken cancellationToken = default);

    ValueTask WriteMamAttributesAsync(LtfsPartition partition, IReadOnlyList<MamAttribute> attributes, CancellationToken cancellationToken = default);
}

public interface ILtfsWormDetectionDevice
{
    ValueTask<LogSenseResponse> ReadLogSenseAsync(LogPageCode pageCode, CancellationToken cancellationToken = default);
}

public sealed record LtfsPartitionModeSense(
    byte MaxExtraPartitionCount,
    byte AdditionalPartitionsDefined,
    long? CurrentBlockLengthBytes,
    byte[] Raw,
    byte[] PageData);

public interface ILtfsModeSenseDevice
{
    ValueTask<LtfsPartitionModeSense> ReadPartitionModeSenseAsync(CancellationToken cancellationToken = default);
}

public sealed class LtfsFormatService
{
    public const string DestructiveConfirmationToken = "FORMAT_LTFS";

    private readonly ILtfsFormatDevice device;
    private readonly IKokoEventBus eventBus;

    public LtfsFormatService(ILtfsFormatDevice device, IKokoEventBus? eventBus = null)
    {
        this.device = device ?? throw new ArgumentNullException(nameof(device));
        this.eventBus = eventBus ?? NullKokoEventBus.Instance;
    }

    public async ValueTask<LtfsFormatResult> FormatAsync(LtfsFormatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var operationId = Guid.NewGuid().ToString("N");
        Publish(operationId, LtfsFormatStepKind.Started, $"Formatting LTFS volume '{request.VolumeName}'.");

        if (request.DryRun)
            return BuildDryRunResult(request, operationId);

        var removalPrevented = false;
        var reserved = false;
        var executor = new LtfsTapeCommandExecutor();
        bool? detectedWorm;
        bool effectiveWorm;
        try
        {
            using (ScsiStartupUnitAttentionRetry.SuppressPowerOnReset(scopeName: "LTFS format preflight"))
            {
                Publish(operationId, LtfsFormatStepKind.Preflight, "Reserve drive and validate media state.");
                await ExecuteFormatCommandAsync(executor, LtfsTapeCommandKind.ReserveDrive, ct => device.ReserveAsync(ct), cancellationToken, LtfsTapeBarrierKind.SessionBarrier, affectsPosition: false).ConfigureAwait(false);
                reserved = true;
                await ExecuteFormatCommandAsync(executor, LtfsTapeCommandKind.PreventRemoval, ct => device.PreventRemovalAsync(true, ct), cancellationToken, LtfsTapeBarrierKind.SessionBarrier, affectsPosition: false).ConfigureAwait(false);
                removalPrevented = true;
                await ExecuteFormatCommandAsync(executor, LtfsTapeCommandKind.TestUnitReady, ct => device.TestUnitReadyAsync(ct), cancellationToken, LtfsTapeBarrierKind.SessionBarrier, affectsPosition: false).ConfigureAwait(false);

                var maxBlockSize = await device.ReadMaximumBlockSizeAsync(cancellationToken).ConfigureAwait(false);
                if (request.BlockSizeBytes > maxBlockSize)
                    throw new InvalidOperationException($"Requested LTFS block size {request.BlockSizeBytes} exceeds drive limit {maxBlockSize}.");

                if (request.PartitionMode == LtfsPartitionMode.TwoPartition)
                {
                    var maxExtraPartitions = await device.ReadMaximumExtraPartitionCountAsync(cancellationToken).ConfigureAwait(false);
                    if (maxExtraPartitions < 1)
                        throw new InvalidOperationException("Two-partition LTFS format requires at least one extra partition.");
                }

                detectedWorm = await DetectWormAsync(cancellationToken).ConfigureAwait(false);
                effectiveWorm = request.Worm || detectedWorm == true;
            }

            if (!effectiveWorm && request.PartitionMode == LtfsPartitionMode.TwoPartition)
            {
                Publish(operationId, LtfsFormatStepKind.Preflight, $"Set capacity proportion {request.Capacity}.");
                await ExecuteFormatCommandAsync(executor, LtfsTapeCommandKind.SetCapacity, ct => device.SetCapacityAsync(request.Capacity, ct), cancellationToken, affectsPosition: false).ConfigureAwait(false);
            }

            if (!effectiveWorm)
            {
                Publish(operationId, LtfsFormatStepKind.FormatMedium, "Initialize medium.");
                await ExecuteFormatCommandAsync(executor, LtfsTapeCommandKind.FormatMedium, ct => device.FormatMediumAsync(formatCode: 0, ct), cancellationToken, affectsPosition: false).ConfigureAwait(false);
            }
            else
            {
                Publish(operationId, LtfsFormatStepKind.FormatMedium, "WORM medium detected/requested; skip destructive medium initialization.");
            }

            if (request.PartitionMode == LtfsPartitionMode.TwoPartition)
            {
                Publish(operationId, LtfsFormatStepKind.PartitionMedium, "Configure and format two-partition LTFS layout.");
                await ExecuteFormatCommandAsync(executor, LtfsTapeCommandKind.ConfigurePartition, ct => device.ConfigureTwoPartitionAsync(request.P0Size, request.P1Size, ct), cancellationToken, affectsPosition: false).ConfigureAwait(false);
                await ExecuteFormatCommandAsync(executor, LtfsTapeCommandKind.FormatMedium, ct => device.FormatMediumAsync(formatCode: 1, ct), cancellationToken, affectsPosition: false).ConfigureAwait(false);
            }

            Publish(operationId, LtfsFormatStepKind.WriteMam, "Write LTFS application MAM attributes.");
            await WriteApplicationMamAsync(request, executor, cancellationToken).ConfigureAwait(false);

            Publish(operationId, LtfsFormatStepKind.Preflight, $"Set variable block size limit {request.BlockSizeBytes}.");
            await ExecuteFormatCommandAsync(executor, LtfsTapeCommandKind.SetBlockSize, ct => device.SetBlockSizeAsync(request.BlockSizeBytes, ct), cancellationToken, affectsPosition: false).ConfigureAwait(false);
            await ApplyEncryptionAsync(operationId, request, cancellationToken).ConfigureAwait(false);

            var formatTime = LtfsIndex.FormatLtfsTime(DateTimeOffset.UtcNow);
            var volumeUuid = request.VolumeUuid ?? Guid.NewGuid();
            var dataPartition = request.PartitionMode == LtfsPartitionMode.PartitionlessLegacy ? LtfsPartition.A : LtfsPartition.B;
            var label = CreateLabel(request, volumeUuid, formatTime, dataPartition);

            Publish(operationId, LtfsFormatStepKind.WriteDataPartitionLabel, "Write data partition VOL1 and LTFS label.");
            await WritePartitionPreambleAsync(dataPartition, label, request.Barcode, twoFilemarksAfterLabel: true, executor, cancellationToken).ConfigureAwait(false);

            var dataIndexBlock = (await ReadPositionAsync(executor, cancellationToken).ConfigureAwait(false)).Block;
            var dataIndex = CreateInitialIndex(request, label, dataPartition, dataIndexBlock, previous: null);

            Publish(operationId, LtfsFormatStepKind.WriteDataPartitionIndex, $"Write initial data partition index at {dataPartition}{dataIndexBlock}.");
            await WriteIndexAsync(dataIndex, executor, cancellationToken).ConfigureAwait(false);
            await WriteFilemarksAsync(executor, 1, cancellationToken).ConfigureAwait(false);

            ulong? indexPartitionIndexBlock = null;
            var finalIndex = dataIndex;
            if (request.PartitionMode == LtfsPartitionMode.TwoPartition)
            {
                label.LocationPartition = LtfsPartition.A;
                Publish(operationId, LtfsFormatStepKind.WriteIndexPartitionLabel, "Write index partition VOL1 and LTFS label.");
                await WritePartitionPreambleAsync(LtfsPartition.A, label, request.Barcode, twoFilemarksAfterLabel: false, executor, cancellationToken).ConfigureAwait(false);

                if (request.WriteInitialIndexPartition && !effectiveWorm)
                {
                    await WriteFilemarksAsync(executor, 1, cancellationToken).ConfigureAwait(false);
                    indexPartitionIndexBlock = (await ReadPositionAsync(executor, cancellationToken).ConfigureAwait(false)).Block;
                    finalIndex = CreateInitialIndex(request, label, LtfsPartition.A, indexPartitionIndexBlock.Value, dataIndex.Location);

                    Publish(operationId, LtfsFormatStepKind.WriteIndexPartitionIndex, $"Write initial index partition copy at A{indexPartitionIndexBlock}.");
                    await WriteIndexAsync(finalIndex, executor, cancellationToken).ConfigureAwait(false);
                    await WriteFilemarksAsync(executor, 1, cancellationToken).ConfigureAwait(false);
                }
            }

            var vciWritten = false;
            if (request.WriteVci)
            {
                Publish(operationId, LtfsFormatStepKind.WriteVci, "Write volume coherency information.");
                try
                {
                    await WriteVciAsync(finalIndex.GenerationNumber, indexPartitionIndexBlock, dataPartition, dataIndexBlock, finalIndex.VolumeUuid, executor, cancellationToken).ConfigureAwait(false);
                    vciWritten = true;
                }
                catch (Exception ex) when (effectiveWorm && (request.WormPolicy ?? new LtfsWormPolicyOptions()).AllowVciFailureWarning && ex is not OperationCanceledException)
                {
                    Publish(operationId, LtfsFormatStepKind.WriteVci, $"WORM VCI update failed after stable format index write: {ex.Message}");
                }
            }

            Publish(operationId, LtfsFormatStepKind.Completed, "LTFS format completed.");
            await TryExportAutosaveAsync(operationId, request, "format", finalIndex, label, cancellationToken).ConfigureAwait(false);
            return new LtfsFormatResult(label, finalIndex, dataIndexBlock, indexPartitionIndexBlock, vciWritten, DryRun: false);
        }
        catch (Exception ex)
        {
            Publish(operationId, LtfsFormatStepKind.Failed, ex.Message);
            throw;
        }
        finally
        {
            await ClearEncryptionOnReleaseAsync(request).ConfigureAwait(false);
            if (removalPrevented)
                await ExecuteFormatCommandAsync(executor, LtfsTapeCommandKind.AllowRemoval, ct => device.PreventRemovalAsync(false, ct), CancellationToken.None, affectsPosition: false).ConfigureAwait(false);
            if (reserved)
                await ExecuteFormatCommandAsync(executor, LtfsTapeCommandKind.ReleaseDrive, ct => device.ReleaseAsync(ct), CancellationToken.None, LtfsTapeBarrierKind.SessionBarrier, affectsPosition: false).ConfigureAwait(false);
        }
    }

    private static void ValidateRequest(LtfsFormatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.VolumeName))
            throw new ArgumentException("Volume name is required.", nameof(request));

        if (request.PartitionMode is not LtfsPartitionMode.TwoPartition and not LtfsPartitionMode.PartitionlessLegacy)
            throw new NotSupportedException($"LTFS partition mode {request.PartitionMode} is not implemented.");

        if (request.BlockSizeBytes <= 0 || request.BlockSizeBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(request), "Block size must be greater than zero and fit a SCSI transfer buffer.");

        if (!string.Equals(request.DestructiveConfirmationToken, DestructiveConfirmationToken, StringComparison.Ordinal))
            throw new InvalidOperationException($"Destructive LTFS format requires confirmation token '{DestructiveConfirmationToken}'.");

        var encryption = request.Encryption ?? new LtfsEncryptionOptions();
        if (encryption.Mode != LtfsEncryptionMode.Disabled && encryption.KeyProvider is null)
            throw new ArgumentException("LTFS encryption key provider is required when encryption is enabled.", nameof(request));

        var autosave = request.Autosave ?? new LtfsAutosaveOptions();
        if (autosave.Enabled && string.IsNullOrWhiteSpace(autosave.RootDirectory))
            throw new ArgumentException("LTFS autosave root directory is required when autosave is enabled.", nameof(request));
    }

    private LtfsFormatResult BuildDryRunResult(LtfsFormatRequest request, string operationId)
    {
        var formatTime = LtfsIndex.FormatLtfsTime(DateTimeOffset.UtcNow);
        var volumeUuid = request.VolumeUuid ?? Guid.Empty;
        var dataPartition = request.PartitionMode == LtfsPartitionMode.PartitionlessLegacy ? LtfsPartition.A : LtfsPartition.B;
        var label = CreateLabel(request, volumeUuid, formatTime, dataPartition);
        var index = CreateInitialIndex(request, label, dataPartition, 0, previous: null);
        Publish(operationId, LtfsFormatStepKind.Completed, "LTFS format dry run completed.");
        return new LtfsFormatResult(label, index, 0, null, VciWritten: false, DryRun: true);
    }

    private async ValueTask WritePartitionPreambleAsync(
        LtfsPartition partition,
        LtfsLabel label,
        string? barcode,
        bool twoFilemarksAfterLabel,
        LtfsTapeCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        await LocateAsync(executor, partition, 0, cancellationToken).ConfigureAwait(false);
        await WriteBlockAsync(executor, LtfsVol1Label.Create(barcode), cancellationToken).ConfigureAwait(false);
        await WriteFilemarksAsync(executor, 1, cancellationToken).ConfigureAwait(false);
        await WriteBlockAsync(executor, LtfsLabelWriter.ToArray(label), cancellationToken).ConfigureAwait(false);
        await WriteFilemarksAsync(executor, twoFilemarksAfterLabel ? 2u : 1u, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteIndexAsync(LtfsIndex index, LtfsTapeCommandExecutor executor, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        LtfsSchemaWriter.Write(stream, index, new LtfsSchemaWriterOptions(LeaveOpen: true));
        await WriteBlockAsync(executor, stream.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteApplicationMamAsync(LtfsFormatRequest request, LtfsTapeCommandExecutor executor, CancellationToken cancellationToken)
    {
        var attributes = new List<MamAttribute>
        {
            TextAttribute(0x0800, "OPEN", 8),
            TextAttribute(0x0801, "Koko.Core", 32),
            TextAttribute(0x0802, typeof(LtfsFormatService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", 8),
            TextAttribute(0x0803, string.Empty, 160),
            new(0x0805, MamAttributeFormat.Binary, new byte[] { 0 }),
            TextAttribute(0x0806, request.Barcode ?? string.Empty, 32),
            TextAttribute(0x080B, LtfsLabel.DefaultVersion, 16),
        };

        await ExecuteFormatCommandAsync(executor, LtfsTapeCommandKind.WriteMamAttributes, ct => device.WriteMamAttributesAsync(LtfsPartition.A, attributes, ct), cancellationToken, affectsPosition: false).ConfigureAwait(false);
    }

    private async ValueTask ExecuteFormatCommandAsync(
        LtfsTapeCommandExecutor executor,
        LtfsTapeCommandKind kind,
        Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken,
        LtfsTapeBarrierKind barrier = LtfsTapeBarrierKind.HardBarrier,
        bool affectsPosition = true)
    {
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(kind, action, LtfsTapeCommandPriority.Control, barrier, AffectsPosition: affectsPosition, ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        await executor.ExecuteAsync(queue, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask LocateAsync(LtfsTapeCommandExecutor executor, LtfsPartition partition, ulong block, CancellationToken cancellationToken)
    {
        var target = new LtfsTapePosition(partition, block);
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.LocateBlock,
            ct => device.LocateAsync(partition, block, ct),
            LtfsTapeCommandPriority.Control,
            LtfsTapeBarrierKind.HardBarrier,
            ExpectedEndPosition: target,
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        await executor.ExecuteAsync(queue, cancellationToken).ConfigureAwait(false);
        executor.SetExpectedPosition(target);
    }

    private async ValueTask<LtfsTapePosition> ReadPositionAsync(LtfsTapeCommandExecutor executor, CancellationToken cancellationToken)
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
        await executor.ExecuteAsync(queue, cancellationToken).ConfigureAwait(false);
        if (position is null)
            throw new InvalidOperationException("LTFS format READ POSITION did not return a position.");
        executor.SetExpectedPosition(position);
        return position;
    }

    private async ValueTask WriteBlockAsync(LtfsTapeCommandExecutor executor, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var start = executor.ExpectedPosition;
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.WriteDataBlock,
            ct => device.WriteBlockAsync(data, ct),
            LtfsTapeCommandPriority.Data,
            LtfsTapeBarrierKind.None,
            ExpectedStartPosition: start,
            ExpectedEndPosition: start is null ? null : start with { Block = start.Block + 1 },
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        await executor.ExecuteAsync(queue, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteFilemarksAsync(LtfsTapeCommandExecutor executor, uint count, CancellationToken cancellationToken)
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
        await executor.ExecuteAsync(queue, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ApplyEncryptionAsync(string operationId, LtfsFormatRequest request, CancellationToken cancellationToken)
    {
        var encryption = request.Encryption ?? new LtfsEncryptionOptions();
        if (encryption.Mode == LtfsEncryptionMode.Disabled)
            return;

        if (device is not ILtfsEncryptionCapableDevice encryptionDevice)
            throw new InvalidOperationException("LTFS encryption was requested but the format device does not support encryption.");

        var material = await encryption.KeyProvider!.ResolveKeyAsync(
            new LtfsEncryptionKeyRequest(operationId, encryption.Mode, encryption.KeyId),
            cancellationToken).ConfigureAwait(false);
        if (material is null)
            throw new InvalidOperationException("LTFS encryption key provider did not return key material.");
        if (material.Key.Length != 32)
            throw new InvalidOperationException("LTFS encryption key must be exactly 32 bytes.");
        if (material.Key.Span.ToArray().All(x => x == 0))
            throw new InvalidOperationException("LTFS encryption key cannot be all zero bytes.");

        await encryptionDevice.SetEncryptionAsync(material.Key, cancellationToken).ConfigureAwait(false);
        eventBus.Publish(new LtfsEncryptionEvent(operationId, "LTFS encryption key applied.", material.KeyFingerprint));
    }

    private async ValueTask ClearEncryptionOnReleaseAsync(LtfsFormatRequest request)
    {
        var encryption = request.Encryption ?? new LtfsEncryptionOptions();
        if (!encryption.ClearDeviceKeyOnRelease || device is not ILtfsEncryptionCapableDevice encryptionDevice)
            return;

        try
        {
            await encryptionDevice.SetEncryptionAsync(null, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Cleanup must not mask the primary format result.
        }
    }

    private async ValueTask<bool?> DetectWormAsync(CancellationToken cancellationToken)
    {
        if (device is not ILtfsWormDetectionDevice wormDevice)
            return null;

        try
        {
            var response = await wormDevice.ReadLogSenseAsync(LogPageCode.VolumeStatistics, cancellationToken).ConfigureAwait(false);
            return LtfsWormDetector.TryDetectFromVolumeStatistics(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private async ValueTask TryExportAutosaveAsync(
        string operationId,
        LtfsFormatRequest request,
        string reason,
        LtfsIndex index,
        LtfsLabel label,
        CancellationToken cancellationToken)
    {
        var autosave = request.Autosave ?? new LtfsAutosaveOptions();
        if (!autosave.Enabled)
            return;

        try
        {
            await new LtfsAutosaveExporter(eventBus).ExportAsync(
                new LtfsAutosaveRequest(
                    operationId,
                    reason,
                    index.Clone(),
                    label.Clone(),
                    autosave,
                    Sources: null,
                    device as ILtfsMetadataExportDevice,
                    Barcode: request.Barcode),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            eventBus.Publish(new KokoOperationEvent(operationId, LtfsFormatStepKind.Failed.ToString(), $"LTFS autosave/export failed: {ex.Message}", KokoOperationSeverity.Warning));
        }
    }

    private async ValueTask WriteVciAsync(
        ulong generation,
        ulong? indexPartitionBlock,
        LtfsPartition dataPartition,
        ulong dataPartitionBlock,
        Guid volumeUuid,
        LtfsTapeCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        await ExecuteFormatCommandAsync(
            executor,
            LtfsTapeCommandKind.WriteVolumeCoherencyInformation,
            ct => device.WriteMamAttributesAsync(
                dataPartition,
                [new LtfsVolumeCoherencyInformation(generation, dataPartitionBlock, volumeUuid).ToMamAttribute()],
                ct),
            cancellationToken,
            affectsPosition: false).ConfigureAwait(false);

        if (indexPartitionBlock is { } block)
        {
            await ExecuteFormatCommandAsync(
                executor,
                LtfsTapeCommandKind.WriteVolumeCoherencyInformation,
                ct => device.WriteMamAttributesAsync(
                    LtfsPartition.A,
                    [new LtfsVolumeCoherencyInformation(generation, block, volumeUuid).ToMamAttribute()],
                    ct),
                cancellationToken,
                affectsPosition: false).ConfigureAwait(false);
        }
    }

    private static LtfsLabel CreateLabel(LtfsFormatRequest request, Guid volumeUuid, string formatTime, LtfsPartition location)
    {
        var dataPartition = request.PartitionMode == LtfsPartitionMode.PartitionlessLegacy ? LtfsPartition.A : LtfsPartition.B;
        var indexPartition = request.PartitionMode == LtfsPartitionMode.PartitionlessLegacy ? dataPartition : LtfsPartition.A;
        return new LtfsLabel
        {
            Creator = request.Creator,
            FormatTime = formatTime,
            VolumeUuid = volumeUuid,
            LocationPartition = location,
            IndexPartition = indexPartition,
            DataPartition = dataPartition,
            BlockSize = request.BlockSizeBytes,
            Compression = request.CompressionEnabled,
        };
    }

    private static LtfsIndex CreateInitialIndex(
        LtfsFormatRequest request,
        LtfsLabel label,
        LtfsPartition locationPartition,
        ulong startBlock,
        LtfsLocation? previous)
    {
        var index = new LtfsIndex
        {
            Creator = label.Creator,
            VolumeUuid = label.VolumeUuid,
            GenerationNumber = 1,
            UpdateTime = label.FormatTime,
            Location = new LtfsLocation { Partition = locationPartition, StartBlock = startBlock },
            PreviousGenerationLocation = previous?.Clone() ?? new LtfsLocation { Partition = label.DataPartition, StartBlock = 0 },
            HighestFileUid = 1,
        };

        index.RootDirectories.Add(new LtfsDirectory
        {
            Name = request.VolumeName,
            FileUid = 1,
            CreationTime = label.FormatTime,
            ChangeTime = label.FormatTime,
            ModifyTime = label.FormatTime,
            AccessTime = label.FormatTime,
            BackupTime = label.FormatTime,
        });
        return index;
    }

    private static MamAttribute TextAttribute(ushort id, string value, int length)
    {
        return new MamAttribute(id, MamAttributeFormat.Text, Encoding.ASCII.GetBytes(value.PadRight(length)[..length]));
    }

    private void Publish(string operationId, LtfsFormatStepKind step, string message)
    {
        eventBus.Publish(new LtfsFormatStepEvent(operationId, step, message));
        eventBus.Publish(new KokoOperationEvent(operationId, step.ToString(), message));
    }
}

public sealed class ScsiLtfsFormatDevice : ILtfsFormatDevice, ILtfsEncryptionCapableDevice, ILtfsWormDetectionDevice, ILtfsModeSenseDevice
{
    private readonly IScsiDrive drive;

    public ScsiLtfsFormatDevice(IScsiDrive drive)
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

    public ValueTask<long> ReadMaximumBlockSizeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ensure(ReadBlockLimitsCommand.TryExecute(drive, new ReadBlockLimitsCommand(), out var result, out var data), result, "READ BLOCK LIMITS failed.");
        if (data.Length < 4)
            throw new InvalidOperationException("READ BLOCK LIMITS returned too little data.");

        var maximum = (data[1] << 16) | (data[2] << 8) | data[3];
        return ValueTask.FromResult((long)maximum);
    }

    public ValueTask<byte> ReadMaximumExtraPartitionCountAsync(CancellationToken cancellationToken = default)
    {
        return ReadMaximumExtraPartitionCountCoreAsync(cancellationToken);
    }

    private async ValueTask<byte> ReadMaximumExtraPartitionCountCoreAsync(CancellationToken cancellationToken)
    {
        var modeSense = await ReadPartitionModeSenseAsync(cancellationToken).ConfigureAwait(false);
        return modeSense.MaxExtraPartitionCount;
    }

    public ValueTask SetCapacityAsync(ushort capacity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ensure(SetCapacityCommand.TryExecute(drive, new SetCapacityCommand(false, capacity), out var result), result, "SET CAPACITY failed.");
        return ValueTask.CompletedTask;
    }

    public async ValueTask ConfigureTwoPartitionAsync(ushort p0Size, ushort p1Size, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modeSense = await ReadPartitionModeSenseAsync(cancellationToken).ConfigureAwait(false);
        var modeData = modeSense.PageData.ToArray();
        Array.Resize(ref modeData, Math.Max(modeData.Length, 12));
        var parameterList = new byte[]
        {
            0, 0, 0x10, 0,
            0x11, 0x0A, modeData[2], 1,
            modeData[4], modeData[5], modeData[6], modeData[7],
            (byte)(p0Size >> 8), (byte)p0Size,
            (byte)(p1Size >> 8), (byte)p1Size,
        };

        Ensure(ModeSelectCommand.TryExecute(
            drive,
            new ModeSelectCommand(false, true, false, parameterList),
            out var selectResult), selectResult, "MODE SELECT partition page failed.");
    }

    public ValueTask<LtfsPartitionModeSense> ReadPartitionModeSenseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ensure(ModeSenseCommand.TryExecute(
            drive,
            new ModeSenseCommand(false, false, ModePageControl.CurrentValues, 0x11, 0, 64),
            out var result,
            out var data), result, "MODE SENSE partition page failed.");

        var modeSense = ModeSenseDataParser.Parse6(data);
        return ValueTask.FromResult(ToLtfsPartitionModeSense(modeSense));
    }

    public static LtfsPartitionModeSense ToLtfsPartitionModeSense(ModeSenseData modeSense)
    {
        var page = modeSense.PageData;
        return new LtfsPartitionModeSense(
            page.Length >= 3 ? page[2] : (byte)0,
            page.Length >= 4 ? page[3] : (byte)0,
            modeSense.CurrentBlockLengthBytes,
            modeSense.Raw,
            page);
    }

    public ValueTask FormatMediumAsync(byte formatCode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ensure(FormatMediumCommand.TryExecute(drive, new FormatMediumCommand(false, (byte)(formatCode & 0x0F), TimeoutSeconds: 3600), out var result), result, "FORMAT MEDIUM failed.");
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

        Ensure(ModeSelectCommand.TryExecute(
            drive,
            new ModeSelectCommand(false, true, false, parameterList),
            out var result), result, "MODE SELECT block size failed.");
        return ValueTask.CompletedTask;
    }

    public ValueTask LocateAsync(LtfsPartition partition, ulong block, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (block > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(block), "10-byte LOCATE supports up to 32-bit block addresses.");

        Ensure(LocateCommand.TryExecute(
            drive,
            new LocateCommand(false, false, true, ToPartitionNumber(partition), (uint)block, LocateDestinationType.LogicalObjectIdentifier, 0),
            out var result), result, "LOCATE failed.");
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

    public ValueTask WriteMamAttributesAsync(LtfsPartition partition, IReadOnlyList<MamAttribute> attributes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parameterList = WriteAttributeCommand.BuildParameterList(attributes);
        Ensure(WriteAttributeCommand.TryExecute(
            drive,
            new WriteAttributeCommand(0, ToPartitionNumber(partition), parameterList),
            out var result), result, "WRITE ATTRIBUTE failed.");
        return ValueTask.CompletedTask;
    }

    public ValueTask SetEncryptionAsync(ReadOnlyMemory<byte>? key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = LtfsEncryptionPayloadBuilder.BuildSetEncryptionPayload(key);
        Ensure(SecurityProtocolOutCommand.TryExecute(drive, new SecurityProtocolOutCommand(0x20, 0x0010, payload), out var result), result, "SECURITY PROTOCOL OUT set encryption failed.");
        return ValueTask.CompletedTask;
    }

    public ValueTask<LogSenseResponse> ReadLogSenseAsync(LogPageCode pageCode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ensure(LogSenseCommand.TryExecute(drive, new LogSenseCommand(pageCode), out var result, out var response), result, "LOG SENSE failed.");
        return ValueTask.FromResult(response);
    }

    private static byte ToPartitionNumber(LtfsPartition partition)
    {
        return partition == LtfsPartition.A ? (byte)0 : (byte)1;
    }

    private static LtfsPartition FromPartitionNumber(byte partition)
    {
        return partition == 0 ? LtfsPartition.A : LtfsPartition.B;
    }

    private static void Ensure(bool transportOk, ScsiCommandResult result, string message)
    {
        if (!transportOk || !result.IsGood)
            throw new LtfsScsiCommandException(message, transportOk, result);
    }
}
