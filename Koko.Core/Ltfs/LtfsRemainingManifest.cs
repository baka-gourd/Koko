namespace Koko.Core.Ltfs;

public sealed record LtfsRemainingManifestItem(
    string Name,
    string? SourcePath,
    string? DestinationPath,
    long Length,
    string Status,
    string? Reason = null);

public sealed record LtfsRemainingManifest(
    Guid VolumeUuid,
    ulong GenerationNumber,
    LtfsLocation LastStableLocation,
    string Reason,
    DateTimeOffset CreatedAt,
    IReadOnlyList<LtfsRemainingManifestItem> CompletedFiles,
    IReadOnlyList<LtfsRemainingManifestItem> RemainingFiles,
    Guid? VolumeSetId = null,
    int VolumeSequence = 1,
    Guid? PreviousVolumeUuid = null,
    string? NextAction = null,
    LtfsRemainingManifestItem? InterruptedFile = null);

public static class LtfsRemainingManifestSourceBuilder
{
    public static IReadOnlyList<LtfsWriteSource> ToWriteSources(LtfsRemainingManifest manifest, int sourceReadBufferBytes = 4 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.RemainingFiles
            .Where(x => string.Equals(x.Status, "Pending", StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.Status, "Interrupted", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace(x.SourcePath))
            .Select(x => LtfsWriteSource.FromFile(x.SourcePath!, x.DestinationPath ?? x.Name, sourceReadBufferBytes))
            .ToArray();
    }
}

internal sealed class LtfsEndOfMediumStopException : Exception
{
    public LtfsEndOfMediumStopException(string reason, bool committedCurrentBlock, Exception? innerException = null)
        : base(reason, innerException)
    {
        CommittedCurrentBlock = committedCurrentBlock;
    }

    public bool CommittedCurrentBlock { get; }
}
