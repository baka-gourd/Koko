namespace Koko.Core.Scsi.Commands;

public readonly record struct WriteCommand(
    bool Fixed,
    uint TransferLength,
    int? BlockSizeBytes = null,
    uint TimeoutSeconds = 600)
{
    public WriteCommand() : this(false, 0, null, 600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        WriteCommand request,
        ReadOnlyMemory<byte> data,
        out ScsiCommandResult result)
    {
        var transferLength = ResolveTransferLength(request, data);
        if (transferLength > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(request.TransferLength), "Transfer length exceeds 24-bit field.");

        Span<byte> cdb = stackalloc byte[6];
        cdb.Clear();

        cdb[0] = 0x0A;
        if (request.Fixed)
            cdb[1] |= 0x01;

        ScsiCdbWriter.WriteUInt24BigEndian(cdb, 2, transferLength);

        return ScsiCommandExecutor.TryExecuteWrite(
            drive,
            cdb,
            data,
            request.TimeoutSeconds,
            out result);
    }

    private static uint ResolveTransferLength(WriteCommand request, ReadOnlyMemory<byte> data)
    {
        if (request.TransferLength != 0)
        {
            ValidateDataLength(request, data, request.TransferLength);
            return request.TransferLength;
        }

        if (data.IsEmpty)
            return 0;

        if (!request.Fixed)
            return (uint)data.Length;

        if (request.BlockSizeBytes is null || request.BlockSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.BlockSizeBytes), "Block size must be specified for fixed writes.");

        if (data.Length % request.BlockSizeBytes.Value != 0)
            throw new ArgumentException("Data length is not a multiple of the fixed block size.", nameof(data));

        return (uint)(data.Length / request.BlockSizeBytes.Value);
    }

    private static void ValidateDataLength(WriteCommand request, ReadOnlyMemory<byte> data, uint transferLength)
    {
        if (!request.Fixed)
        {
            if (transferLength == 0 && !data.IsEmpty)
                throw new ArgumentException("Transfer length is zero but data is not empty.", nameof(data));

            if (transferLength != 0 && transferLength != data.Length)
                throw new ArgumentException("Transfer length does not match data length.", nameof(data));

            return;
        }

        if (request.BlockSizeBytes is null || request.BlockSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.BlockSizeBytes), "Block size must be specified for fixed writes.");

        if (transferLength == 0 && !data.IsEmpty)
            throw new ArgumentException("Transfer length is zero but data is not empty.", nameof(data));

        if (data.IsEmpty)
            return;

        if (data.Length % request.BlockSizeBytes.Value != 0)
            throw new ArgumentException("Data length is not a multiple of the fixed block size.", nameof(data));

        var blocks = data.Length / request.BlockSizeBytes.Value;
        if (transferLength != blocks)
            throw new ArgumentException("Transfer length does not match fixed block count.", nameof(data));
    }
}
