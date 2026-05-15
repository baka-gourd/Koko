namespace Koko.Core.Scsi;

public interface IScsiDrive
{
    public int BlockSizeLimit { get; set; }

    public ScsiTransportError? LastTransportError { get; }

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

    public bool ScsiCommand(ReadOnlySpan<byte> commandBlock,
        DataDirection dataDirection,
        uint timeout,
        out byte scsiStatus,
        out uint bytesReturned,
        Span<byte> senseBuffer);
}
