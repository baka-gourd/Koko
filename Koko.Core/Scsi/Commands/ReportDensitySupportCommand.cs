namespace Koko.Core.Scsi.Commands;

public readonly record struct ReportDensitySupportCommand(
    bool ReportMediumTypeDescriptors,
    bool ReportCurrentMedia,
    ushort AllocationLength,
    uint TimeoutSeconds = 600)
{
    public ReportDensitySupportCommand() : this(false, false, 0, 600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        ReportDensitySupportCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        Span<byte> cdb = stackalloc byte[10];
        cdb.Clear();

        cdb[0] = 0x44;
        if (request.ReportMediumTypeDescriptors)
            cdb[1] |= 0x02;
        if (request.ReportCurrentMedia)
            cdb[1] |= 0x01;

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
