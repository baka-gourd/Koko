namespace Koko.Core.Scsi.Commands;

public readonly record struct ReceiveDiagnosticResultsCommand(
    byte PageCode,
    bool PageCodeValid,
    ushort AllocationLength,
    uint TimeoutSeconds = 10)
{
    public static bool TryExecute(
        IScsiDrive drive,
        ReceiveDiagnosticResultsCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x1C;
        if (request.PageCodeValid)
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
