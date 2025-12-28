namespace Koko.Core.Scsi;

public interface IScsiDrive
{
    public int BlockSizeLimit { get; set; }

    public bool ScsiRead(
        ReadOnlySpan<byte> commandBlock,
        Span<byte> returnBuffer,
        uint timeoutSeconds,
        out byte scsiStatus,
        out uint bytesReturned,
        Span<byte> senseBuffer);
    public bool ScsiWrite(
        ReadOnlySpan<byte> commandBlock,
        Span<byte> dataBuffer,
        uint timeoutSeconds,
        out byte scsiStatus,
        out uint bytesReturned,
        Span<byte> senseBuffer);
}