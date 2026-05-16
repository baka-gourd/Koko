namespace Koko.Core.Scsi.Commands;

public readonly record struct SeekCommand(
    uint LogicalBlockAddress,
    uint TimeoutSeconds = 10)
{
    public SeekCommand() : this(0, 10)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        SeekCommand request,
        out ScsiCommandResult result)
    {
        Span<byte> cdb = stackalloc byte[10];
        cdb.Clear();

        cdb[0] = 0x2B;
        ScsiCdbWriter.WriteUInt32BigEndian(cdb, 2, request.LogicalBlockAddress);

        return ScsiCommandExecutor.TryExecuteNoData(
            drive,
            cdb,
            DataDirection.In,
            request.TimeoutSeconds,
            out result);
    }
}
