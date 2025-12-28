namespace Koko.Core.Scsi.Codes;

public enum SenseCode : byte
{
    NoSense = 0x0,
    RecoveredError = 0x1,
    NotReady = 0x2,
    MediumError = 0x3,
    HardwareError = 0x4,
    IllegalRequest = 0x5,
    UnitAttention = 0x6,
    DataProtect = 0x7,
    BlankCheck = 0x8,
    VendorSpecific = 0x9,
    CopyAborted = 0xA,
    AbortedCommand = 0xB,
    Equal = 0xC,
    VolumeOverflow = 0xD,
    Miscompare = 0xE,
    Reserved = 0xF
}

public static class SenseCodeExtensions
{
    public static string ToText(this SenseCode code) => code switch
    {
        SenseCode.NoSense => "NO SENSE",
        SenseCode.RecoveredError => "RECOVERED ERROR",
        SenseCode.NotReady => "NOT READY",
        SenseCode.MediumError => "MEDIUM ERROR",
        SenseCode.HardwareError => "HARDWARE ERROR",
        SenseCode.IllegalRequest => "ILLEGAL REQUEST",
        SenseCode.UnitAttention => "UNIT ATTENTION",
        SenseCode.DataProtect => "DATA PROTECT",
        SenseCode.BlankCheck => "BLANK CHECK",
        SenseCode.VendorSpecific => "VENDOR SPECIFIC",
        SenseCode.CopyAborted => "COPY ABORTED",
        SenseCode.AbortedCommand => "ABORTED COMMAND",
        SenseCode.Equal => "EQUAL",
        SenseCode.VolumeOverflow => "VOLUME OVERFLOW",
        SenseCode.Miscompare => "MISCOMPARE",
        SenseCode.Reserved => "RESERVED",
        _ => $"UNKNOWN (0x{(byte)code:X2})"
    };
}