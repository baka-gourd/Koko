using Koko.Core.Scsi;

namespace Koko.Core.Scsi.Commands;

public readonly record struct ReportLunsCommand(
    byte SelectReport = 0,
    uint AllocationLength = 0,
    uint TimeoutSeconds = 10)
{
    public static bool TryExecute(
        IScsiDrive drive,
        ReportLunsCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        if (request.AllocationLength > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(request.AllocationLength), "Allocation length exceeds supported buffer size.");

        Span<byte> cdb = stackalloc byte[12];
        cdb.Clear();

        cdb[0] = 0xA0;
        cdb[2] = request.SelectReport;
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
