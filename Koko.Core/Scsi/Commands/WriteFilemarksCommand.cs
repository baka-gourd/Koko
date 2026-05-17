namespace Koko.Core.Scsi.Commands;

public readonly record struct WriteFilemarksCommand(
    bool Immediate,
    uint FilemarkCount,
    bool WriteSetMarks = false,
    uint TimeoutSeconds = 600)
{
    public WriteFilemarksCommand() : this(false, 0, false, 600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        WriteFilemarksCommand request,
        out ScsiCommandResult result)
    {
        if (request.FilemarkCount > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(request.FilemarkCount), "Filemark count exceeds 24-bit field.");

        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x10;
        if (request.WriteSetMarks)
            cdb[1] |= 0x02;
        if (request.Immediate)
            cdb[1] |= 0x01;

        ScsiCdbWriter.WriteUInt24BigEndian(cdb, 2, request.FilemarkCount);

        return ScsiCommandExecutor.TryExecuteNoData(
            drive,
            cdb,
            DataDirection.In,
            request.TimeoutSeconds,
            out result);
    }
}
