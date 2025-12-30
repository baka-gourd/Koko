using System;
using System.Text;

namespace Koko.Helpers;

public static class HexDump
{
    public static string Format(ReadOnlySpan<byte> data, int bytesPerLine = 16)
    {
        if (data.Length == 0)
            return string.Empty;

        var sb = new StringBuilder();

        for (int offset = 0; offset < data.Length; offset += bytesPerLine)
        {
            int lineLen = Math.Min(bytesPerLine, data.Length - offset);

            // |    0h:
            sb.Append("| ");
            sb.Append(offset.ToString("X4"));
            sb.Append("h: ");

            // Hex part
            for (int i = 0; i < bytesPerLine; i++)
            {
                if (i < lineLen)
                {
                    sb.Append(data[offset + i].ToString("X2"));
                    sb.Append(' ');
                }
                else
                {
                    sb.Append("   "); // 对齐
                }
            }

            // ASCII part
            sb.Append(' ');

            for (int i = 0; i < lineLen; i++)
            {
                byte b = data[offset + i];
                sb.Append(IsPrintableAscii(b) ? (char)b : '.');
            }

            // 补齐 ASCII 到 16 列（末行）
            for (int i = lineLen; i < bytesPerLine; i++)
                sb.Append(' ');

            sb.Append(" |");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static bool IsPrintableAscii(byte b)
        => b >= 0x20 && b <= 0x7E;
}