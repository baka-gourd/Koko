namespace Koko.Core.Scsi.Codes.Cartridges;

public enum ParticleType
{
    Unknown,
    BaFe,
    MP
}

public enum SubstrateType
{
    Unknown,
    SPALTAN,
    PEN
}

public enum ServoBandId
{
    Unknown,
    LegacyUDIM,
    NonUDIM
}

public sealed class TapeCartridgeProfile(
    ushort cartridgeType,
    string? format,
    string vendor,
    string sn,
    ParticleType particleType,
    SubstrateType substrateType,
    string? manufacturingDate = null,
    ushort tapeLengthQuarterMetres = 0,
    ushort mediaCode = 0,
    ServoBandId servoBandId = ServoBandId.Unknown)
{
    public CartridgeId Id { get; } = CartridgeTypeResolver.Resolve(cartridgeType, format);

    // ---- Fields populated from CM pages (HP LTO path) ----
    // NOTE: These are optional/absent for some media types or older CM versions.
    public string? ManufacturingDate => manufacturingDate;
    public ushort TapeLengthQuarterMetres => tapeLengthQuarterMetres;
    public ushort MediaCode => mediaCode;
    public ServoBandId ServoBandId => servoBandId;

    public int CMLength
        => LTOMediaCapabilities.GetCMLength(Id.Abbr);

    public string Vendor => vendor;
    public string SN => sn;
    public string? Format => format;
    public ParticleType ParticleType => particleType;
    public SubstrateType SubstrateType => substrateType;

    public int TapeLengthMeters
        => Id.Family switch
        {
            CartridgeFamily.Lto when Id.LtoDensity is not null
                => LTOMediaCapabilities.GetTapeLengthMeters(Id.LtoDensity.Value),
            CartridgeFamily.Cleaning
                => LTOMediaCapabilities.GetTapeLengthMeters("CU"),
            _ => 0
        };

    public bool IsLaterThan(LTODensityCode code)
    {
        if (Id.LtoDensity is null || Id.LtoDensity.HasValue == false)
        {
            throw new Exception("Cannot compare version");
        }
        return Id.LtoDensity.Value.IsLaterThan(code);
    }

    public int KbPerDataset
        => (Id.LtoDensity is not null) ? LTOMediaCapabilities.GetKbPerDataset(Id.LtoDensity.Value) : 0;

    public int CcqPerDataset
        => (Id.LtoDensity is not null) ? LTOMediaCapabilities.GetCcqPerDataset(Id.LtoDensity.Value) : 0;

    public int SetsPerWrap
        => (Id.LtoDensity is not null) ? LTOMediaCapabilities.GetSetsPerWrap(Id.LtoDensity.Value) : 0;

    public double MbPerWrapMetre
        => (Id.LtoDensity is not null) ? LTOMediaCapabilities.GetMbPerWrapMetre(Id.LtoDensity.Value) : 0d;

    public int TapeLuLife
        => (Id.LtoDensity is not null) ? LTOMediaCapabilities.GetTapeLuLife(Id.LtoDensity.Value) : 0;

    public int TapeLifeInVols
        => (Id.LtoDensity is not null) ? LTOMediaCapabilities.GetTapeLifeInVols(Id.LtoDensity.Value) : 0;

    public int WrapLengthMeters
        => (Id.LtoDensity is not null) ? LTOMediaCapabilities.GetWrapLengthMeters(Id.LtoDensity.Value) : 0;

    public int NoWrapsOnTape
        => (Id.LtoDensity is not null) ? LTOMediaCapabilities.GetNoWrapsOnTape(Id.LtoDensity.Value) : 0;

    public int MinDatasetsForAssessingCapacityLoss
        => (Id.LtoDensity is not null)
            ? LTOMediaCapabilities.GetMinDatasetsForAssessingCapacityLoss(Id.LtoDensity.Value)
            : 0;

    // ------- IBM 3592 placeholders (you will fill later) -------
    // TODO
}