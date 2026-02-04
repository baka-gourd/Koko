using System.Runtime.InteropServices;

using Koko.Core.Scsi;

namespace Koko.Core.Scsi.Commands;

internal static class ScsiCommandExecutor
{
    public static bool TryExecuteRead(
        IScsiDrive drive,
        ReadOnlySpan<byte> cdb,
        int allocationLength,
        uint timeoutSeconds,
        out ScsiCommandResult result,
        out byte[] data)
    {
        if (drive is null) throw new ArgumentNullException(nameof(drive));
        if (allocationLength < 0) throw new ArgumentOutOfRangeException(nameof(allocationLength));

        data = allocationLength == 0 ? Array.Empty<byte>() : new byte[allocationLength];
        var sense = new byte[IOControl.DefaultSenseLength];

        var ok = drive.ScsiRead(
            commandBlock: cdb,
            returnBuffer: data,
            timeoutSeconds: timeoutSeconds,
            out var scsiStatus,
            out var bytesReturned,
            senseBuffer: sense);

        if (bytesReturned < (uint)data.Length)
            Array.Resize(ref data, (int)bytesReturned);

        result = ScsiCommandResult.From(ok, scsiStatus, bytesReturned, sense);
        return ok;
    }

    public static bool TryExecuteRead(
        IScsiDrive drive,
        ReadOnlySpan<byte> cdb,
        Span<byte> dataBuffer,
        uint timeoutSeconds,
        out ScsiCommandResult result)
    {
        if (drive is null) throw new ArgumentNullException(nameof(drive));

        var sense = new byte[IOControl.DefaultSenseLength];
        var ok = drive.ScsiRead(
            commandBlock: cdb,
            returnBuffer: dataBuffer,
            timeoutSeconds: timeoutSeconds,
            out var scsiStatus,
            out var bytesReturned,
            senseBuffer: sense);

        result = ScsiCommandResult.From(ok, scsiStatus, bytesReturned, sense);
        return ok;
    }

    public static bool TryExecuteWrite(
        IScsiDrive drive,
        ReadOnlySpan<byte> cdb,
        ReadOnlyMemory<byte> dataOut,
        uint timeoutSeconds,
        out ScsiCommandResult result)
    {
        if (drive is null) throw new ArgumentNullException(nameof(drive));

        Span<byte> dataSpan = dataOut.IsEmpty
            ? Span<byte>.Empty
            : GetWritableSpan(dataOut);

        var sense = new byte[IOControl.DefaultSenseLength];
        var ok = drive.ScsiWrite(
            commandBlock: cdb,
            dataBuffer: dataSpan,
            timeoutSeconds: timeoutSeconds,
            out var scsiStatus,
            out var bytesReturned,
            senseBuffer: sense);

        result = ScsiCommandResult.From(ok, scsiStatus, bytesReturned, sense);
        return ok;
    }

    public static bool TryExecuteNoData(
        IScsiDrive drive,
        ReadOnlySpan<byte> cdb,
        DataDirection direction,
        uint timeoutSeconds,
        out ScsiCommandResult result)
    {
        if (drive is null) throw new ArgumentNullException(nameof(drive));

        var sense = new byte[IOControl.DefaultSenseLength];
        var ok = drive.ScsiCommand(
            commandBlock: cdb,
            dataDirection: direction,
            timeout: timeoutSeconds,
            out var scsiStatus,
            out var bytesReturned,
            senseBuffer: sense);

        result = ScsiCommandResult.From(ok, scsiStatus, bytesReturned, sense);
        return ok;
    }

    private static Span<byte> GetWritableSpan(ReadOnlyMemory<byte> data)
    {
        if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment) && segment.Array is not null)
            return segment.Array.AsSpan(segment.Offset, segment.Count);

        return data.ToArray().AsSpan();
    }
}
