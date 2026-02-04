using Koko.Core.Scsi;

namespace Koko.Core.Scsi.Commands;

public enum SpaceCode : byte
{
    Blocks = 0b000,
    Filemarks = 0b001,
    EndOfData = 0b011
}

public readonly record struct SpaceCommand(
    bool Use16Byte,
    SpaceCode Code,
    long Count,
    uint TimeoutSeconds = 60)
{
    public static bool TryExecute(
        IScsiDrive drive,
        SpaceCommand request,
        out ScsiCommandResult result)
    {
        if (request.Code != SpaceCode.Blocks && request.Code != SpaceCode.Filemarks && request.Code != SpaceCode.EndOfData)
            throw new ArgumentOutOfRangeException(nameof(request.Code), "Invalid SPACE code.");

        if (!request.Use16Byte)
        {
            if (request.Count < -0x800000 || request.Count > 0x7FFFFF)
                throw new ArgumentOutOfRangeException(nameof(request.Count), "Count exceeds 24-bit signed field.");

            Span<byte> cdb = stackalloc byte[6];
            cdb.Clear();

            cdb[0] = 0x11;
            cdb[1] = (byte)((byte)request.Code & 0x07);

            var countField = (uint)request.Count & 0xFFFFFF;
            ScsiCdbWriter.WriteUInt24BigEndian(cdb, 2, countField);

            return ScsiCommandExecutor.TryExecuteNoData(
                drive,
                cdb,
                DataDirection.Unspecified,
                request.TimeoutSeconds,
                out result);
        }

        Span<byte> cdb16 = stackalloc byte[16];
        cdb16.Clear();

        cdb16[0] = 0x91;
        cdb16[1] = (byte)((byte)request.Code & 0x07);

        ScsiCdbWriter.WriteUInt64BigEndian(cdb16, 4, unchecked((ulong)request.Count));

        return ScsiCommandExecutor.TryExecuteNoData(
            drive,
            cdb16,
            DataDirection.Unspecified,
            request.TimeoutSeconds,
            out result);
    }
}
