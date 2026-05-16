namespace Koko.Core.Scsi.Commands;

public readonly record struct SetCapacityCommand(
    bool Immediate,
    ushort CapacityProportionValue,
    uint TimeoutSeconds = 60)
{
    public SetCapacityCommand() : this(false, 0, 60)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        SetCapacityCommand request,
        out ScsiCommandResult result)
    {
        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x0B;
        if (request.Immediate)
            cdb[1] |= 0x01;

        ScsiCdbWriter.WriteUInt16BigEndian(cdb, 3, request.CapacityProportionValue);

        return ScsiCommandExecutor.TryExecuteNoData(
            drive,
            cdb,
            DataDirection.In,
            request.TimeoutSeconds,
            out result);
    }
}
