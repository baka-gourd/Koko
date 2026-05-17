namespace Koko.Core.Scsi.Commands;

public enum ModePageControl : byte
{
    CurrentValues = 0b00,
    ChangeableValues = 0b01,
    DefaultValues = 0b10,
    SavedValues = 0b11
}

public readonly record struct ModeSenseCommand(
    bool Use10Byte,
    bool DisableBlockDescriptors,
    ModePageControl PageControl,
    byte PageCode,
    byte SubPageCode,
    ushort AllocationLength,
    uint TimeoutSeconds = 600)
{
    public ModeSenseCommand() : this(false, false, default, 0, 0, 0, 600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        ModeSenseCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        Span<byte> cdb = stackalloc byte[request.Use10Byte ? 10 : 6];
        cdb.Clear();

        if (request.Use10Byte)
        {
            cdb[0] = 0x5A;
            if (request.DisableBlockDescriptors)
                cdb[1] |= 0x08;

            cdb[2] = (byte)(((byte)request.PageControl << 6) | (request.PageCode & 0x3F));
            cdb[3] = request.SubPageCode;
            ScsiCdbWriter.WriteUInt16BigEndian(cdb, 7, request.AllocationLength);
        }
        else
        {
            if (request.AllocationLength > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(request.AllocationLength), "Allocation length exceeds 6-byte CDB limit.");

            cdb[0] = 0x1A;
            if (request.DisableBlockDescriptors)
                cdb[1] |= 0x08;

            cdb[2] = (byte)(((byte)request.PageControl << 6) | (request.PageCode & 0x3F));
            cdb[3] = request.SubPageCode;
            cdb[4] = (byte)request.AllocationLength;
        }

        return ScsiCommandExecutor.TryExecuteRead(
            drive,
            cdb,
            request.AllocationLength,
            request.TimeoutSeconds,
            out result,
            out data);
    }
}
