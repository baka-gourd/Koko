namespace Koko.Core.Scsi.Commands;

public readonly record struct ReadTocCommand(
    byte Format,
    byte TrackSessionNumber,
    ushort AllocationLength,
    bool Msf = false,
    bool RelativeAddress = false,
    uint TimeoutSeconds = 10)
{
    public static bool TryExecute(
        IScsiDrive drive,
        ReadTocCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        Span<byte> cdb = stackalloc byte[10];
        cdb.Clear();

        cdb[0] = 0x43;
        if (request.Msf)
            cdb[1] |= 0x02;
        if (request.RelativeAddress)
            cdb[1] |= 0x01;

        cdb[2] = (byte)(request.Format & 0x0F);
        cdb[6] = request.TrackSessionNumber;
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
