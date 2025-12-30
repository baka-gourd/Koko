namespace Koko.Core.Scsi.Codes.Cartridges;

public readonly record struct Ibm3592CartridgeType(string Abbr)
{
    public override string ToString() => Abbr;

    public static bool TryFromAbbr(string? abbr, out Ibm3592CartridgeType value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(abbr))
            return false;

        var s = abbr.Trim().ToUpperInvariant();
        if (s is not ['J', _]) return false;
        value = new Ibm3592CartridgeType(s);
        return true;

    }
}