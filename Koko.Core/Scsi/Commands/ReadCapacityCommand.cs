using Koko.Core.Scsi;

namespace Koko.Core.Scsi.Commands;

public readonly record struct ReadCapacityCommand(
    uint LogicalBlockAddress = 0,
    bool PartialMediumIndicator = false,
    uint TimeoutSeconds = 10)
{
    public static bool TryExecute(
        IScsiDrive drive,
        ReadCapacityCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        Span<byte> cdb = stackalloc byte[10];
        cdb.Clear();

        cdb[0] = 0x25;
        ScsiCdbWriter.WriteUInt32BigEndian(cdb, 2, request.LogicalBlockAddress);
        if (request.PartialMediumIndicator)
            cdb[8] |= 0x01;

        return ScsiCommandExecutor.TryExecuteRead(
            drive,
            cdb,
            8,
            request.TimeoutSeconds,
            out result,
            out data);
    }
}
