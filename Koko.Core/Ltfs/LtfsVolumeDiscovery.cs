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

public enum LtfsDiscoveryConflictPolicy
{
    FailOnHigherInvalidGeneration,
    UseHighestValidWithWarning
}

public sealed record LtfsVolumeDiscoveryOptions(
    LtfsDiscoveryMode Mode = LtfsDiscoveryMode.Normal,
    Guid? ExpectedVolumeUuid = null,
    bool AllowForeignVolume = false,
    LtfsDiscoveryConflictPolicy ConflictPolicy = LtfsDiscoveryConflictPolicy.FailOnHigherInvalidGeneration,
    ulong NormalScanMaxBlocks = 64,
    ulong FullScanMaxBlocks = 4096);

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

    public LtfsVolumeScanner(ILtfsWriterDevice device)
    {
        this.device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public async ValueTask<LtfsDiscoveryGraph> ScanAsync(
        LtfsDiscoveryMode mode,
        LtfsWriterOptions writerOptions,
        LtfsVolumeDiscoveryOptions discoveryOptions,
        IReadOnlyList<LtfsVolumeCoherencyInformation> vciReferences,
        CancellationToken cancellationToken = default)
    {
        var labels = new List<LtfsDiscoveredLabel>();
        var indexes = new List<LtfsDiscoveredIndex>();
        var warnings = new List<string>();
        var maxBlocks = mode == LtfsDiscoveryMode.Full ? discoveryOptions.FullScanMaxBlocks : discoveryOptions.NormalScanMaxBlocks;

        foreach (var partition in new[] { LtfsPartition.A, LtfsPartition.B })
        {
            for (ulong block = 0; block < maxBlocks; block++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] data;
                try
                {
                    await device.LocateAsync(partition, block, cancellationToken).ConfigureAwait(false);
                    data = await device.ReadBlockAsync(writerOptions.BlockSizeBytes, cancellationToken).ConfigureAwait(false);
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
        writerOptions = LtfsWriterService.ResolvePublicOptions(writerOptions);
        var warnings = new List<string>();
        var candidates = new List<(LtfsVolumeCoherencyInformation Vci, LtfsPartition Partition)>();
        var vciReferences = new List<LtfsVolumeCoherencyInformation>();
        var operationId = Guid.NewGuid().ToString("N");

        await ApplyEncryptionAsync(operationId, writerOptions, cancellationToken).ConfigureAwait(false);
        var detectedWorm = await DetectWormAsync(cancellationToken).ConfigureAwait(false);

        if (device is ILtfsMetadataExportDevice metadataDevice)
        {
            var attributes = await metadataDevice.ReadMamAttributesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var attribute in attributes.Where(x => x.Id == LtfsVolumeCoherencyInformation.MamAttributeId))
            {
                if (LtfsVolumeCoherencyInformation.TryParse(attribute.Value.Span, out var vci))
                {
                    vciReferences.Add(vci);
                    candidates.Add((vci, LtfsPartition.B));
                }
            }
        }

        if (candidates.Count == 0 && options.Mode == LtfsDiscoveryMode.Fast)
            throw new LtfsWriterException("LTFS discovery could not find a valid VCI/MAM index pointer.");

        var ordered = candidates
            .Where(x => options.ExpectedVolumeUuid is null || options.AllowForeignVolume || x.Vci.VolumeUuid == options.ExpectedVolumeUuid.Value)
            .OrderByDescending(x => x.Vci.Generation)
            .ThenBy(x => x.Partition == LtfsPartition.A ? 0 : 1)
            .ToArray();
        if (ordered.Length == 0 && options.Mode == LtfsDiscoveryMode.Fast)
            throw new LtfsWriterException("LTFS discovery found VCI candidates but none matched the expected volume UUID.");

        Exception? lastError = null;
        foreach (var candidate in ordered)
        {
            var partition = candidate.Partition;
            var source = partition == LtfsPartition.A ? LtfsIndexDiscoverySource.VciIndexPartition : LtfsIndexDiscoverySource.VciDataPartition;
            try
            {
                await device.LocateAsync(partition, candidate.Vci.IndexBlock, cancellationToken).ConfigureAwait(false);
                var payload = await device.ReadToFilemarkAsync(writerOptions.BlockSizeBytes, cancellationToken).ConfigureAwait(false);
                using var stream = new MemoryStream(payload, writable: false);
                var index = LtfsSchemaReader.Read(stream);
                var validation = LtfsIndexValidator.ValidateInternal(index, new LtfsIndexValidationOptions(writerOptions.BlockSizeBytes));
                if (!validation.IsValid)
                {
                    warnings.Add($"VCI candidate {partition}{candidate.Vci.IndexBlock} is invalid: {string.Join("; ", validation.Errors)}");
                    continue;
                }

                await device.LocateEndOfDataAsync(LtfsPartition.B, cancellationToken).ConfigureAwait(false);
                var append = await device.ReadPositionAsync(cancellationToken).ConfigureAwait(false);
                var expectedDataIndex = index.Location.Partition == LtfsPartition.B ? index.Location.StartBlock : index.PreviousGenerationLocation.StartBlock;
                var dirty = append.Block > expectedDataIndex;
                if (dirty)
                    warnings.Add("Data partition EOD is after the latest known data checkpoint; unindexed data may exist.");

                return new LtfsVolumeDiscoveryResult(index, null, append, source, dirty, IsWorm(index, detectedWorm), false, warnings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                warnings.Add($"VCI candidate {partition}{candidate.Vci.IndexBlock} failed: {ex.Message}");
            }
        }

        if (options.Mode == LtfsDiscoveryMode.Fast)
            throw new LtfsWriterException("LTFS discovery failed to load a valid index from VCI candidates.", lastError ?? new InvalidOperationException("No valid candidates."));

        var legacy = await TryLegacyLayoutDiscoveryAsync(options, writerOptions, warnings, detectedWorm, cancellationToken).ConfigureAwait(false);
        if (legacy is not null)
            return legacy;

        var graph = await new LtfsVolumeScanner(device).ScanAsync(options.Mode, writerOptions, options, vciReferences, cancellationToken).ConfigureAwait(false);
        warnings.AddRange(graph.Warnings);
        var selected = SelectBestCandidate(graph.Indexes, options, warnings);
        if (selected is null || selected.Index is null)
            throw new LtfsWriterException("LTFS fallback discovery did not find a valid index.", lastError ?? new InvalidOperationException("No valid fallback candidates."));

        var label = graph.Labels
            .Where(x => x.Label.VolumeUuid == Guid.Empty || x.Label.VolumeUuid == selected.Index.VolumeUuid)
            .OrderBy(x => x.Partition == LtfsPartition.A ? 0 : 1)
            .FirstOrDefault()?.Label;

        await device.LocateEndOfDataAsync(LtfsPartition.B, cancellationToken).ConfigureAwait(false);
        var fallbackAppend = await device.ReadPositionAsync(cancellationToken).ConfigureAwait(false);
        var stableDataBlock = selected.Index.Location.Partition == LtfsPartition.B ? selected.Index.Location.StartBlock : selected.Index.PreviousGenerationLocation.StartBlock;
        var fallbackDirty = fallbackAppend.Block > stableDataBlock;
        if (fallbackDirty)
            warnings.Add("Data partition EOD is after the fallback-discovered checkpoint; unindexed data may exist.");

        var fallbackSource = selected.Partition == LtfsPartition.A ? LtfsIndexDiscoverySource.IndexPartitionScan : LtfsIndexDiscoverySource.DataCheckpointScan;
        return new LtfsVolumeDiscoveryResult(selected.Index, label, fallbackAppend, fallbackSource, fallbackDirty, IsWorm(selected.Index, detectedWorm), false, warnings, graph);
    }

    private async ValueTask<LtfsVolumeDiscoveryResult?> TryLegacyLayoutDiscoveryAsync(
        LtfsVolumeDiscoveryOptions options,
        LtfsWriterOptions writerOptions,
        List<string> warnings,
        bool? detectedWorm,
        CancellationToken cancellationToken)
    {
        foreach (var partition in new[] { LtfsPartition.A, LtfsPartition.B })
        {
            try
            {
                await device.LocateFilemarkAsync(partition, partition == LtfsPartition.A ? 3UL : 1UL, cancellationToken).ConfigureAwait(false);
                var position = await device.ReadPositionAsync(cancellationToken).ConfigureAwait(false);
                var payload = await device.ReadToFilemarkAsync(writerOptions.BlockSizeBytes, cancellationToken).ConfigureAwait(false);
                var index = ReadAndValidateIndex(payload, writerOptions);
                if (!MatchesExpected(index, options))
                    continue;

                await device.LocateEndOfDataAsync(LtfsPartition.B, cancellationToken).ConfigureAwait(false);
                var append = await device.ReadPositionAsync(cancellationToken).ConfigureAwait(false);
                var dataBlock = index.Location.Partition == LtfsPartition.B ? index.Location.StartBlock : index.PreviousGenerationLocation.StartBlock;
                var dirty = append.Block > dataBlock;
                if (dirty)
                    warnings.Add("Data partition EOD is after the legacy-discovered checkpoint; unindexed data may exist.");

                var label = await TryReadLegacyLabelAsync(index.VolumeUuid, writerOptions.BlockSizeBytes, cancellationToken).ConfigureAwait(false);
                var source = partition == LtfsPartition.A ? LtfsIndexDiscoverySource.LabelLayout : LtfsIndexDiscoverySource.DataCheckpointScan;
                return new LtfsVolumeDiscoveryResult(index, label, append, source, dirty, IsWorm(index, detectedWorm), false, warnings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Legacy LTFS layout candidate on partition {partition} failed: {ex.Message}");
            }
        }

        var dataEodCandidate = await TryLegacyDataEodIndexAsync(options, writerOptions, warnings, detectedWorm, cancellationToken).ConfigureAwait(false);
        return dataEodCandidate;
    }

    private async ValueTask<LtfsVolumeDiscoveryResult?> TryLegacyDataEodIndexAsync(
        LtfsVolumeDiscoveryOptions options,
        LtfsWriterOptions writerOptions,
        List<string> warnings,
        bool? detectedWorm,
        CancellationToken cancellationToken)
    {
        try
        {
            await device.LocateEndOfDataAsync(LtfsPartition.B, cancellationToken).ConfigureAwait(false);
            var eod = await device.ReadPositionAsync(cancellationToken).ConfigureAwait(false);
            if (eod.FileNumber is not { } fileNumber || fileNumber <= 1)
                return null;

            await device.LocateFilemarkAsync(LtfsPartition.B, fileNumber - 1, cancellationToken).ConfigureAwait(false);
            var position = await device.ReadPositionAsync(cancellationToken).ConfigureAwait(false);
            var payload = await device.ReadToFilemarkAsync(writerOptions.BlockSizeBytes, cancellationToken).ConfigureAwait(false);
            var index = ReadAndValidateIndex(payload, writerOptions);
            if (!MatchesExpected(index, options))
                return null;

            var label = await TryReadLegacyLabelAsync(index.VolumeUuid, writerOptions.BlockSizeBytes, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<LtfsLabel?> TryReadLegacyLabelAsync(Guid volumeUuid, long blockSizeBytes, CancellationToken cancellationToken)
    {
        foreach (var partition in new[] { LtfsPartition.A, LtfsPartition.B })
        {
            try
            {
                await device.LocateAsync(partition, 2, cancellationToken).ConfigureAwait(false);
                var data = await device.ReadBlockAsync(blockSizeBytes, cancellationToken).ConfigureAwait(false);
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

    private static LtfsDiscoveredIndex? SelectBestCandidate(
        IReadOnlyList<LtfsDiscoveredIndex> indexes,
        LtfsVolumeDiscoveryOptions options,
        List<string> warnings)
    {
        var valid = indexes
            .Where(x => x.IsValid && x.Index is not null)
            .Where(x => options.ExpectedVolumeUuid is null || options.AllowForeignVolume || x.Index!.VolumeUuid == options.ExpectedVolumeUuid.Value)
            .ToArray();
        if (valid.Length == 0)
            return null;

        ulong highestSeen = 0;
        foreach (var index in indexes)
        {
            if (index.Index is not null && index.Index.GenerationNumber > highestSeen)
                highestSeen = index.Index.GenerationNumber;
        }
        var highestValid = valid.Select(x => x.Index!.GenerationNumber).Max();
        if (highestSeen > highestValid && options.ConflictPolicy == LtfsDiscoveryConflictPolicy.FailOnHigherInvalidGeneration)
            throw new LtfsWriterException($"LTFS discovery found invalid generation {highestSeen} newer than valid generation {highestValid}.");
        if (highestSeen > highestValid)
            warnings.Add($"Using valid generation {highestValid}; newer generation {highestSeen} was invalid.");

        return valid
            .OrderByDescending(x => x.Index!.GenerationNumber)
            .ThenBy(x => x.Partition == LtfsPartition.A ? 0 : 1)
            .ThenBy(x => x.Index!.Location.Partition == x.Partition && x.Index.Location.StartBlock == x.Block ? 0 : 1)
            .First();
    }
}
