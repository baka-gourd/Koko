using System.Buffers.Binary;

namespace Koko.Core.Scsi.Commands;

internal static class ScsiCdbWriter
{
    public static void WriteUInt16BigEndian(Span<byte> buffer, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(offset, 2), value);

    public static void WriteUInt24BigEndian(Span<byte> buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 16);
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)value;
    }

    public static void WriteUInt32BigEndian(Span<byte> buffer, int offset, uint value)
        => BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(offset, 4), value);

    public static void WriteUInt64BigEndian(Span<byte> buffer, int offset, ulong value)
        => BinaryPrimitives.WriteUInt64BigEndian(buffer.Slice(offset, 8), value);

    public static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> buffer, int offset)
        => BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset, 2));

    public static uint ReadUInt24BigEndian(ReadOnlySpan<byte> buffer, int offset)
        => (uint)((buffer[offset] << 16) | (buffer[offset + 1] << 8) | buffer[offset + 2]);

    public static uint ReadUInt32BigEndian(ReadOnlySpan<byte> buffer, int offset)
        => BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset, 4));

    public static ulong ReadUInt64BigEndian(ReadOnlySpan<byte> buffer, int offset)
        => BinaryPrimitives.ReadUInt64BigEndian(buffer.Slice(offset, 8));
}
