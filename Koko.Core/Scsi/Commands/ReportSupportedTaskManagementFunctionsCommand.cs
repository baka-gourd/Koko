namespace Koko.Core.Scsi.Commands;

public readonly record struct ReportSupportedTaskManagementFunctionsCommand(
    uint AllocationLength,
    uint TimeoutSeconds = 10)
{
    public ReportSupportedTaskManagementFunctionsCommand() : this(0, 10)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        ReportSupportedTaskManagementFunctionsCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        if (request.AllocationLength > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(request.AllocationLength), "Allocation length exceeds supported buffer size.");

        Span<byte> cdb = stackalloc byte[12];
        cdb.Clear();

        cdb[0] = 0xA3;
        cdb[1] = 0x0D;
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
