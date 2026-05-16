using Koko.Core.Scsi;
using Koko.Core.Scsi.Commands;

namespace Koko.Core.Ltfs;

public enum LtfsDiscoveryMode
{
    Fast,
    Normal,
    Full
}

public enum LtfsIndexDiscoverySource
{
    Unknown,
    VciIndexPartition,
    VciDataPartition,
    LabelLayout,
    DataCheckpointScan,
    IndexPartitionScan,
    FallbackScan
}

public sealed record LtfsVolumeDiscoveryOptions(
    Guid? ExpectedVolumeUuid = null,
    bool AllowForeignVolume = false);

public sealed record LtfsDiscoveredLabel(
    LtfsPartition Partition,
    ulong Block,
    LtfsLabel Label);

public sealed record LtfsDiscoveredIndex(
    LtfsPartition Partition,
    ulong Block,
    LtfsIndex? Index,
    LtfsIndexValidationResult? Validation,
    string Source,
    string? Error = null)
{
    public bool IsValid => Index is not null && Validation?.IsValid == true;
}

public sealed record LtfsDiscoveryGraph(
    IReadOnlyList<LtfsDiscoveredLabel> Labels,
    IReadOnlyList<LtfsDiscoveredIndex> Indexes,
    IReadOnlyList<LtfsVolumeCoherencyInformation> VciReferences,
    IReadOnlyList<string> Warnings);

public sealed record LtfsVolumeDiscoveryResult(
    LtfsIndex Index,
    LtfsLabel? Label,
    LtfsTapePosition AppendPoint,
    LtfsIndexDiscoverySource Source,
    bool DirtyAppendDetected,
    bool Worm,
    bool WriteProtected,
    IReadOnlyList<string> Warnings,
    LtfsDiscoveryGraph? Graph = null);

public sealed class LtfsVolumeScanner
{
    private readonly ILtfsWriterDevice device;
    private readonly LtfsTapeCommandExecutor executor;
    private readonly LtfsTapeSessionControl? control;

    public LtfsVolumeScanner(ILtfsWriterDevice device, LtfsTapeCommandExecutor? executor = null, LtfsTapeSessionControl? control = null)
    {
        this.device = device ?? throw new ArgumentNullException(nameof(device));
        this.executor = executor ?? new LtfsTapeCommandExecutor();
        this.control = control;
    }

    public async ValueTask<LtfsDiscoveryGraph> ScanAsync(
        LtfsDiscoveryMode mode,
        LtfsWriterOptions writerOptions,
        IReadOnlyList<LtfsVolumeCoherencyInformation> vciReferences,
        CancellationToken cancellationToken = default)
    {
        var labels = new List<LtfsDiscoveredLabel>();
        var indexes = new List<LtfsDiscoveredIndex>();
        var warnings = new List<string>();
        var maxBlocks = mode == LtfsDiscoveryMode.Full ? 4096UL : 64UL;

        foreach (var partition in new[] { LtfsPartition.A, LtfsPartition.B })
        {
            for (ulong block = 0; block < maxBlocks; block++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] data;
                try
                {
                    data = await ReadBlockAtAsync(partition, block, writerOptions.BlockSizeBytes, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    warnings.Add($"Scan skipped {partition}{block}: {ex.Message}");
                    continue;
                }

                if (LooksLikeLtfsLabel(data))
                {
                    try
                    {
                        labels.Add(new LtfsDiscoveredLabel(partition, block, LtfsLabelReader.FromArray(data)));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        warnings.Add($"LTFS label candidate {partition}{block} failed: {ex.Message}");
                    }
                    continue;
                }

                if (!LooksLikeLtfsIndex(data))
                    continue;

                indexes.Add(ReadIndexCandidate(partition, block, data, writerOptions, partition == LtfsPartition.A ? "scan-index-partition" : "scan-data-partition"));
            }
        }

        return new LtfsDiscoveryGraph(labels, indexes, vciReferences, warnings);
    }

    private async ValueTask<byte[]> ReadBlockAtAsync(LtfsPartition partition, ulong block, long blockSizeBytes, CancellationToken cancellationToken)
    {
        byte[]? data = null;
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
            async ct => data = await device.ReadBlockAsync(blockSizeBytes, ct).ConfigureAwait(false),
            LtfsTapeCommandPriority.Data,
            LtfsTapeBarrierKind.None,
            ExpectedStartPosition: target,
            ExpectedEndPosition: target with { Block = target.Block + 1 },
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        await executor.ExecuteAsync(queue, control, cancellationToken).ConfigureAwait(false);
        return data ?? throw new LtfsWriterException($"LTFS scan read at {partition}{block} returned no data.");
    }

    private static LtfsDiscoveredIndex ReadIndexCandidate(LtfsPartition partition, ulong block, byte[] data, LtfsWriterOptions writerOptions, string source)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var index = LtfsSchemaReader.Read(stream);
            var validation = LtfsIndexValidator.ValidateInternal(index, new LtfsIndexValidationOptions(writerOptions.BlockSizeBytes));
            return new LtfsDiscoveredIndex(partition, block, index, validation, source);
        }
        catch (Exception ex)
        {
            return new LtfsDiscoveredIndex(partition, block, null, null, source, ex.Message);
        }
    }

    private static bool LooksLikeLtfsLabel(ReadOnlySpan<byte> data) => IndexOfAscii(data, "<ltfslabel") >= 0;

    private static bool LooksLikeLtfsIndex(ReadOnlySpan<byte> data) => IndexOfAscii(data, "<ltfsindex") >= 0;

    private static int IndexOfAscii(ReadOnlySpan<byte> data, string text)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(text);
        return data.IndexOf(needle);
    }
}

public sealed class LtfsVolumeDiscoveryService
{
    private const int LegacyProbeReadLimitBytes = 0x80000;

    private readonly ILtfsWriterDevice device;

    public LtfsVolumeDiscoveryService(ILtfsWriterDevice device)
    {
        this.device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public async ValueTask<LtfsVolumeDiscoveryResult> DiscoverAsync(
        LtfsVolumeDiscoveryOptions? options = null,
        LtfsWriterOptions? writerOptions = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new LtfsVolumeDiscoveryOptions();
        var warnings = new List<string>();
        var operationId = Guid.NewGuid().ToString("N");
        var executor = new LtfsTapeCommandExecutor();
        var effectiveWriterOptions = LtfsWriterService.ResolvePublicOptions(writerOptions);
        LtfsLabel label;
        bool? detectedWorm;

        using (ScsiStartupUnitAttentionRetry.SuppressPowerOnReset(scopeName: "LTFS discovery startup"))
        {
            await ApplyEncryptionAsync(operationId, effectiveWriterOptions, cancellationToken).ConfigureAwait(false);
            detectedWorm = await DetectWormAsync(cancellationToken).ConfigureAwait(false);
            var vol1 = await ReadBlockAtAsync(executor, LtfsPartition.A, 0, 80, effectiveWriterOptions.TapeControl, cancellationToken).ConfigureAwait(false);
            ValidateVol1(vol1);
            label = await ReadLabelAtFilemarkAsync(executor, LtfsPartition.A, 1, LegacyProbeReadLimitBytes, effectiveWriterOptions.TapeControl, cancellationToken).ConfigureAwait(false);
            effectiveWriterOptions = effectiveWriterOptions with { BlockSizeBytes = label.BlockSize };
            await device.SetBlockSizeAsync(label.BlockSize, cancellationToken).ConfigureAwait(false);
        }

        var vciResult = await TryDiscoverFromVciAsync(options, effectiveWriterOptions, warnings, detectedWorm, executor, label, cancellationToken).ConfigureAwait(false);
        if (vciResult is not null)
            return vciResult;

        return await DiscoverLegacyAsync(options, effectiveWriterOptions, warnings, detectedWorm, executor, label, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<LtfsVolumeDiscoveryResult?> TryDiscoverFromVciAsync(
        LtfsVolumeDiscoveryOptions options,
        LtfsWriterOptions writerOptions,
        List<string> warnings,
        bool? detectedWorm,
        LtfsTapeCommandExecutor executor,
        LtfsLabel label,
        CancellationToken cancellationToken)
    {
        if (device is not ILtfsPartitionMamDevice mamDevice)
            return null;

        var candidates = new List<(LtfsPartition Partition, LtfsVolumeCoherencyInformation Vci)>();
        foreach (var partition in new[] { LtfsPartition.A, LtfsPartition.B })
        {
            try
            {
                var attributes = await mamDevice.ReadMamAttributesAsync(partition, cancellationToken).ConfigureAwait(false);
                foreach (var attribute in attributes.Where(x => x.Id == LtfsVolumeCoherencyInformation.MamAttributeId))
                {
                    if (LtfsVolumeCoherencyInformation.TryParse(attribute.Value.Span, out var vci))
                        candidates.Add((partition, vci));
                    else
                        warnings.Add($"VCI MAM attribute on partition {partition} is not parseable.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"VCI MAM read on partition {partition} failed: {ex.Message}");
            }
        }

        if (candidates.Count == 0)
            return null;

        if (options.ExpectedVolumeUuid is { } expected && !options.AllowForeignVolume && candidates.All(x => x.Vci.VolumeUuid != expected))
            throw new LtfsWriterException("LTFS VCI discovery found a foreign volume.");

        if (candidates.Select(x => x.Vci.Generation).Distinct().Skip(1).Any())
            warnings.Add("VCI MAM attributes report different generations; trying the highest generation first.");

        foreach (var candidate in candidates
                     .Where(x => options.ExpectedVolumeUuid is null || options.AllowForeignVolume || x.Vci.VolumeUuid == options.ExpectedVolumeUuid.Value)
                     .OrderByDescending(x => x.Vci.Generation)
                     .ThenBy(x => x.Partition == LtfsPartition.A ? 0 : 1))
        {
            try
            {
                var payload = await ReadToFilemarkAtAsync(executor, candidate.Partition, candidate.Vci.IndexBlock, writerOptions, cancellationToken).ConfigureAwait(false);
                var index = ReadAndValidateIndex(payload, writerOptions);
                if (index.VolumeUuid != candidate.Vci.VolumeUuid)
                    throw new LtfsWriterException("VCI points to an index with a different volume UUID.");
                if (index.GenerationNumber != candidate.Vci.Generation)
                    throw new LtfsWriterException("VCI points to an index with a different generation.");
                if (!MatchesExpected(index, options))
                    throw new LtfsWriterException("LTFS VCI discovery found a foreign volume.");

                var dataPartition = InferDataPartition(index, candidate.Partition);
                var append = await LocateEndOfDataAsync(executor, dataPartition, writerOptions, cancellationToken).ConfigureAwait(false);
                var stableDataBlock = index.Location.Partition == dataPartition ? index.Location.StartBlock : index.PreviousGenerationLocation.StartBlock;
                var dirty = append.Block > stableDataBlock;
                if (dirty)
                    warnings.Add("Data partition EOD is after the VCI-discovered checkpoint; unindexed data may exist.");

                var source = candidate.Partition == LtfsPartition.A
                    ? LtfsIndexDiscoverySource.VciIndexPartition
                    : LtfsIndexDiscoverySource.VciDataPartition;
                return new LtfsVolumeDiscoveryResult(index, label, append, source, dirty, IsWorm(index, detectedWorm), false, warnings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"VCI candidate {candidate.Partition}{candidate.Vci.IndexBlock} generation {candidate.Vci.Generation} failed: {ex.Message}");
            }
        }

        return null;
    }

    private async ValueTask<LtfsVolumeDiscoveryResult> DiscoverLegacyAsync(
        LtfsVolumeDiscoveryOptions options,
        LtfsWriterOptions writerOptions,
        List<string> warnings,
        bool? detectedWorm,
        LtfsTapeCommandExecutor executor,
        LtfsLabel label,
        CancellationToken cancellationToken)
    {
        var indexPartition = LtfsPartition.A;
        var dataPartition = LtfsPartition.B;
        if (label.LocationPartition == label.DataPartition && label.IndexPartition != label.DataPartition)
        {
            dataPartition = LtfsPartition.A;
            indexPartition = LtfsPartition.B;
            label = await ReadLabelAtFilemarkAsync(executor, indexPartition, 1, writerOptions.BlockSizeBytes, writerOptions.TapeControl, cancellationToken).ConfigureAwait(false);
        }
        else if (label.IndexPartition == label.DataPartition)
        {
            indexPartition = LtfsPartition.A;
            dataPartition = LtfsPartition.A;
        }

        if (indexPartition == dataPartition)
            return await DiscoverLegacySinglePartitionAsync(options, writerOptions, warnings, detectedWorm, executor, label, dataPartition, cancellationToken).ConfigureAwait(false);

        var index = await ReadPayloadAfterFilemarkAsync(
            executor,
            indexPartition,
            3,
            writerOptions.BlockSizeBytes,
            writerOptions.TapeControl,
            payload => ReadAndValidateIndex(payload, writerOptions),
            "LTFS index",
            cancellationToken).ConfigureAwait(false);
        if (!MatchesExpected(index, options))
            throw new LtfsWriterException("LTFS legacy discovery found a foreign volume.");

        var append = await LocateEndOfDataAsync(executor, dataPartition, writerOptions, cancellationToken).ConfigureAwait(false);
        var stableDataBlock = index.Location.Partition == dataPartition ? index.Location.StartBlock : index.PreviousGenerationLocation.StartBlock;
        var dirty = append.Block > stableDataBlock;
        if (dirty)
            warnings.Add("Data partition EOD is after the legacy-discovered checkpoint; unindexed data may exist.");

        return new LtfsVolumeDiscoveryResult(index, label, append, LtfsIndexDiscoverySource.LabelLayout, dirty, IsWorm(index, detectedWorm), false, warnings);
    }

    private async ValueTask<LtfsVolumeDiscoveryResult> DiscoverLegacySinglePartitionAsync(
        LtfsVolumeDiscoveryOptions options,
        LtfsWriterOptions writerOptions,
        List<string> warnings,
        bool? detectedWorm,
        LtfsTapeCommandExecutor executor,
        LtfsLabel label,
        LtfsPartition dataPartition,
        CancellationToken cancellationToken)
    {
        var eod = await LocateEndOfDataAsync(executor, dataPartition, writerOptions, cancellationToken).ConfigureAwait(false);
        if (eod.FileNumber is not { } fileNumber || fileNumber <= 1)
            throw new LtfsWriterException("LTFS legacy discovery could not locate the previous single-partition index filemark.");

        var index = await ReadPayloadAfterFilemarkAsync(
            executor,
            dataPartition,
            fileNumber - 1,
            writerOptions.BlockSizeBytes,
            writerOptions.TapeControl,
            payload => ReadAndValidateIndex(payload, writerOptions),
            "LTFS index",
            cancellationToken).ConfigureAwait(false);
        if (!MatchesExpected(index, options))
            throw new LtfsWriterException("LTFS legacy discovery found a foreign volume.");

        var dirty = eod.Block > index.Location.StartBlock;
        if (dirty)
            warnings.Add("Data partition EOD is after the legacy EOD checkpoint; unindexed data may exist.");

        return new LtfsVolumeDiscoveryResult(index, label, eod, LtfsIndexDiscoverySource.DataCheckpointScan, dirty, IsWorm(index, detectedWorm), false, warnings);
    }

    private async ValueTask<LtfsWriterOptions> ResolveDiscoveryWriterOptionsAsync(
        LtfsWriterOptions? writerOptions,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (writerOptions is not null)
            return LtfsWriterService.ResolvePublicOptions(writerOptions);

        if (device is ILtfsModeSenseDevice modeSenseDevice)
        {
            try
            {
                var modeSense = await modeSenseDevice.ReadPartitionModeSenseAsync(cancellationToken).ConfigureAwait(false);
                if (modeSense.CurrentBlockLengthBytes is > 0 and <= int.MaxValue)
                {
                    warnings.Add($"MODE SENSE 0x11 current block length is {modeSense.CurrentBlockLengthBytes.Value} bytes; max extra partitions={modeSense.MaxExtraPartitionCount}, defined={modeSense.AdditionalPartitionsDefined}.");
                    return LtfsWriterService.ResolvePublicOptions(new LtfsWriterOptions(BlockSizeBytes: modeSense.CurrentBlockLengthBytes.Value));
                }

                warnings.Add($"MODE SENSE 0x11 reports variable block length; using default discovery read limit {LtfsSequentialReadPlanOptions.Default.LtfsBlockSizeBytes} bytes.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"MODE SENSE 0x11 block length probe failed: {ex.Message}");
            }
        }

        return LtfsWriterService.ResolvePublicOptions(null);
    }

    private async ValueTask<LtfsVolumeDiscoveryResult?> TryLegacyLayoutDiscoveryAsync(
        LtfsVolumeDiscoveryOptions options,
        LtfsWriterOptions writerOptions,
        List<string> warnings,
        bool? detectedWorm,
        LtfsTapeCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        foreach (var partition in new[] { LtfsPartition.A, LtfsPartition.B })
        {
            try
            {
                var position = await LocateFilemarkAsync(executor, partition, partition == LtfsPartition.A ? 3UL : 1UL, writerOptions.TapeControl, cancellationToken).ConfigureAwait(false);
                var payload = await ReadToFilemarkAtCurrentPositionAsync(executor, position, writerOptions.BlockSizeBytes, writerOptions.TapeControl, cancellationToken).ConfigureAwait(false);
                var index = ReadAndValidateIndex(payload, writerOptions);
                if (!MatchesExpected(index, options))
                    continue;

                var append = await LocateEndOfDataAsync(executor, LtfsPartition.B, writerOptions, cancellationToken).ConfigureAwait(false);
                var dataBlock = index.Location.Partition == LtfsPartition.B ? index.Location.StartBlock : index.PreviousGenerationLocation.StartBlock;
                var dirty = append.Block > dataBlock;
                if (dirty)
                    warnings.Add("Data partition EOD is after the legacy-discovered checkpoint; unindexed data may exist.");

                var label = await TryReadLegacyLabelAsync(index.VolumeUuid, writerOptions, executor, cancellationToken).ConfigureAwait(false);
                var source = partition == LtfsPartition.A ? LtfsIndexDiscoverySource.LabelLayout : LtfsIndexDiscoverySource.DataCheckpointScan;
                return new LtfsVolumeDiscoveryResult(index, label, append, source, dirty, IsWorm(index, detectedWorm), false, warnings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Legacy LTFS layout candidate on partition {partition} failed: {ex.Message}");
            }
        }

        var dataEodCandidate = await TryLegacyDataEodIndexAsync(options, writerOptions, warnings, detectedWorm, executor, cancellationToken).ConfigureAwait(false);
        return dataEodCandidate;
    }

    private async ValueTask<LtfsVolumeDiscoveryResult?> TryLegacyDataEodIndexAsync(
        LtfsVolumeDiscoveryOptions options,
        LtfsWriterOptions writerOptions,
        List<string> warnings,
        bool? detectedWorm,
        LtfsTapeCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        try
        {
            var eod = await LocateEndOfDataAsync(executor, LtfsPartition.B, writerOptions, cancellationToken).ConfigureAwait(false);
            if (eod.FileNumber is not { } fileNumber || fileNumber <= 1)
                return null;

            var position = await LocateFilemarkAsync(executor, LtfsPartition.B, fileNumber - 1, writerOptions.TapeControl, cancellationToken).ConfigureAwait(false);
            var payload = await ReadToFilemarkAtCurrentPositionAsync(executor, position, writerOptions.BlockSizeBytes, writerOptions.TapeControl, cancellationToken).ConfigureAwait(false);
            var index = ReadAndValidateIndex(payload, writerOptions);
            if (!MatchesExpected(index, options))
                return null;

            var label = await TryReadLegacyLabelAsync(index.VolumeUuid, writerOptions, executor, cancellationToken).ConfigureAwait(false);
            var dirty = eod.Block > index.Location.StartBlock;
            if (dirty)
                warnings.Add("Data partition EOD is after the legacy EOD checkpoint; unindexed data may exist.");

            return new LtfsVolumeDiscoveryResult(index, label, eod, LtfsIndexDiscoverySource.DataCheckpointScan, dirty, IsWorm(index, detectedWorm), false, warnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"Legacy data-partition EOD scan failed: {ex.Message}");
            return null;
        }
    }

    private async ValueTask<LtfsLabel?> TryReadLegacyLabelAsync(Guid volumeUuid, LtfsWriterOptions writerOptions, LtfsTapeCommandExecutor executor, CancellationToken cancellationToken)
    {
        foreach (var partition in new[] { LtfsPartition.A, LtfsPartition.B })
        {
            try
            {
                var data = await ReadBlockAtAsync(executor, partition, 2, writerOptions, cancellationToken).ConfigureAwait(false);
                if (data.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes("<ltfslabel")) < 0)
                    continue;

                var label = LtfsLabelReader.FromArray(data);
                if (label.VolumeUuid == Guid.Empty || label.VolumeUuid == volumeUuid)
                    return label;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex;
            }
        }

        return null;
    }

    private async ValueTask<LtfsLabel> ReadLabelAtFilemarkAsync(
        LtfsTapeCommandExecutor executor,
        LtfsPartition partition,
        ulong filemark,
        long readLimitBytes,
        LtfsTapeSessionControl? tapeControl,
        CancellationToken cancellationToken)
    {
        return await ReadPayloadAfterFilemarkAsync(
            executor,
            partition,
            filemark,
            readLimitBytes,
            tapeControl,
            payload => LtfsLabelReader.FromArray(payload),
            "LTFS label",
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T> ReadPayloadAfterFilemarkAsync<T>(
        LtfsTapeCommandExecutor executor,
        LtfsPartition partition,
        ulong filemark,
        long readLimitBytes,
        LtfsTapeSessionControl? tapeControl,
        Func<byte[], T> readPayload,
        string payloadKind,
        CancellationToken cancellationToken)
    {
        Exception? directFailure = null;
        try
        {
            var directStart = await LocateFilemarkAsync(executor, partition, filemark, tapeControl, cancellationToken).ConfigureAwait(false);
            var payload = await ReadToFilemarkAtCurrentPositionAsync(executor, directStart, readLimitBytes, tapeControl, cancellationToken).ConfigureAwait(false);
            return readPayload(payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            directFailure = ex;
        }

        try
        {
            _ = await LocateFilemarkAsync(executor, partition, filemark, tapeControl, cancellationToken).ConfigureAwait(false);
            var payloadStart = await AdvancePastFilemarkAsync(executor, tapeControl, cancellationToken).ConfigureAwait(false);
            var payload = await ReadToFilemarkAtCurrentPositionAsync(executor, payloadStart, readLimitBytes, tapeControl, cancellationToken).ConfigureAwait(false);
            return readPayload(payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new LtfsWriterException(
                $"LTFS discovery could not read {payloadKind} after filemark {partition}{filemark}. Direct read failed: {directFailure?.Message}; filemark-advance read failed: {ex.Message}",
                ex);
        }
    }

    private async ValueTask<LtfsTapePosition> AdvancePastFilemarkAsync(
        LtfsTapeCommandExecutor executor,
        LtfsTapeSessionControl? tapeControl,
        CancellationToken cancellationToken)
    {
        LtfsTapePosition? position = null;
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.ReadDataBlock,
            ct => device.AdvancePastFilemarkAsync(ct),
            LtfsTapeCommandPriority.Data,
            LtfsTapeBarrierKind.HardBarrier,
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.ReadPosition,
            async ct => position = await device.ReadPositionAsync(ct).ConfigureAwait(false),
            LtfsTapeCommandPriority.Control,
            LtfsTapeBarrierKind.HardBarrier,
            AffectsPosition: false,
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        await executor.ExecuteAsync(queue, tapeControl, cancellationToken).ConfigureAwait(false);
        if (position is null)
            throw new LtfsWriterException("LTFS discovery filemark advance did not return a position.");
        executor.SetExpectedPosition(position);
        return position;
    }

    private static void ValidateVol1(ReadOnlySpan<byte> data)
    {
        if (data.Length != 80)
            throw new LtfsWriterException("LTFS legacy discovery did not find a valid 80-byte VOL1 label.");

        var text = System.Text.Encoding.ASCII.GetString(data);
        if (!text.StartsWith("VOL1", StringComparison.Ordinal) || text.Length < 28 || !string.Equals(text.Substring(24, 4), "LTFS", StringComparison.Ordinal))
            throw new LtfsWriterException("LTFS legacy discovery did not find a valid LTFS VOL1 label.");
    }

    private async ValueTask ApplyEncryptionAsync(string operationId, LtfsWriterOptions writerOptions, CancellationToken cancellationToken)
    {
        var encryption = writerOptions.Encryption ?? new LtfsEncryptionOptions();
        if (encryption.Mode == LtfsEncryptionMode.Disabled)
            return;

        if (device is not ILtfsEncryptionCapableDevice encryptionDevice)
            throw new LtfsWriterException("LTFS discovery encryption was requested but the device does not support encryption.");
        if (encryption.KeyProvider is null)
            throw new LtfsWriterException("LTFS discovery encryption key provider is required when encryption is enabled.");

        var material = await encryption.KeyProvider.ResolveKeyAsync(
            new LtfsEncryptionKeyRequest(operationId, encryption.Mode, encryption.KeyId),
            cancellationToken).ConfigureAwait(false);
        if (material is null || material.Key.Length != 32 || material.Key.Span.ToArray().All(x => x == 0))
            throw new LtfsWriterException("LTFS discovery encryption key material is invalid.");

        await encryptionDevice.SetEncryptionAsync(material.Key, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<byte[]> ReadToFilemarkAtAsync(
        LtfsTapeCommandExecutor executor,
        LtfsPartition partition,
        ulong block,
        LtfsWriterOptions writerOptions,
        CancellationToken cancellationToken)
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
        await executor.ExecuteAsync(queue, writerOptions.TapeControl, cancellationToken).ConfigureAwait(false);
        return await ReadToFilemarkAtCurrentPositionAsync(executor, target, writerOptions.BlockSizeBytes, writerOptions.TapeControl, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<byte[]> ReadToFilemarkAtCurrentPositionAsync(
        LtfsTapeCommandExecutor executor,
        LtfsTapePosition position,
        long readLimitBytes,
        LtfsTapeSessionControl? tapeControl,
        CancellationToken cancellationToken)
    {
        byte[]? payload = null;
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.ReadDataBlock,
            async ct => payload = await device.ReadToFilemarkAsync(readLimitBytes, ct).ConfigureAwait(false),
            LtfsTapeCommandPriority.Data,
            LtfsTapeBarrierKind.HardBarrier,
            ExpectedStartPosition: position,
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        if (!executor.PositionKnown || executor.ExpectedPosition is null || executor.ExpectedPosition.Partition != position.Partition || executor.ExpectedPosition.Block != position.Block)
            executor.SetExpectedPosition(position);
        await executor.ExecuteAsync(queue, tapeControl, cancellationToken).ConfigureAwait(false);
        return payload ?? throw new LtfsWriterException($"LTFS discovery read-to-filemark at {position.Partition}{position.Block} returned no payload.");
    }

    private async ValueTask<byte[]> ReadBlockAtAsync(
        LtfsTapeCommandExecutor executor,
        LtfsPartition partition,
        ulong block,
        LtfsWriterOptions writerOptions,
        CancellationToken cancellationToken)
    {
        return await ReadBlockAtAsync(executor, partition, block, writerOptions.BlockSizeBytes, writerOptions.TapeControl, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<byte[]> ReadBlockAtAsync(
        LtfsTapeCommandExecutor executor,
        LtfsPartition partition,
        ulong block,
        long readLimitBytes,
        LtfsTapeSessionControl? tapeControl,
        CancellationToken cancellationToken)
    {
        byte[]? data = null;
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
            async ct => data = await device.ReadBlockAsync(readLimitBytes, ct).ConfigureAwait(false),
            LtfsTapeCommandPriority.Data,
            LtfsTapeBarrierKind.None,
            ExpectedStartPosition: target,
            ExpectedEndPosition: target with { Block = target.Block + 1 },
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        await executor.ExecuteAsync(queue, tapeControl, cancellationToken).ConfigureAwait(false);
        return data ?? throw new LtfsWriterException($"LTFS discovery read at {partition}{block} returned no data.");
    }

    private async ValueTask<LtfsTapePosition> LocateEndOfDataAsync(
        LtfsTapeCommandExecutor executor,
        LtfsPartition partition,
        LtfsWriterOptions writerOptions,
        CancellationToken cancellationToken)
    {
        LtfsTapePosition? position = null;
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.LocateEod,
            ct => device.LocateEndOfDataAsync(partition, ct),
            LtfsTapeCommandPriority.Control,
            LtfsTapeBarrierKind.HardBarrier,
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.ReadPosition,
            async ct => position = await device.ReadPositionAsync(ct).ConfigureAwait(false),
            LtfsTapeCommandPriority.Control,
            LtfsTapeBarrierKind.HardBarrier,
            AffectsPosition: false,
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        await executor.ExecuteAsync(queue, writerOptions.TapeControl, cancellationToken).ConfigureAwait(false);
        if (position is null)
            throw new LtfsWriterException("LTFS discovery locate EOD did not return a position.");
        executor.SetExpectedPosition(position);
        return position;
    }

    private async ValueTask<LtfsTapePosition> LocateFilemarkAsync(
        LtfsTapeCommandExecutor executor,
        LtfsPartition partition,
        ulong filemark,
        LtfsTapeSessionControl? tapeControl,
        CancellationToken cancellationToken)
    {
        LtfsTapePosition? position = null;
        var queue = new LtfsTapeCommandQueue();
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.LocateFilemark,
            ct => device.LocateFilemarkAsync(partition, filemark, ct),
            LtfsTapeCommandPriority.Control,
            LtfsTapeBarrierKind.HardBarrier,
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        queue.Enqueue(new LtfsTapeCommand(
            LtfsTapeCommandKind.ReadPosition,
            async ct => position = await device.ReadPositionAsync(ct).ConfigureAwait(false),
            LtfsTapeCommandPriority.Control,
            LtfsTapeBarrierKind.HardBarrier,
            AffectsPosition: false,
            ReadPositionAsync: ct => device.ReadPositionAsync(ct)));
        await executor.ExecuteAsync(queue, tapeControl, cancellationToken).ConfigureAwait(false);
        if (position is null)
            throw new LtfsWriterException($"LTFS discovery locate filemark {partition}{filemark} did not return a position.");
        executor.SetExpectedPosition(position);
        return position;
    }

    private async ValueTask<bool?> DetectWormAsync(CancellationToken cancellationToken)
    {
        try
        {
            return LtfsWormDetector.TryDetectFromVolumeStatistics(await device.ReadLogSenseAsync(LogPageCode.VolumeStatistics, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static LtfsIndex ReadAndValidateIndex(byte[] payload, LtfsWriterOptions writerOptions)
    {
        using var stream = new MemoryStream(payload, writable: false);
        var index = LtfsSchemaReader.Read(stream);
        var validation = LtfsIndexValidator.ValidateInternal(index, new LtfsIndexValidationOptions(writerOptions.BlockSizeBytes));
        if (!validation.IsValid)
            throw new LtfsWriterException($"LTFS index validation failed: {string.Join("; ", validation.Errors)}");
        return index;
    }

    private static bool MatchesExpected(LtfsIndex index, LtfsVolumeDiscoveryOptions options)
    {
        return options.ExpectedVolumeUuid is null || options.AllowForeignVolume || index.VolumeUuid == options.ExpectedVolumeUuid.Value;
    }

    private static bool IsWorm(LtfsIndex index, bool? detectedWorm)
    {
        return detectedWorm == true || index.VolumeLockState == LtfsVolumeLockState.PermLocked;
    }

    private static LtfsPartition InferDataPartition(LtfsIndex index, LtfsPartition vciPartition)
    {
        if (index.Location.Partition == LtfsPartition.B || index.PreviousGenerationLocation.Partition == LtfsPartition.B)
            return LtfsPartition.B;

        return vciPartition == LtfsPartition.B ? LtfsPartition.B : LtfsPartition.A;
    }

}
