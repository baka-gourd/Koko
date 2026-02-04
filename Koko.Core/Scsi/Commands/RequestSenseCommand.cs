using Koko.Core.Scsi;

namespace Koko.Core.Scsi.Commands;

public readonly record struct RequestSenseCommand(
    bool DescriptorFormat,
    byte AllocationLength = 0x12,
    uint TimeoutSeconds = 10)
{
    public static bool TryExecute(
        IScsiDrive drive,
        RequestSenseCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x03;
        if (request.DescriptorFormat)
            cdb[1] |= 0x01;

        cdb[4] = request.AllocationLength;

        return ScsiCommandExecutor.TryExecuteRead(
            drive,
            cdb,
            request.AllocationLength,
            request.TimeoutSeconds,
            out result,
            out data);
    }
}
