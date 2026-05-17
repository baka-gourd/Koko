namespace Koko.Core.Scsi.Commands;

public readonly record struct ReadCdRom6Command(
    uint LogicalBlockAddress,
    byte TransferLength,
    uint TimeoutSeconds = 600)
{
    public ReadCdRom6Command() : this(0, 0, 600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        ReadCdRom6Command request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        if (request.LogicalBlockAddress > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(request.LogicalBlockAddress), "Logical block address exceeds 24-bit field.");

        var allocationLength = checked(request.TransferLength * 2048);

        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x08;
        ScsiCdbWriter.WriteUInt24BigEndian(cdb, 1, request.LogicalBlockAddress);
        cdb[4] = request.TransferLength;

        return ScsiCommandExecutor.TryExecuteRead(
            drive,
            cdb,
            allocationLength,
            request.TimeoutSeconds,
            out result,
            out data);
    }
}
