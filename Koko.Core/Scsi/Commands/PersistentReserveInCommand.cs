namespace Koko.Core.Scsi.Commands;

public readonly record struct PersistentReserveInCommand(
    byte ServiceAction,
    ushort AllocationLength,
    uint TimeoutSeconds = 600)
{
    public PersistentReserveInCommand() : this(0, 0, 600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        PersistentReserveInCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        Span<byte> cdb = stackalloc byte[10];
        cdb.Clear();

        cdb[0] = 0x5E;
        cdb[1] = (byte)(request.ServiceAction & 0x1F);
        ScsiCdbWriter.WriteUInt16BigEndian(cdb, 7, request.AllocationLength);

        return ScsiCommandExecutor.TryExecuteRead(
            drive,
            cdb,
            request.AllocationLength,
            request.TimeoutSeconds,
            out result,
            out data);
    }
}
