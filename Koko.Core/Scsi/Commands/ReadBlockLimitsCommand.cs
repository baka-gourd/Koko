namespace Koko.Core.Scsi.Commands;

public readonly record struct ReadBlockLimitsCommand(
    uint TimeoutSeconds = 600)
{
    public ReadBlockLimitsCommand() : this(600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        ReadBlockLimitsCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x05;

        return ScsiCommandExecutor.TryExecuteRead(
            drive,
            cdb,
            6,
            request.TimeoutSeconds,
            out result,
            out data);
    }
}
