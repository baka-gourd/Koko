namespace Koko.Core.Scsi.Commands;

public readonly record struct WriteBufferCommand(
    byte Mode,
    byte BufferId = 0,
    uint BufferOffset = 0,
    ReadOnlyMemory<byte> Data = default,
    uint TimeoutSeconds = 600)
{
    public WriteBufferCommand() : this(0, 0, 0, default, 600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        WriteBufferCommand request,
        out ScsiCommandResult result)
    {
        if (request.BufferOffset > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(request.BufferOffset), "Buffer offset exceeds 24-bit field.");

        if (request.Data.Length > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(request.Data), "Parameter list length exceeds 24-bit field.");

        Span<byte> cdb = stackalloc byte[10];
        cdb.Clear();

        cdb[0] = 0x3B;
        cdb[1] = (byte)(request.Mode & 0x1F);
        cdb[2] = request.BufferId;

        ScsiCdbWriter.WriteUInt24BigEndian(cdb, 3, request.BufferOffset);
        ScsiCdbWriter.WriteUInt24BigEndian(cdb, 6, (uint)request.Data.Length);

        return ScsiCommandExecutor.TryExecuteWrite(
            drive,
            cdb,
            request.Data,
            request.TimeoutSeconds,
            out result);
    }
}
