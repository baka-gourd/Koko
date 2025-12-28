using System.Collections.Frozen;

namespace Koko.Core.Scsi.Codes;

public class DensityCode
{
    private static readonly FrozenDictionary<byte, string> Map =
        new Dictionary<byte, string>
        {
            { 0x40, "L1" },
            { 0x42, "L2" },
            { 0x44, "L3" },
            { 0x46, "L4" },
            { 0x58, "L5" },
            { 0x5A, "L6" },
            { 0x5C, "L7" },
            { 0x5D, "M8" },
            { 0x5E, "L8" },
            { 0x60, "L9" },
        }.ToFrozenDictionary();

    public static bool TryGet(byte code, out string text)
        => Map.TryGetValue(code, out text!);

    public static string GetOrHex(byte code)
        => Map.TryGetValue(code, out var text)
            ? text
            : $"0x{code:X2}";
}