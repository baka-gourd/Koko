namespace Koko.Core.Scsi.Commands;

public readonly record struct VerifyCommand(
    bool Fixed,
    uint VerificationLength,
    uint TimeoutSeconds = 600)
{
    public VerifyCommand() : this(false, 0, 600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        VerifyCommand request,
        out ScsiCommandResult result)
    {
        if (request.VerificationLength > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(request.VerificationLength), "Verification length exceeds 24-bit field.");

        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x13;
        if (request.Fixed)
            cdb[1] |= 0x01;

        ScsiCdbWriter.WriteUInt24BigEndian(cdb, 2, request.VerificationLength);

        return ScsiCommandExecutor.TryExecuteNoData(
            drive,
            cdb,
            DataDirection.In,
            request.TimeoutSeconds,
            out result);
    }
}
