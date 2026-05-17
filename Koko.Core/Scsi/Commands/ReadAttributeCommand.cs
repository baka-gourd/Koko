namespace Koko.Core.Scsi.Commands;

public readonly record struct ReadAttributeCommand(
    byte ServiceAction,
    byte VolumeNumber = 0,
    byte PartitionNumber = 0,
    ushort FirstAttributeId = 0,
    ushort AllocationLength = 0,
    bool Cache = false,
    uint TimeoutSeconds = 600)
{
    public ReadAttributeCommand() : this(0, 0, 0, 0, 0, false, 600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        ReadAttributeCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        Span<byte> cdb = stackalloc byte[16];
        cdb.Clear();

        cdb[0] = 0x8C;
        cdb[1] = (byte)(request.ServiceAction & 0x1F);
        cdb[5] = request.VolumeNumber;
        cdb[7] = request.PartitionNumber;

        ScsiCdbWriter.WriteUInt16BigEndian(cdb, 8, request.FirstAttributeId);
        ScsiCdbWriter.WriteUInt16BigEndian(cdb, 10, request.AllocationLength);

        if (request.Cache)
            cdb[14] |= 0x01;

        return ScsiCommandExecutor.TryExecuteRead(
            drive,
            cdb,
            request.AllocationLength,
            request.TimeoutSeconds,
            out result,
            out data);
    }
}
