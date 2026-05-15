namespace Koko.Core.Scsi.Commands;

public readonly record struct ReadCommand(
    bool SuppressIncorrectLengthIndicator,
    bool Fixed,
    uint TransferLength,
    int? BlockSizeBytes = null,
    uint TimeoutSeconds = 60)
{
    public static bool TryExecute(
        IScsiDrive drive,
        ReadCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        if (request.TransferLength > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(request.TransferLength), "Transfer length exceeds 24-bit field.");

        var allocationLength = ComputeAllocationLength(request);

        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x08;
        if (request.SuppressIncorrectLengthIndicator)
            cdb[1] |= 0x02;
        if (request.Fixed)
            cdb[1] |= 0x01;

        ScsiCdbWriter.WriteUInt24BigEndian(cdb, 2, request.TransferLength);

        return ScsiCommandExecutor.TryExecuteRead(
            drive,
            cdb,
            allocationLength,
            request.TimeoutSeconds,
            out result,
            out data);
    }

    private static int ComputeAllocationLength(ReadCommand request)
    {
        if (request.TransferLength == 0)
            return 0;

        if (!request.Fixed)
            return (int)request.TransferLength;

        if (request.BlockSizeBytes is null || request.BlockSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.BlockSizeBytes), "Block size must be specified for fixed reads.");

        var length = (long)request.BlockSizeBytes.Value * request.TransferLength;
        if (length > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(request.TransferLength), "Allocation length exceeds supported buffer size.");

        return (int)length;
    }
}
