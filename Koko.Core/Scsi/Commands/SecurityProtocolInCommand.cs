namespace Koko.Core.Scsi.Commands;

public readonly record struct SecurityProtocolInCommand(
    byte SecurityProtocol,
    ushort SecurityProtocolSpecific,
    uint AllocationLength,
    bool IncrementBy512 = false,
    uint TimeoutSeconds = 10)
{
    public SecurityProtocolInCommand() : this(0, 0, 0, false, 10)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        SecurityProtocolInCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        if (request.AllocationLength > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(request.AllocationLength), "Allocation length exceeds supported buffer size.");

        Span<byte> cdb = stackalloc byte[12];
        cdb.Clear();

        cdb[0] = 0xA2;
        cdb[1] = request.SecurityProtocol;
        ScsiCdbWriter.WriteUInt16BigEndian(cdb, 2, request.SecurityProtocolSpecific);
        if (request.IncrementBy512)
            cdb[4] |= 0x01;

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
