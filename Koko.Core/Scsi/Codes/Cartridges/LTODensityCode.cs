using System.Collections.Frozen;
using System.Globalization;

namespace Koko.Core.Scsi.Codes.Cartridges;

public readonly struct LTODensityCode : IEquatable<LTODensityCode>
{
    public byte Code { get; }

    private LTODensityCode(byte code) => Code = code;

    public static readonly LTODensityCode L1 = new(0x40);
    public static readonly LTODensityCode L2 = new(0x42);
    public static readonly LTODensityCode L3 = new(0x44);
    public static readonly LTODensityCode L4 = new(0x46);
    public static readonly LTODensityCode L5 = new(0x58);
    public static readonly LTODensityCode L6 = new(0x5A);
    public static readonly LTODensityCode L7 = new(0x5C);
    public static readonly LTODensityCode M8 = new(0x5D);
    public static readonly LTODensityCode L8 = new(0x5E);
    public static readonly LTODensityCode L9 = new(0x60);

    private static readonly (byte Code, string Text)[] Entries =
    [
        (0x40, "L1"),
        (0x42, "L2"),
        (0x44, "L3"),
        (0x46, "L4"),
        (0x58, "L5"),
        (0x5A, "L6"),
        (0x5C, "L7"),
        (0x5D, "M8"),
        (0x5E, "L8"),
        (0x60, "L9")
    ];

    private static readonly FrozenDictionary<byte, int> CodeToOrder =
        Entries.Select((e, i) => (e.Code, Order: i))
            .ToDictionary(x => x.Code, x => x.Order)
            .ToFrozenDictionary();

    private static readonly FrozenDictionary<byte, string> CodeToText =
        Entries.ToDictionary(e => e.Code, e => e.Text)
               .ToFrozenDictionary();

    private static readonly FrozenDictionary<string, byte> TextToCode =
        Entries.ToDictionary(e => e.Text, e => e.Code, StringComparer.OrdinalIgnoreCase)
               .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static LTODensityCode FromCode(byte code) => new(code);

    public bool IsLaterThan(LTODensityCode other)
    {
        if (!CodeToOrder.TryGetValue(Code, out var thisOrder))
            return false;

        if (!CodeToOrder.TryGetValue(other.Code, out var otherOrder))
            return false;

        return thisOrder >= otherOrder;
    }

    public static bool TryFromKnownCode(byte code, out LTODensityCode value)
    {
        if (CodeToText.ContainsKey(code))
        {
            value = new LTODensityCode(code);
            return true;
        }

        value = default;
        return false;
    }

    public static bool TryParse(string? input, out LTODensityCode value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        var s = input.Trim();

        // 1) Label -> code
        if (TextToCode.TryGetValue(s, out var codeFromLabel))
        {
            value = new LTODensityCode(codeFromLabel);
            return true;
        }

        // 2) 0xNN -> hex
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var hex = s[2..].Trim();
            return TryParseHexByte(hex, out value);
        }

        // 3) NN (2 hex digits) -> hex
        if (s.Length is 1 or 2)
        {
            // allow "A" -> 0x0A, "5A" -> 0x5A
            if (TryParseHexByte(s, out value))
                return true;
        }

        // 4) decimal byte
        if (byte.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
        {
            value = new LTODensityCode(dec);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Try get known label from code.
    /// </summary>
    public static bool TryGetText(byte code, out string text)
        => CodeToText.TryGetValue(code, out text!);

    /// <summary>
    /// Get known label; otherwise return hex string like "0x5A".
    /// </summary>
    public static string GetTextOrHex(byte code)
        => CodeToText.TryGetValue(code, out var text)
            ? text
            : $"0x{code:X2}";

    /// <summary>
    /// Try get protocol code from known label.
    /// </summary>
    public static bool TryGetCode(string text, out byte code)
        => TextToCode.TryGetValue(text, out code);

    /// <summary>
    /// Whether the code is known in the mapping table.
    /// </summary>
    public bool IsKnown => CodeToText.ContainsKey(Code);

    /// <summary>
    /// Known label; otherwise hex.
    /// </summary>
    public string TextOrHex => GetTextOrHex(Code);

    public override string ToString() => TextOrHex;

    public bool Equals(LTODensityCode other) => Code == other.Code;
    public override bool Equals(object? obj) => obj is LTODensityCode other && Equals(other);
    public override int GetHashCode() => Code;

    public static bool operator ==(LTODensityCode left, LTODensityCode right) => left.Equals(right);
    public static bool operator !=(LTODensityCode left, LTODensityCode right) => !left.Equals(right);

    private static bool TryParseHexByte(string hex, out LTODensityCode value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(hex))
            return false;

        var h = hex.Trim();

        // normalize 1-2 hex digits
        if (h.Length > 2)
            return false;

        if (byte.TryParse(h, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b))
        {
            value = new LTODensityCode(b);
            return true;
        }

        return false;
    }
}