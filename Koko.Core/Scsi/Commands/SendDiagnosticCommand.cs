using Koko.Core.Scsi;

namespace Koko.Core.Scsi.Commands;

public readonly record struct SendDiagnosticCommand(
    bool SelfTest,
    bool UnitOffline,
    ReadOnlyMemory<byte> ParameterData,
    uint TimeoutSeconds = 60)
{
    public static bool TryExecute(
        IScsiDrive drive,
        SendDiagnosticCommand request,
        out ScsiCommandResult result)
    {
        if (request.ParameterData.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(request.ParameterData), "Parameter data exceeds 16-bit length.");

        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x1D;
        cdb[1] |= 0x10;
        if (request.SelfTest)
            cdb[1] |= 0x04;
        if (request.UnitOffline)
            cdb[1] |= 0x01;

        ScsiCdbWriter.WriteUInt16BigEndian(cdb, 3, (ushort)request.ParameterData.Length);

        return ScsiCommandExecutor.TryExecuteWrite(
            drive,
            cdb,
            request.ParameterData,
            request.TimeoutSeconds,
            out result);
    }
}
