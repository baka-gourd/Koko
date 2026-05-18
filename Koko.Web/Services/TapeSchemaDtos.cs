namespace Koko.Web.Services;

public sealed record TapeSchemaFileListDto(
    string ArchiveXxHash128,
    Guid VolumeUuid,
    ulong GenerationNumber,
    int TotalCount,
    IReadOnlyList<TapeSchemaFileDto> Items);

public sealed record TapeSchemaFileDto(
    string Path,
    string DirectoryPath,
    string Name,
    long Length,
    bool ReadOnly,
    bool OpenForWrite,
    string CreationTime,
    string ChangeTime,
    string ModifyTime,
    string AccessTime,
    string BackupTime,
    long FileUid,
    string? Symlink,
    IReadOnlyList<TapeSchemaExtendedAttributeDto> ExtendedAttributes,
    IReadOnlyList<TapeSchemaExtentDto> Extents);

public sealed record TapeSchemaExtendedAttributeDto(string Key, string Value);

public sealed record TapeSchemaExtentDto(
    long FileOffset,
    string Partition,
    long StartBlock,
    long ByteOffset,
    long ByteCount);
