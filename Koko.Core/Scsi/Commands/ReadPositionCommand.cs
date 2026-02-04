using Koko.Core.Scsi;

namespace Koko.Core.Scsi.Commands;

public enum ReadPositionServiceAction : byte
{
    ShortForm = 0x00,
    LongForm = 0x06,
    ExtendedForm = 0x08
}

public readonly record struct ReadPositionCommand(
    ReadPositionServiceAction ServiceAction,
    ushort AllocationLength = 0,
    uint TimeoutSeconds = 10)
{
    public static bool TryExecute(
        IScsiDrive drive,
        ReadPositionCommand request,
        out ScsiCommandResult result,
        out ReadPositionResponse response)
    {
        var allocationLengthField = request.ServiceAction switch
        {
            ReadPositionServiceAction.ShortForm => (ushort)0,
            ReadPositionServiceAction.LongForm => (ushort)0,
            _ => request.AllocationLength == 0 ? (ushort)32 : request.AllocationLength
        };

        var dataLength = request.ServiceAction switch
        {
            ReadPositionServiceAction.ShortForm => 20,
            ReadPositionServiceAction.LongForm => 32,
            _ => allocationLengthField
        };

        Span<byte> cdb = stackalloc byte[10];
        cdb.Clear();

        cdb[0] = 0x34;
        cdb[1] = (byte)((byte)request.ServiceAction & 0x1F);
        ScsiCdbWriter.WriteUInt16BigEndian(cdb, 7, allocationLengthField);

        var buffer = dataLength == 0 ? Array.Empty<byte>() : new byte[dataLength];

        var ok = ScsiCommandExecutor.TryExecuteRead(
            drive,
            cdb,
            buffer,
            request.TimeoutSeconds,
            out result);

        response = ReadPositionResponse.Parse(request.ServiceAction, buffer);
        return ok;
    }
}

public readonly record struct ReadPositionResponse(
    ReadPositionServiceAction ServiceAction,
    ReadOnlyMemory<byte> Raw,
    ReadPositionShortForm? ShortForm,
    ReadPositionLongForm? LongForm,
    ReadPositionExtendedForm? ExtendedForm)
{
    public static ReadPositionResponse Parse(ReadPositionServiceAction serviceAction, ReadOnlyMemory<byte> data)
    {
        return serviceAction switch
        {
            ReadPositionServiceAction.ShortForm => new ReadPositionResponse(
                serviceAction,
                data,
                ReadPositionShortForm.TryParse(data.Span),
                null,
                null),
            ReadPositionServiceAction.LongForm => new ReadPositionResponse(
                serviceAction,
                data,
                null,
                ReadPositionLongForm.TryParse(data.Span),
                null),
            _ => new ReadPositionResponse(
                serviceAction,
                data,
                null,
                null,
                ReadPositionExtendedForm.TryParse(data.Span))
        };
    }
}

public readonly record struct ReadPositionShortForm(
    bool BeginningOfPartition,
    bool EndOfPartition,
    bool BlockLocationUnknown,
    bool ByteCountUnknown,
    bool LogicalObjectLocationValid,
    byte PartitionNumber,
    uint FirstBlockLocation,
    uint LastBlockLocation,
    uint BlocksInBuffer,
    uint BytesInBuffer)
{
    public static ReadPositionShortForm? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20) return null;

        var flags = data[0];
        var bop = (flags & 0x80) != 0;
        var eop = (flags & 0x40) != 0;
        var locu = (flags & 0x20) != 0;
        var bycu = (flags & 0x10) != 0;
        var lolu = (flags & 0x04) != 0;

        var partition = data[1];
        var firstBlock = ScsiCdbWriter.ReadUInt32BigEndian(data, 4);
        var lastBlock = ScsiCdbWriter.ReadUInt32BigEndian(data, 8);
        var blocksInBuffer = data.Length >= 16 ? ScsiCdbWriter.ReadUInt24BigEndian(data, 13) : 0u;
        var bytesInBuffer = data.Length >= 20 ? ScsiCdbWriter.ReadUInt32BigEndian(data, 16) : 0u;

        return new ReadPositionShortForm(
            bop,
            eop,
            locu,
            bycu,
            !lolu,
            partition,
            firstBlock,
            lastBlock,
            blocksInBuffer,
            bytesInBuffer);
    }
}

public readonly record struct ReadPositionLongForm(
    bool BeginningOfPartition,
    bool EndOfPartition,
    bool MarkPositionUnknown,
    bool LogicalObjectNumberValid,
    uint PartitionNumber,
    ulong BlockNumber,
    ulong FileNumber,
    ulong SetNumber)
{
    public static ReadPositionLongForm? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32) return null;

        var flags = data[0];
        var bop = (flags & 0x80) != 0;
        var eop = (flags & 0x40) != 0;
        var mpu = (flags & 0x08) != 0;
        var lonu = (flags & 0x04) != 0;

        var partition = ScsiCdbWriter.ReadUInt32BigEndian(data, 4);
        var blockNumber = ScsiCdbWriter.ReadUInt64BigEndian(data, 8);
        var fileNumber = ScsiCdbWriter.ReadUInt64BigEndian(data, 16);
        var setNumber = ScsiCdbWriter.ReadUInt64BigEndian(data, 24);

        return new ReadPositionLongForm(
            bop,
            eop,
            mpu,
            !lonu,
            partition,
            blockNumber,
            fileNumber,
            setNumber);
    }
}

public readonly record struct ReadPositionExtendedForm(
    bool BeginningOfPartition,
    bool EndOfPartition,
    bool BlockLocationUnknown,
    bool ByteCountUnknown,
    bool LogicalObjectLocationValid,
    byte PartitionNumber,
    ushort AdditionalLength,
    uint BlocksInBuffer,
    ulong FirstBlockLocation,
    ulong LastBlockLocation,
    ulong BytesInBuffer)
{
    public static ReadPositionExtendedForm? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32) return null;

        var flags = data[0];
        var bop = (flags & 0x80) != 0;
        var eop = (flags & 0x40) != 0;
        var locu = (flags & 0x20) != 0;
        var bycu = (flags & 0x10) != 0;
        var lolu = (flags & 0x04) != 0;

        var partition = data[1];
        var additionalLength = ScsiCdbWriter.ReadUInt16BigEndian(data, 2);
        var blocksInBuffer = ScsiCdbWriter.ReadUInt24BigEndian(data, 5);
        var firstBlock = ScsiCdbWriter.ReadUInt64BigEndian(data, 8);
        var lastBlock = ScsiCdbWriter.ReadUInt64BigEndian(data, 16);
        var bytesInBuffer = ScsiCdbWriter.ReadUInt64BigEndian(data, 24);

        return new ReadPositionExtendedForm(
            bop,
            eop,
            locu,
            bycu,
            !lolu,
            partition,
            additionalLength,
            blocksInBuffer,
            firstBlock,
            lastBlock,
            bytesInBuffer);
    }
}
