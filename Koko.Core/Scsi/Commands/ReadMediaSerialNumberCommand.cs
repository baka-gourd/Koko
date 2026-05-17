namespace Koko.Core.Scsi.Commands;

public readonly record struct ReadMediaSerialNumberCommand(
    ushort AllocationLength,
    uint TimeoutSeconds = 600)
{
    public ReadMediaSerialNumberCommand() : this(0, 600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        ReadMediaSerialNumberCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        Span<byte> cdb = stackalloc byte[12];
        cdb.Clear();

        cdb[0] = 0xAB;
        cdb[1] = 0x01;

        ScsiCdbWriter.WriteUInt16BigEndian(cdb, 6, request.AllocationLength);

        return ScsiCommandExecutor.TryExecuteRead(
            drive,
            cdb,
            request.AllocationLength,
            request.TimeoutSeconds,
            out result,
            out data);
    }
}
