namespace Koko.Core.Scsi.Commands;

public readonly record struct PreventAllowMediumRemovalCommand(
    bool Prevent,
    uint TimeoutSeconds = 600)
{
    public PreventAllowMediumRemovalCommand() : this(false, 600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        PreventAllowMediumRemovalCommand request,
        out ScsiCommandResult result)
    {
        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x1E;
        cdb[4] = (byte)(request.Prevent ? 0x01 : 0x00);

        return ScsiCommandExecutor.TryExecuteNoData(
            drive,
            cdb,
            DataDirection.In,
            request.TimeoutSeconds,
            out result);
    }
}
