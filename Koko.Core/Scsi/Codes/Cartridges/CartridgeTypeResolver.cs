namespace Koko.Core.Scsi.Codes.Cartridges;

public static class CartridgeTypeResolver
{
    public static CartridgeId Resolve(ushort cartridgeType, string? format)
    {
        if (((cartridgeType >> 15) & 1) == 1)
            return new CartridgeId(CartridgeFamily.Cleaning, "CU");

        var low = (byte)(cartridgeType & 0xFF);

        switch (low)
        {
            case 1: return FromLtoLabel("L1");
            case 2: return FromLtoLabel("L2");
            case 4: return FromLtoLabel("L3");
            case 8: return FromLtoLabel("L4");
            case 16: return FromLtoLabel("L5");
            case 32: return FromLtoLabel("L6");
            case 64:
                {
                    var isM8 = format?.IndexOf("Type M", StringComparison.OrdinalIgnoreCase) >= 0;
                    return FromLtoLabel(isM8 ? "M8" : "L7");
                }
            case 128: return FromLtoLabel("L8");
            case 129: return FromLtoLabel("L9");
        }

        var abbr = cartridgeType switch
        {
            5126 => "JA",
            13 => "JB",
            15 => "JC",
            17 => "JD",
            19 => "JE",
            21 => "JF",
            13318 => "JJ",
            8207 => "JK",
            8209 => "JL",
            8211 => "JM",
            8213 => "JN",
            _ => ""
        };

        if (!string.IsNullOrEmpty(abbr) && Ibm3592CartridgeType.TryFromAbbr(abbr, out var t))
            return new CartridgeId(CartridgeFamily.Ibm3592, abbr, Ibm3592Type: t);

        return new CartridgeId(CartridgeFamily.Unknown, "");
    }

    private static CartridgeId FromLtoLabel(string abbr)
    {
        if (LTODensityCode.TryParse(abbr, out var density))
            return new CartridgeId(CartridgeFamily.Lto, abbr, LtoDensity: density);

        return new CartridgeId(CartridgeFamily.Lto, abbr);
    }
}