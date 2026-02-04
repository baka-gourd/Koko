using Koko.Core.Scsi;

namespace Koko.Core.Scsi.Commands;

public readonly record struct ReadBufferCommand(
    byte Mode,
    byte BufferId = 0,
    uint BufferOffset = 0,
    uint AllocationLength = 0,
    uint TimeoutSeconds = 10)
{
    private const byte DescriptorMode = 0x03;

    public static bool TryExecute(
        IScsiDrive drive,
        ReadBufferCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        if (request.BufferOffset > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(request.BufferOffset), "Buffer offset exceeds 24-bit field.");

        if (request.AllocationLength > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(request.AllocationLength), "Allocation length exceeds 24-bit field.");

        Span<byte> cdb = stackalloc byte[10];
        cdb.Clear();

        cdb[0] = 0x3C;
        cdb[1] = (byte)(request.Mode & 0x1F);
        cdb[2] = request.BufferId;

        ScsiCdbWriter.WriteUInt24BigEndian(cdb, 3, request.BufferOffset);
        ScsiCdbWriter.WriteUInt24BigEndian(cdb, 6, request.AllocationLength);

        return ScsiCommandExecutor.TryExecuteRead(
            drive,
            cdb,
            checked((int)request.AllocationLength),
            request.TimeoutSeconds,
            out result,
            out data);
    }

    public static bool TryExecuteWithLengthProbe(
        IScsiDrive drive,
        ReadBufferCommand request,
        out ScsiCommandResult result,
        out byte[] data)
    {
        var probeRequest = request with
        {
            Mode = DescriptorMode,
            BufferOffset = 0,
            AllocationLength = 4
        };

        var okProbe = TryExecute(drive, probeRequest, out var probeResult, out var probeData);
        if (!okProbe || probeData.Length < 4)
        {
            result = probeResult;
            data = probeData;
            return okProbe;
        }

        var bufferLength = ((uint)probeData[1] << 16) | ((uint)probeData[2] << 8) | probeData[3];
        if (bufferLength == 0)
        {
            result = probeResult;
            data = Array.Empty<byte>();
            return okProbe;
        }

        var readRequest = request with { AllocationLength = bufferLength };
        var okRead = TryExecute(drive, readRequest, out var readResult, out var readData);

        result = readResult;
        data = readData;
        return okRead;
    }
}
