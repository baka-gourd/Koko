namespace Koko.Core.Scsi.Commands;

public sealed record ModeSenseData(
    byte[] Raw,
    byte[] PageData,
    int BlockDescriptorLength,
    long? CurrentBlockLengthBytes);

public static class ModeSenseDataParser
{
    public static ModeSenseData Parse6(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return new ModeSenseData(data.ToArray(), [], 0, null);

        var descriptorLength = data[3];
        var pageOffset = Math.Min(data.Length, 4 + descriptorLength);
        long? blockLength = null;

        if (descriptorLength >= 8 && data.Length >= 12)
            blockLength = (data[9] << 16) | (data[10] << 8) | data[11];

        return new ModeSenseData(
            data.ToArray(),
            data[pageOffset..].ToArray(),
            descriptorLength,
            blockLength);
    }
}
