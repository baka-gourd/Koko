namespace Koko.Web.Services;

public sealed record TapeMetadataOverviewDto(
    int TapeCount,
    int ArchiveCount,
    int MissingCount,
    DateTimeOffset? LastIndexedAtUtc);

public sealed record TapeMetadataQueryDto(
    string? Search = null,
    string? Barcode = null,
    bool IncludeMissing = true,
    int Skip = 0,
    int Take = 200);

public sealed record TapeMetadataQueryResultDto(
    int TotalCount,
    IReadOnlyList<TapeMetadataArchiveDto> Items);

public sealed record TapeMetadataBarcodeGroupQueryDto(
    string? Search = null,
    bool IncludeMissing = true);

public sealed record TapeMetadataBarcodeGroupResultDto(
    int TotalCount,
    IReadOnlyList<TapeMetadataBarcodeGroupDto> Items);

public sealed record TapeMetadataBarcodeGroupDto(
    string Barcode,
    int ArchiveCount,
    TapeMetadataArchiveDto LatestArchive);

public sealed record TapeMetadataArchiveDto(
    string ArchiveXxHash128,
    string Barcode,
    string ArchivePath,
    string RelativePath,
    string ArchiveName,
    long ArchiveSizeBytes,
    DateTimeOffset ArchiveLastWriteTimeUtc,
    DateTimeOffset IndexedAtUtc,
    bool Missing,
    string Status,
    string? Error,
    Guid? VolumeUuid,
    long? GenerationNumber,
    string? LtfsUpdateTime,
    string? LocationPartition,
    long? LocationStartBlock,
    long? FileCount,
    long? DirectoryCount,
    long? LogicalBytes,
    long? TotalBytes,
    long? UsedBytes,
    long? AvailableBytes);

public sealed record TapeMetadataPruneResultDto(int DeletedRecords);
