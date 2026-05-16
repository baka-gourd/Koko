namespace Koko.Core.Scsi.Commands;

public readonly record struct InquiryCommand(
    bool EnableVitalProductData,
    byte PageCode = 0,
    ushort AllocationLength = 0,
    uint TimeoutSeconds = 10)
{
    public InquiryCommand() : this(false, 0, 0, 10)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        InquiryCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        if (!request.EnableVitalProductData && request.PageCode != 0)
            throw new ArgumentOutOfRangeException(nameof(request.PageCode), "Page code must be zero when EVPD is false.");

        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x12;
        if (request.EnableVitalProductData)
            cdb[1] |= 0x01;

        cdb[2] = request.PageCode;
        ScsiCdbWriter.WriteUInt16BigEndian(cdb, 3, request.AllocationLength);

        return ScsiCommandExecutor.TryExecuteRead(
            drive,
            cdb,
            request.AllocationLength,
            request.TimeoutSeconds,
            out result,
            out data);
    }
}
