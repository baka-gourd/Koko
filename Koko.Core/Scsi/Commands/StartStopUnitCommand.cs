using Koko.Core.Scsi;

namespace Koko.Core.Scsi.Commands;

public readonly record struct StartStopUnitCommand(
    bool Start,
    bool LoadEject,
    bool Immediate = false,
    uint TimeoutSeconds = 10)
{
    public static bool TryExecute(
        IScsiDrive drive,
        StartStopUnitCommand request,
        out ScsiCommandResult result)
    {
        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x1B;
        if (request.Immediate)
            cdb[1] |= 0x01;

        if (request.LoadEject)
            cdb[4] |= 0x02;
        if (request.Start)
            cdb[4] |= 0x01;

        return ScsiCommandExecutor.TryExecuteNoData(
            drive,
            cdb,
            DataDirection.Unspecified,
            request.TimeoutSeconds,
            out result);
    }
}
