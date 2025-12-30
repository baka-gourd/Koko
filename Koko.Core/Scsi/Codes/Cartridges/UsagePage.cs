namespace Koko.Core.Scsi.Codes.Cartridges;

public readonly record struct UsagePage(
    int Index,
    byte[] Data0,
    int Data1
);