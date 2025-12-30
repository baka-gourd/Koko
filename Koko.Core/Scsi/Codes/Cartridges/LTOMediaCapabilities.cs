using System.Collections.Frozen;

namespace Koko.Core.Scsi.Codes.Cartridges;

public static class LTOMediaCapabilities
{
    private static int GetOrZero(FrozenDictionary<byte, int> map, LTODensityCode density)
        => map.GetValueOrDefault(density.Code, 0);

    private static double GetOrZero(FrozenDictionary<byte, double> map, LTODensityCode density)
        => map.GetValueOrDefault(density.Code, 0d);

    // ---------- GetCMLength (includes CU) ----------
    private static readonly FrozenDictionary<string, int> CmLengthByAbbr =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["CU"] = 4096,
            ["L1"] = 4096,
            ["L2"] = 4096,
            ["L3"] = 4096,
            ["L4"] = 8160,
            ["L5"] = 8160,
            ["L6"] = 16352,
            ["L7"] = 16352,
            ["M8"] = 16352,
            ["L8"] = 16352,
            ["L9"] = 32736,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static int GetCMLength(string cartridgeTypeAbbr)
        => CmLengthByAbbr.GetValueOrDefault(cartridgeTypeAbbr, 0);

    // ---------- KB_PER_DATASET ----------
    private static readonly FrozenDictionary<byte, int> KbPerDataset =
        new Dictionary<byte, int>
        {
            [0x40] = 404,   // L1
            [0x42] = 404,   // L2
            [0x44] = 1617,  // L3
            [0x46] = 1590,  // L4
            [0x58] = 2473,  // L5
            [0x5A] = 2473,  // L6
            [0x5C] = 5032,  // L7
            [0x5D] = 5032,  // M8
            [0x5E] = 5032,  // L8
            [0x60] = 9806,  // L9
        }.ToFrozenDictionary();

    public static int GetKbPerDataset(LTODensityCode density) => GetOrZero(KbPerDataset, density);

    // ---------- CCQ_PER_DATASET ----------
    private static readonly FrozenDictionary<byte, int> CcqPerDataset =
        new Dictionary<byte, int>
        {
            [0x40] = 64,
            [0x42] = 64,
            [0x44] = 128,
            [0x46] = 128,
            [0x58] = 192,
            [0x5A] = 192,
            [0x5C] = 192,
            [0x5D] = 192,
            [0x5E] = 192,
            [0x60] = 384,
        }.ToFrozenDictionary();

    public static int GetCcqPerDataset(LTODensityCode density) => GetOrZero(CcqPerDataset, density);

    // ---------- SETS_PER_WRAP ----------
    private static readonly FrozenDictionary<byte, int> SetsPerWrap =
        new Dictionary<byte, int>
        {
            [0x40] = 5500,
            [0x42] = 8200,
            [0x44] = 6000,
            [0x46] = 9500,
            [0x58] = 7800,
            [0x5A] = 7805,
            [0x5C] = 10950,
            [0x5D] = 10950,
            [0x5E] = 11660,
            [0x60] = 6770,
        }.ToFrozenDictionary();

    public static int GetSetsPerWrap(LTODensityCode density) => GetOrZero(SetsPerWrap, density);

    // ---------- MB_PER_WRAP_METRE (Double) ----------
    private static readonly FrozenDictionary<byte, double> MbPerWrapMetre =
        new Dictionary<byte, double>
        {
            [0x40] = 3.84,
            [0x42] = 5.75,
            [0x44] = 14.98,
            [0x46] = 19.27,
            [0x58] = 23.9,
            [0x5A] = 23.89,
            [0x5C] = 59.85,
            [0x5D] = 59.85,
            [0x5E] = 63.64,
            [0x60] = 66.59,
        }.ToFrozenDictionary();

    public static double GetMbPerWrapMetre(LTODensityCode density) => GetOrZero(MbPerWrapMetre, density);

    // ---------- TAPE_LU_LIFE ----------
    private static readonly FrozenDictionary<byte, int> TapeLuLife =
        new Dictionary<byte, int>
        {
            [0x40] = 20000,
            [0x42] = 20000,
            [0x44] = 20000,
            [0x46] = 20000,
            [0x58] = 20000,
            [0x5A] = 20000,
            [0x5C] = 20000,
            [0x5D] = 20000,
            [0x5E] = 20000,
            [0x60] = 20000,
        }.ToFrozenDictionary();

    public static int GetTapeLuLife(LTODensityCode density) => GetOrZero(TapeLuLife, density);

    // ---------- TAPE_LIFE_IN_VOLS ----------
    private static readonly FrozenDictionary<byte, int> TapeLifeInVols =
        new Dictionary<byte, int>
        {
            [0x40] = 260,
            [0x42] = 260,
            [0x44] = 260,
            [0x46] = 260,
            [0x58] = 260,
            [0x5A] = 130,
            [0x5C] = 130,
            [0x5D] = 98,
            [0x5E] = 75,
            [0x60] = 55,
        }.ToFrozenDictionary();

    public static int GetTapeLifeInVols(LTODensityCode density) => GetOrZero(TapeLifeInVols, density);

    // ---------- WRAP_LEN_IN_MTRS ----------
    private static readonly FrozenDictionary<byte, int> WrapLenMeters =
        new Dictionary<byte, int>
        {
            [0x40] = 580,
            [0x42] = 580,
            [0x44] = 648,
            [0x46] = 783,
            [0x58] = 808,
            [0x5A] = 808,
            [0x5C] = 922,
            [0x5D] = 922,
            [0x5E] = 922,
            [0x60] = 997,
        }.ToFrozenDictionary();

    public static int GetWrapLengthMeters(LTODensityCode density) => GetOrZero(WrapLenMeters, density);

    // ---------- TAPE_LEN_IN_MTRS (includes CU) ----------
    private static readonly FrozenDictionary<byte, int> TapeLenMetersByDensity =
        new Dictionary<byte, int>
        {
            [0x40] = 609,
            [0x42] = 609,
            [0x44] = 680,
            [0x46] = 820,
            [0x58] = 846,
            [0x5A] = 846,
            [0x5C] = 960,
            [0x5D] = 960,
            [0x5E] = 960,
            [0x60] = 1034,
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, int> TapeLenMetersByAbbr =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["CU"] = 319,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static int GetTapeLengthMeters(LTODensityCode density) => GetOrZero(TapeLenMetersByDensity, density);

    public static int GetTapeLengthMeters(string cartridgeTypeAbbr)
        => TapeLenMetersByAbbr.GetValueOrDefault(cartridgeTypeAbbr, 0);

    // ---------- NO_WRAPS_ON_TAPE ----------
    private static readonly FrozenDictionary<byte, int> NoWrapsOnTape =
        new Dictionary<byte, int>
        {
            [0x40] = 48,
            [0x42] = 64,
            [0x44] = 44,
            [0x46] = 56,
            [0x58] = 80,
            [0x5A] = 136,
            [0x5C] = 112,
            [0x5D] = 168,
            [0x5E] = 208,
            [0x60] = 280,
        }.ToFrozenDictionary();

    public static int GetNoWrapsOnTape(LTODensityCode density) => GetOrZero(NoWrapsOnTape, density);

    // ---------- MIN_DATASETS_FOR_ASSESSING_CAPACITY_LOSS ----------
    private static readonly FrozenDictionary<byte, int> MinDatasetsForCapacityLoss =
        new Dictionary<byte, int>
        {
            [0x40] = 11064,
            [0x42] = 16600,
            [0x44] = 12500,
            [0x46] = 19500,
            [0x58] = 15920,
            [0x5A] = 15620,
            [0x5C] = 11060,
            [0x5D] = 11060,
            [0x5E] = 12020,
            [0x60] = 13540,
        }.ToFrozenDictionary();

    public static int GetMinDatasetsForAssessingCapacityLoss(LTODensityCode density)
        => GetOrZero(MinDatasetsForCapacityLoss, density);
}