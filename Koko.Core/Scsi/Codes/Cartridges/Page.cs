namespace Koko.Core.Scsi.Codes.Cartridges;

public readonly record struct Page(
    int Key,
    int Version,
    int Offset,
    int Length,
    PageType Type)
{
    public static Page Create(
        int key,
        int version,
        PageType type,
        int offset = -1,
        int length = -1)
        => new(key, version, offset, length, type);
}

public enum PageType
{
    Unprotected,
    Protected
}