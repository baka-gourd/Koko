using Koko.Core.Scsi;

namespace Koko.Core.Scsi.Commands;

public enum LocateDestinationType : byte
{
    LogicalObjectIdentifier = 0b000,
    LogicalFileIdentifier = 0b001,
    EndOfData = 0b011
}

public readonly record struct LocateCommand(
    bool Use16Byte,
    bool Immediate,
    bool ChangePartition,
    byte Partition,
    uint BlockAddress,
    LocateDestinationType DestinationType,
    ulong LogicalIdentifier,
    uint TimeoutSeconds = 60)
{
    public static bool TryExecute(
        IScsiDrive drive,
        LocateCommand request,
        out ScsiCommandResult result)
    {
        Span<byte> cdb = stackalloc byte[request.Use16Byte ? 16 : 10];
        cdb.Clear();

        if (request.Use16Byte)
        {
            cdb[0] = 0x92;
            cdb[1] = (byte)(((byte)request.DestinationType & 0x07) << 3);
            if (request.ChangePartition)
                cdb[1] |= 0x02;
            if (request.Immediate)
                cdb[1] |= 0x01;

            cdb[3] = request.Partition;
            ScsiCdbWriter.WriteUInt64BigEndian(cdb, 4, request.LogicalIdentifier);
        }
        else
        {
            cdb[0] = 0x2B;
            if (request.ChangePartition)
                cdb[1] |= 0x02;
            if (request.Immediate)
                cdb[1] |= 0x01;

            ScsiCdbWriter.WriteUInt32BigEndian(cdb, 3, request.BlockAddress);
            cdb[8] = request.Partition;
        }

        return ScsiCommandExecutor.TryExecuteNoData(
            drive,
            cdb,
            DataDirection.Unspecified,
            request.TimeoutSeconds,
            out result);
    }
}
