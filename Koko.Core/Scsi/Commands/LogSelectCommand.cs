using Koko.Core.Scsi;

namespace Koko.Core.Scsi.Commands;

public readonly record struct LogSelectCommand(
    LogPageControl PageControl,
    bool ParameterCodeReset,
    ReadOnlyMemory<byte> ParameterData,
    bool SaveParameters = false,
    uint TimeoutSeconds = 10)
{
    public static bool TryExecute(
        IScsiDrive drive,
        LogSelectCommand request,
        out ScsiCommandResult result)
    {
        if (request.SaveParameters)
            throw new ArgumentOutOfRangeException(nameof(request.SaveParameters), "Save Parameters is not supported and must be false.");

        if (request.ParameterData.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(request.ParameterData), "Parameter data exceeds 16-bit length.");

        Span<byte> cdb = stackalloc byte[10];
        cdb.Clear();

        cdb[0] = 0x4C;
        if (request.ParameterCodeReset)
            cdb[1] |= 0x02;
        if (request.SaveParameters)
            cdb[1] |= 0x01;

        cdb[2] = (byte)((byte)request.PageControl << 6);
        ScsiCdbWriter.WriteUInt16BigEndian(cdb, 7, (ushort)request.ParameterData.Length);

        return ScsiCommandExecutor.TryExecuteWrite(
            drive,
            cdb,
            request.ParameterData,
            request.TimeoutSeconds,
            out result);
    }
}
