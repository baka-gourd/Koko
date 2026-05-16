namespace Koko.Core.Scsi.Commands;

public readonly record struct LoadUnloadCommand(
    bool Immediate,
    bool Hold,
    bool Retension,
    bool Load,
    uint TimeoutSeconds = 60)
{
    public LoadUnloadCommand() : this(false, false, false, false, 60)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        LoadUnloadCommand request,
        out ScsiCommandResult result)
    {
        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x1B;
        if (request.Immediate)
            cdb[1] |= 0x01;

        if (request.Hold)
            cdb[4] |= 0x08;
        if (request.Retension)
            cdb[4] |= 0x02;
        if (request.Load)
            cdb[4] |= 0x01;

        return ScsiCommandExecutor.TryExecuteNoData(
            drive,
            cdb,
            DataDirection.In,
            request.TimeoutSeconds,
            out result);
    }
}
