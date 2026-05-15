namespace Koko.Core.Scsi.Commands;

public readonly record struct SecurityProtocolOutCommand(
    byte SecurityProtocol,
    ushort SecurityProtocolSpecific,
    ReadOnlyMemory<byte> ParameterData,
    bool IncrementBy512 = false,
    uint TimeoutSeconds = 10)
{
    public static bool TryExecute(
        IScsiDrive drive,
        SecurityProtocolOutCommand request,
        out ScsiCommandResult result)
    {
        if (request.ParameterData.Length > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(request.ParameterData), "Parameter data exceeds supported buffer size.");

        Span<byte> cdb = stackalloc byte[12];
        cdb.Clear();

        cdb[0] = 0xB5;
        cdb[1] = request.SecurityProtocol;
        ScsiCdbWriter.WriteUInt16BigEndian(cdb, 2, request.SecurityProtocolSpecific);
        if (request.IncrementBy512)
            cdb[4] |= 0x01;

        ScsiCdbWriter.WriteUInt32BigEndian(cdb, 6, (uint)request.ParameterData.Length);

        return ScsiCommandExecutor.TryExecuteWrite(
            drive,
            cdb,
            request.ParameterData,
            request.TimeoutSeconds,
            out result);
    }
}
