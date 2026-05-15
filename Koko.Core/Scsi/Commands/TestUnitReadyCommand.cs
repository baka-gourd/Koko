namespace Koko.Core.Scsi.Commands;

public readonly record struct TestUnitReadyCommand(
    uint TimeoutSeconds = 10)
{
    public static bool TryExecute(
        IScsiDrive drive,
        TestUnitReadyCommand request,
        out ScsiCommandResult result)
    {
        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x00;

        return ScsiCommandExecutor.TryExecuteNoData(
            drive,
            cdb,
            // Match legacy LTFSCopyGUI: TUR is sent through the read path with
            // a zero-length buffer. Some Windows tape drivers reject
            // PASS_THROUGH_DIRECT + zero transfer + UNSPECIFIED as ERROR_INVALID_PARAMETER.
            DataDirection.In,
            request.TimeoutSeconds,
            out result);
    }
}
