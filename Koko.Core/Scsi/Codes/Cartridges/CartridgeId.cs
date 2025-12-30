namespace Koko.Core.Scsi.Codes.Cartridges;

public enum CartridgeFamily
{
    Unknown = 0,
    Lto,
    Ibm3592,
    Cleaning
}

public readonly record struct CartridgeId(
    CartridgeFamily Family,
    string Abbr,
    LTODensityCode? LtoDensity = null,
    Ibm3592CartridgeType? Ibm3592Type = null
);