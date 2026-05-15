namespace Koko.Core.Scsi.Commands;

public readonly record struct PersistentReserveOutCommand(
    byte ServiceAction,
    byte Scope,
    byte Type,
    ReadOnlyMemory<byte> ParameterData,
    uint TimeoutSeconds = 10)
{
    public static bool TryExecute(
        IScsiDrive drive,
        PersistentReserveOutCommand request,
        out ScsiCommandResult result)
    {
        if (request.ParameterData.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(request.ParameterData), "Parameter data exceeds 16-bit length.");

        Span<byte> cdb = stackalloc byte[10];
        cdb.Clear();

        cdb[0] = 0x5F;
        cdb[1] = (byte)(request.ServiceAction & 0x1F);
        cdb[2] = (byte)(((request.Scope & 0x0F) << 4) | (request.Type & 0x0F));
        ScsiCdbWriter.WriteUInt16BigEndian(cdb, 7, (ushort)request.ParameterData.Length);

        return ScsiCommandExecutor.TryExecuteWrite(
            drive,
            cdb,
            request.ParameterData,
            request.TimeoutSeconds,
            out result);
    }
}
