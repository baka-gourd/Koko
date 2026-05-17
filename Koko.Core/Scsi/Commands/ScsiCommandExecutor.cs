using System.Runtime.InteropServices;

using Serilog;

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

        var retryCount = 0;
        while (true)
        {
            data = allocationLength == 0 ? Array.Empty<byte>() : new byte[allocationLength];
            var sense = new byte[IOControl.DefaultSenseLength];

            var ok = drive.ScsiRead(
                commandBlock: cdb,
                returnBuffer: data,
                timeoutSeconds: timeoutSeconds,
                out var scsiStatus,
                out var bytesReturned,
                senseBuffer: sense);

            result = ScsiCommandResult.From(ok, scsiStatus, bytesReturned, sense, drive.LastTransportError);
            var dataLength = GetReadDataLength(data.Length, bytesReturned, result.SenseData);
            if (dataLength < data.Length)
                Array.Resize(ref data, dataLength);

            if (!ShouldRetry(cdb, result, retryCount))
                return ok;

            retryCount++;
        }
    }

    public static bool TryExecuteRead(
        IScsiDrive drive,
        ReadOnlySpan<byte> cdb,
        Span<byte> dataBuffer,
        uint timeoutSeconds,
        out ScsiCommandResult result)
    {
        if (drive is null) throw new ArgumentNullException(nameof(drive));

        var retryCount = 0;
        while (true)
        {
            var sense = new byte[IOControl.DefaultSenseLength];
            var ok = drive.ScsiRead(
                commandBlock: cdb,
                returnBuffer: dataBuffer,
                timeoutSeconds: timeoutSeconds,
                out var scsiStatus,
                out var bytesReturned,
                senseBuffer: sense);

            result = ScsiCommandResult.From(ok, scsiStatus, bytesReturned, sense, drive.LastTransportError);
            if (!ShouldRetry(cdb, result, retryCount))
                return ok;

            retryCount++;
        }
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

        var retryCount = 0;
        while (true)
        {
            var sense = new byte[IOControl.DefaultSenseLength];
            var ok = drive.ScsiWrite(
                commandBlock: cdb,
                dataBuffer: dataSpan,
                timeoutSeconds: timeoutSeconds,
                out var scsiStatus,
                out var bytesReturned,
                senseBuffer: sense);

            result = ScsiCommandResult.From(ok, scsiStatus, bytesReturned, sense, drive.LastTransportError);
            if (!ShouldRetry(cdb, result, retryCount))
                return ok;

            retryCount++;
        }
    }

    public static bool TryExecuteNoData(
        IScsiDrive drive,
        ReadOnlySpan<byte> cdb,
        DataDirection direction,
        uint timeoutSeconds,
        out ScsiCommandResult result)
    {
        if (drive is null) throw new ArgumentNullException(nameof(drive));

        var retryCount = 0;
        while (true)
        {
            var sense = new byte[IOControl.DefaultSenseLength];
            var ok = drive.ScsiCommand(
                commandBlock: cdb,
                dataDirection: direction,
                timeout: timeoutSeconds,
                out var scsiStatus,
                out var bytesReturned,
                senseBuffer: sense);

            result = ScsiCommandResult.From(ok, scsiStatus, bytesReturned, sense, drive.LastTransportError);
            if (!ShouldRetry(cdb, result, retryCount))
                return ok;

            retryCount++;
        }
    }

    private static Span<byte> GetWritableSpan(ReadOnlyMemory<byte> data)
    {
        if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment) && segment.Array is not null)
            return segment.Array.AsSpan(segment.Offset, segment.Count);

        return data.ToArray().AsSpan();
    }

    private static int GetReadDataLength(int allocationLength, uint bytesReturned, ReadOnlySpan<byte> sense)
    {
        if (TryGetShortIncorrectLengthDataLength(allocationLength, sense, out var shortDataLength))
            return shortDataLength;

        _ = bytesReturned;
        return allocationLength;
    }

    private static bool TryGetShortIncorrectLengthDataLength(int allocationLength, ReadOnlySpan<byte> sense, out int dataLength)
    {
        dataLength = 0;
        if (sense.Length < 7 || (sense[2] & 0x20) == 0)
            return false;

        var residual = (sense[3] << 24) | (sense[4] << 16) | (sense[5] << 8) | sense[6];
        if (residual < 0 || residual > allocationLength)
            return false;

        dataLength = allocationLength - residual;
        return true;
    }

    private static bool ShouldRetry(ReadOnlySpan<byte> cdb, ScsiCommandResult result, int retryCount)
    {
        if (!ScsiStartupUnitAttentionRetry.ShouldRetryPowerOnReset(result, retryCount))
            return false;

        Log.Information(
            "Suppressing startup UNIT ATTENTION power-on reset and retrying SCSI command. Scope={ScopeName}, Opcode=0x{Opcode:X2}, Attempt={Attempt}, MaxRetries={MaxRetries}",
            ScsiStartupUnitAttentionRetry.CurrentScopeName,
            cdb.Length == 0 ? (byte)0 : cdb[0],
            retryCount + 1,
            ScsiStartupUnitAttentionRetry.CurrentMaxRetries);
        return true;
    }
}
