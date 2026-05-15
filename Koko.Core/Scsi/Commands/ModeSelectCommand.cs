namespace Koko.Core.Scsi.Commands;

public readonly record struct ModeSelectCommand(
    bool Use10Byte,
    bool PageFormat,
    bool SavePages,
    ReadOnlyMemory<byte> ParameterList,
    ushort ParameterListLength = 0,
    uint TimeoutSeconds = 10)
{
    public static bool TryExecute(
        IScsiDrive drive,
        ModeSelectCommand request,
        out ScsiCommandResult result)
    {
        if (request.ParameterList.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(request.ParameterList), "Parameter list exceeds 16-bit length field.");

        var listLength = request.ParameterListLength != 0
            ? request.ParameterListLength
            : (ushort)request.ParameterList.Length;

        Span<byte> cdb = stackalloc byte[request.Use10Byte ? 10 : 6];
        cdb.Clear();

        if (request.Use10Byte)
        {
            cdb[0] = 0x55;
            if (request.PageFormat)
                cdb[1] |= 0x10;
            if (request.SavePages)
                cdb[1] |= 0x01;

            ScsiCdbWriter.WriteUInt16BigEndian(cdb, 7, listLength);
        }
        else
        {
            if (listLength > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(request.ParameterListLength), "Parameter list length exceeds 6-byte CDB limit.");

            cdb[0] = 0x15;
            if (request.PageFormat)
                cdb[1] |= 0x10;
            if (request.SavePages)
                cdb[1] |= 0x01;

            cdb[4] = (byte)listLength;
        }

        return ScsiCommandExecutor.TryExecuteWrite(
            drive,
            cdb,
            request.ParameterList,
            request.TimeoutSeconds,
            out result);
    }
}
