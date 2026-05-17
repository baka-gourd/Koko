namespace Koko.Core.Scsi.Commands;

public readonly record struct FormatMediumCommand(
    bool Immediate,
    byte FormatCode,
    ushort TransferLength = 0,
    uint TimeoutSeconds = 600)
{
    public FormatMediumCommand() : this(false, 0, 0, 600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        FormatMediumCommand request,
        out ScsiCommandResult result)
    {
        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x04;
        if (request.Immediate)
            cdb[1] |= 0x01;

        cdb[2] = (byte)(request.FormatCode & 0x0F);
        ScsiCdbWriter.WriteUInt16BigEndian(cdb, 3, request.TransferLength);

        return ScsiCommandExecutor.TryExecuteNoData(
            drive,
            cdb,
            DataDirection.In,
            request.TimeoutSeconds,
            out result);
    }
}
