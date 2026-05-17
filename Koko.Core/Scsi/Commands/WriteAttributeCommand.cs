namespace Koko.Core.Scsi.Commands;

public readonly record struct WriteAttributeCommand(
    byte VolumeNumber,
    byte PartitionNumber,
    ReadOnlyMemory<byte> ParameterList,
    uint TimeoutSeconds = 600)
{
    public WriteAttributeCommand() : this(0, 0, default, 600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        WriteAttributeCommand request,
        out ScsiCommandResult result)
    {
        Span<byte> cdb = stackalloc byte[16];
        cdb.Clear();

        cdb[0] = 0x8D;
        cdb[5] = request.VolumeNumber;
        cdb[7] = request.PartitionNumber;
        ScsiCdbWriter.WriteUInt32BigEndian(cdb, 10, (uint)request.ParameterList.Length);

        return ScsiCommandExecutor.TryExecuteWrite(
            drive,
            cdb,
            request.ParameterList,
            request.TimeoutSeconds,
            out result);
    }

    public static byte[] BuildParameterList(IEnumerable<MamAttribute> attributes)
    {
        if (attributes is null) throw new ArgumentNullException(nameof(attributes));

        var ordered = attributes.OrderBy(a => a.Id).ToArray();
        var attributeBytes = ordered.Sum(a => a.EncodedLength);
        var totalLength = 4 + attributeBytes;

        var buffer = new byte[totalLength];
        ScsiCdbWriter.WriteUInt32BigEndian(buffer, 0, (uint)attributeBytes);

        var offset = 4;
        foreach (var attr in ordered)
        {
            attr.WriteTo(buffer.AsSpan(offset, attr.EncodedLength));
            offset += attr.EncodedLength;
        }

        return buffer;
    }
}
