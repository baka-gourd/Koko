namespace Koko.Core.Scsi.Commands;

public readonly record struct RewindCommand(
    bool Immediate,
    uint TimeoutSeconds = 600)
{
    public RewindCommand() : this(false, 600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        RewindCommand request,
        out ScsiCommandResult result)
    {
        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x01;
        if (request.Immediate)
            cdb[1] |= 0x01;

        return ScsiCommandExecutor.TryExecuteNoData(
            drive,
            cdb,
            DataDirection.In,
            request.TimeoutSeconds,
            out result);
    }
}
