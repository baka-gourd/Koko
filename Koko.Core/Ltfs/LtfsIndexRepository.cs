namespace Koko.Core.Ltfs;

public enum LtfsCandidateCommitMode
{
    Replace,
    AppendBaseline,
    Rollback
}

public sealed record LtfsCheckpointPolicy(
    long? MaxUnindexedBytes = null,
    TimeSpan? MaxUnindexedAge = null,
    long? MaxUnindexedFiles = null);

public sealed record LtfsIndexCounters(
    long UnindexedBytes,
    long UnindexedFiles,
    DateTimeOffset LastCheckpointTime);

public sealed record LtfsCandidateIndex(
    LtfsIndex Index,
    LtfsIndexValidationResult Validation,
    string SourceDescription)
{
    public bool CanCommit => Validation.IsValid;
}

public sealed class LtfsIndexRepository
{
    public LtfsIndexRepository(LtfsIndex current)
    {
        ArgumentNullException.ThrowIfNull(current);
        Current = current.Clone();
        StableCheckpoint = current.Clone();
    }

    public LtfsIndex Current { get; private set; }
    public LtfsIndex StableCheckpoint { get; private set; }

    public LtfsCandidateIndex StageExternalIndex(
        Stream schemaStream,
        string sourceDescription,
        LtfsSchemaReaderOptions? readerOptions = null,
        LtfsIndexValidationOptions? validationOptions = null)
    {
        var candidate = LtfsSchemaReader.Read(schemaStream, readerOptions);
        var validation = LtfsIndexValidator.ValidateInternal(candidate, validationOptions);
        return new LtfsCandidateIndex(candidate, validation, sourceDescription);
    }

    public LtfsCandidateIndex StageExternalIndexFile(
        string path,
        LtfsSchemaReaderOptions? readerOptions = null,
        LtfsIndexValidationOptions? validationOptions = null)
    {
        using var stream = File.OpenRead(path);
        return StageExternalIndex(stream, path, readerOptions, validationOptions);
    }

    public void CommitCandidate(LtfsCandidateIndex candidate, LtfsCandidateCommitMode mode, bool allowForeignVolume = false)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!candidate.Validation.IsValid)
            throw new InvalidOperationException("Cannot commit an invalid LTFS index candidate.");

        if (!allowForeignVolume && Current.VolumeUuid != Guid.Empty && candidate.Index.VolumeUuid != Current.VolumeUuid)
            throw new InvalidOperationException("Cannot commit a foreign volume index without explicit approval.");

        if (mode != LtfsCandidateCommitMode.Rollback && candidate.Index.GenerationNumber < StableCheckpoint.GenerationNumber)
            throw new InvalidOperationException("Cannot commit an older generation unless rollback/import older generation is explicit.");

        Current = candidate.Index.Clone();
        StableCheckpoint = candidate.Index.Clone();
    }

    public LtfsIndex ApplyDataPartitionCheckpoint(ulong startBlock, DateTimeOffset updateTime)
    {
        var checkpoint = LtfsIndexUpdater.CreateDataPartitionCheckpoint(Current, startBlock, updateTime);
        Current = checkpoint.Clone();
        StableCheckpoint = checkpoint.Clone();
        return checkpoint;
    }

    public LtfsIndex ApplyIndexPartitionRefresh(ulong startBlock, DateTimeOffset updateTime)
    {
        var refreshed = LtfsIndexUpdater.CreateIndexPartitionRefresh(Current, startBlock, updateTime);
        Current = refreshed.Clone();
        StableCheckpoint = refreshed.Clone();
        return refreshed;
    }

    public static bool ShouldCheckpoint(LtfsIndexCounters counters, LtfsCheckpointPolicy policy, DateTimeOffset now, bool force = false)
    {
        if (force)
            return true;

        if (policy.MaxUnindexedBytes is > 0 && counters.UnindexedBytes >= policy.MaxUnindexedBytes.Value)
            return true;

        if (policy.MaxUnindexedFiles is > 0 && counters.UnindexedFiles >= policy.MaxUnindexedFiles.Value)
            return true;

        if (policy.MaxUnindexedAge is { } age && age > TimeSpan.Zero && now - counters.LastCheckpointTime >= age)
            return true;

        return false;
    }
}

public static class LtfsIndexUpdater
{
    public static LtfsIndex CreateDataPartitionCheckpoint(LtfsIndex source, ulong startBlock, DateTimeOffset updateTime)
    {
        return CreateDataPartitionCheckpoint(source, LtfsPartition.B, startBlock, updateTime);
    }

    public static LtfsIndex CreateDataPartitionCheckpoint(LtfsIndex source, LtfsPartition partition, ulong startBlock, DateTimeOffset updateTime)
    {
        ArgumentNullException.ThrowIfNull(source);

        var checkpoint = source.Clone();
        var previousLocation = checkpoint.Location.Clone();

        checkpoint.GenerationNumber += 1;
        checkpoint.UpdateTime = LtfsIndex.FormatLtfsTime(updateTime);
        checkpoint.Location = new LtfsLocation
        {
            Partition = partition,
            StartBlock = startBlock,
        };
        checkpoint.PreviousGenerationLocation = previousLocation;
        return checkpoint;
    }

    public static LtfsIndex CreateIndexPartitionRefresh(LtfsIndex source, ulong startBlock, DateTimeOffset updateTime)
    {
        ArgumentNullException.ThrowIfNull(source);

        var refreshed = source.Clone();
        var oldLocation = refreshed.Location.Clone();

        refreshed.UpdateTime = LtfsIndex.FormatLtfsTime(updateTime);
        refreshed.Location = new LtfsLocation
        {
            Partition = LtfsPartition.A,
            StartBlock = startBlock,
        };

        if (oldLocation.Partition == LtfsPartition.B)
            refreshed.PreviousGenerationLocation = oldLocation;

        return refreshed;
    }
}
