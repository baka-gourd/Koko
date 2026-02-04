namespace Koko.Core.Scsi.Commands;

public enum MamAttributeFormat : byte
{
    Binary = 0b00,
    Ascii = 0b01,
    Text = 0b10
}

public readonly record struct MamAttribute(
    ushort Id,
    MamAttributeFormat Format,
    ReadOnlyMemory<byte> Value,
    bool ReadOnly = false)
{
    public int EncodedLength => 5 + Value.Length;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < EncodedLength)
            throw new ArgumentException("Destination span is too small.", nameof(destination));

        ScsiCdbWriter.WriteUInt16BigEndian(destination, 0, Id);
        destination[2] = (byte)((ReadOnly ? 0x80 : 0x00) | ((byte)Format & 0x03));
        ScsiCdbWriter.WriteUInt16BigEndian(destination, 3, (ushort)Value.Length);
        Value.Span.CopyTo(destination.Slice(5, Value.Length));
    }
}
