using System.Text;

using Koko.Core.Scsi.Codes;

namespace Koko.Core.Scsi.Parsers;

public static class SenseParser
{
    public static string ParseSense(ReadOnlySpan<byte> senseBuffer)
    {
        var sb = new StringBuilder();

        if (senseBuffer.Length == 0)
            return string.Empty;

        // VALID bit
        var valid = (senseBuffer[0] & 0x80) != 0;

        // Response code (fixed format = 0x70 / 0x71)
        var responseCode = (byte)(senseBuffer[0] & 0x7F);
        var fixedFormat = false;

        if (responseCode == 0x70)
        {
            sb.AppendLine("Error code represents current error");
            fixedFormat = true;
        }
        else if (responseCode == 0x71)
        {
            sb.AppendLine("Error code represents deferred error");
            fixedFormat = true;
        }

        if (!fixedFormat)
        {
            // 非 fixed format，仍然尽量输出 ASC/ASCQ
            if (senseBuffer.Length >= 14)
            {
                sb.Append("Additional code: ");
                sb.AppendLine(AdditionalSenseCode.GetOrUnknown(
                    senseBuffer[12],
                    senseBuffer[13]));
            }
            return sb.ToString();
        }

        // ---- Fixed format sense ----

        // sense[2] flags + sense key
        if (senseBuffer.Length >= 3)
        {
            var b2 = senseBuffer[2];

            if ((b2 & 0x80) != 0)
                sb.AppendLine("Filemark encountered");

            if ((b2 & 0x40) != 0)
                sb.AppendLine("EOM encountered");

            if ((b2 & 0x20) != 0)
                sb.AppendLine("Blocklen mismatch");

            var senseKey = (SenseCode)(b2 & 0x0F);
            sb.Append("Sense key: ");
            sb.AppendLine(senseKey.ToText());
        }

        // Information bytes (sense[3..6]) if VALID
        if (valid && senseBuffer.Length >= 7)
        {
            sb.Append("Info bytes: ");
            sb.AppendLine(
                Convert.ToHexString(senseBuffer.Slice(3, 4).ToArray()));
        }

        // byte addLen = senseBuffer.Length >= 8 ? senseBuffer[7] : (byte)0;

        // ASC / ASCQ
        byte asc = 0, ascq = 0;
        if (senseBuffer.Length >= 14)
        {
            asc = senseBuffer[12];
            ascq = senseBuffer[13];
        }

        // Sense-key specific (sense[15..17])
        if (senseBuffer.Length >= 18)
        {
            var b15 = senseBuffer[15];

            var sksv = (b15 & 0x80) != 0;
            var bitPointer = b15 & 0x07;

            var senseKeyRaw = senseBuffer.Length >= 3
                ? (byte)(senseBuffer[2] & 0x0F)
                : (byte)0;

            if (sksv)
            {
                var value = (senseBuffer[16] << 8) | senseBuffer[17];

                // 对标 VB
                if (senseKeyRaw == 0x05) // ILLEGAL REQUEST
                {
                    sb.AppendLine(
                        $"Error byte = {value} bit = {bitPointer}");
                }
                else if (senseKeyRaw == 0x00 || senseKeyRaw == 0x02)
                {
                    sb.AppendLine($"Progress = {value}");
                }
            }
            else
            {
                sb.Append("Drive Error Code = ");
                sb.AppendLine(
                    Convert.ToHexString(senseBuffer.Slice(16, 2).ToArray()));
            }
        }

        // Clean required flag (sense[21] bit3)
        if (senseBuffer.Length >= 22 &&
            (senseBuffer[21] & 0x08) != 0)
        {
            sb.AppendLine("Clean is required");
        }

        // Additional Sense Code text
        sb.Append("Additional code: ");
        sb.AppendLine(
            AdditionalSenseCode.GetOrUnknown(asc, ascq));

        return sb.ToString();
    }
}