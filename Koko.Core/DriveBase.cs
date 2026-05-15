using Koko.Core.Scsi;

namespace Koko.Core;

public abstract class DriveBase : IScsiDrive, IDisposable
{
    public virtual int BlockSizeLimit { get; set; }

    public virtual ScsiTransportError? LastTransportError { get; protected set; }

    public abstract bool ScsiRead(ReadOnlySpan<byte> commandBlock, Span<byte> returnBuffer, uint timeoutSeconds,
        out byte scsiStatus,
        out uint bytesReturned, Span<byte> senseBuffer);


    public abstract bool ScsiWrite(ReadOnlySpan<byte> commandBlock, Span<byte> dataBuffer, uint timeoutSeconds,
        out byte scsiStatus,
        out uint bytesReturned, Span<byte> senseBuffer);


    public abstract bool ScsiCommand(ReadOnlySpan<byte> commandBlock, DataDirection dataDirection, uint timeout,
        out byte scsiStatus,
        out uint bytesReturned, Span<byte> senseBuffer);


    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
    private int _disposed;
    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (disposing)
        {
            DisposeCore();
        }
    }

    protected virtual void DisposeCore()
    {
    }
}
