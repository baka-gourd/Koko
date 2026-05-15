namespace Koko.Core.Scsi.Commands;

public readonly record struct EraseCommand(
    bool Immediate,
    bool LongErase,
    uint TimeoutSeconds = 60)
{
    public static bool TryExecute(
        IScsiDrive drive,
        EraseCommand request,
        out ScsiCommandResult result)
    {
        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x19;
        if (request.Immediate)
            cdb[1] |= 0x02;
        if (request.LongErase)
            cdb[1] |= 0x01;

        return ScsiCommandExecutor.TryExecuteNoData(
            drive,
            cdb,
            DataDirection.Unspecified,
            request.TimeoutSeconds,
            out result);
    }
}
