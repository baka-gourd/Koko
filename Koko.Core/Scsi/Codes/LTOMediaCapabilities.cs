using Koko.Core.Scsi.Codes.Cartridges;

using System.Collections.Frozen;

namespace Koko.Core.Scsi.Codes;

public static class LTOMediaCapabilities
{
    private static readonly FrozenDictionary<byte, int> NoWrapsOnTape =
        new Dictionary<byte, int>
        {
            [0x40] = 48,   // L1
            [0x42] = 64,   // L2
            [0x44] = 44,   // L3
            [0x46] = 56,   // L4
            [0x58] = 80,   // L5
            [0x5A] = 136,  // L6
            [0x5C] = 112,  // L7
            [0x5D] = 168,  // M8
            [0x5E] = 208,  // L8
            [0x60] = 280,  // L9
        }.ToFrozenDictionary();

    public static int GetNoWrapsOnTape(LTODensityCode density)
        => NoWrapsOnTape.GetValueOrDefault(density.Code, 0);

    public static bool TryGetNoWrapsOnTape(LTODensityCode density, out int value)
        => NoWrapsOnTape.TryGetValue(density.Code, out value);

    public static int GetNoWrapsOnTape(byte densityCode)
        => NoWrapsOnTape.GetValueOrDefault(densityCode, 0);

    public static bool TryGetNoWrapsOnTape(byte densityCode, out int value)
        => NoWrapsOnTape.TryGetValue(densityCode, out value);
}