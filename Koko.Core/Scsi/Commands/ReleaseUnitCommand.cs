namespace Koko.Core.Scsi.Commands;

public readonly record struct ReleaseUnitCommand(
    bool Use10Byte,
    bool ThirdParty = false,
    byte ThirdPartyDeviceId = 0,
    uint TimeoutSeconds = 600)
{
    public ReleaseUnitCommand() : this(false, false, 0, 600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        ReleaseUnitCommand request,
        out ScsiCommandResult result)
    {
        if (!request.Use10Byte)
        {
            if (request.ThirdParty || request.ThirdPartyDeviceId != 0)
                throw new ArgumentOutOfRangeException(nameof(request.ThirdParty), "Third-party release is not supported in 6-byte RELEASE UNIT.");

            Span<byte> cdb = stackalloc byte[6];
            cdb.Clear();

            cdb[0] = 0x17;

            return ScsiCommandExecutor.TryExecuteNoData(
                drive,
                cdb,
                DataDirection.In,
                request.TimeoutSeconds,
                out result);
        }

        Span<byte> cdb10 = stackalloc byte[10];
        cdb10.Clear();

        cdb10[0] = 0x57;
        if (request.ThirdParty)
            cdb10[1] |= 0x10;

        cdb10[3] = request.ThirdPartyDeviceId;

        return ScsiCommandExecutor.TryExecuteNoData(
            drive,
            cdb10,
            DataDirection.In,
            request.TimeoutSeconds,
            out result);
    }
}
