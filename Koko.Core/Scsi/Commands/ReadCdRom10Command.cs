namespace Koko.Core.Scsi.Commands;

public readonly record struct ReadCdRom10Command(
    uint LogicalBlockAddress,
    ushort TransferLength,
    uint TimeoutSeconds = 10)
{
    public static bool TryExecute(
        IScsiDrive drive,
        ReadCdRom10Command request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        var allocationLength = checked((int)request.TransferLength * 2048);

        Span<byte> cdb = stackalloc byte[10];
        cdb.Clear();

        cdb[0] = 0x28;
        ScsiCdbWriter.WriteUInt32BigEndian(cdb, 2, request.LogicalBlockAddress);
        ScsiCdbWriter.WriteUInt16BigEndian(cdb, 7, request.TransferLength);

        return ScsiCommandExecutor.TryExecuteRead(
            drive,
            cdb,
            allocationLength,
            request.TimeoutSeconds,
            out result,
            out data);
    }
}
