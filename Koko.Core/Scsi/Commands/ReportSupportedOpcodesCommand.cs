namespace Koko.Core.Scsi.Commands;

public readonly record struct ReportSupportedOpcodesCommand(
    byte ReportingOptions,
    bool ReturnCommandTimeoutsDescriptor,
    byte RequestedOpcode = 0,
    ushort RequestedServiceAction = 0,
    uint AllocationLength = 0,
    uint TimeoutSeconds = 600)
{
    public ReportSupportedOpcodesCommand() : this(0, false, 0, 0, 0, 600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        ReportSupportedOpcodesCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        if (request.AllocationLength > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(request.AllocationLength), "Allocation length exceeds supported buffer size.");

        Span<byte> cdb = stackalloc byte[12];
        cdb.Clear();

        cdb[0] = 0xA3;
        cdb[1] = 0x0C;
        cdb[2] = (byte)((request.ReturnCommandTimeoutsDescriptor ? 0x80 : 0x00) | (request.ReportingOptions & 0x07));
        cdb[3] = request.RequestedOpcode;
        ScsiCdbWriter.WriteUInt16BigEndian(cdb, 4, request.RequestedServiceAction);
        ScsiCdbWriter.WriteUInt32BigEndian(cdb, 6, request.AllocationLength);

        return ScsiCommandExecutor.TryExecuteRead(
            drive,
            cdb,
            checked((int)request.AllocationLength),
            request.TimeoutSeconds,
            out result,
            out data);
    }
}
