namespace Koko.Core.Scsi.Commands;

public enum LogPageControl : byte
{
    CurrentThresholdValues = 0b00,
    CurrentCumulativeValues = 0b01,
    DefaultThresholdValues = 0b10,
    DefaultCumulativeValues = 0b11
}

public readonly record struct LogPageCode(byte Value)
{
    public static LogPageCode SupportedPages => new(0x00);
    public static LogPageCode WriteErrorCounters => new(0x02);
    public static LogPageCode ReadErrorCounters => new(0x03);
    public static LogPageCode SequentialAccessDevice => new(0x0C);
    public static LogPageCode Temperature => new(0x0D);
    public static LogPageCode DtdStatus => new(0x11);
    public static LogPageCode TapeAlertResponse => new(0x12);
    public static LogPageCode RequestedRecovery => new(0x13);
    public static LogPageCode DeviceStatistics => new(0x14);
    public static LogPageCode ServiceBuffersInformation => new(0x15);
    public static LogPageCode TapeDiagnostics => new(0x16);
    public static LogPageCode VolumeStatistics => new(0x17);
    public static LogPageCode ProtocolSpecificPort => new(0x18);
    public static LogPageCode DataCompression => new(0x1B);
    public static LogPageCode TapeAlert => new(0x2E);
    public static LogPageCode TapeUsage => new(0x30);
    public static LogPageCode TapeCapacity => new(0x31);
    public static LogPageCode DataCompressionHp => new(0x32);
    public static LogPageCode DeviceWellness => new(0x33);
    public static LogPageCode PerformanceData => new(0x34);
    public static LogPageCode DtDeviceError => new(0x35);
    public static LogPageCode DeviceStatus => new(0x3E);

    public override string ToString() => $"0x{Value:X2}";
}

public readonly record struct LogSenseCommand(
    LogPageCode PageCode,
    LogPageControl PageControl = LogPageControl.CurrentCumulativeValues,
    ushort ParameterPointer = 0,
    ushort AllocationLength = 0,
    bool SaveParameters = false,
    bool ParameterPointerControl = false,
    uint TimeoutSeconds = 600)
{
    public LogSenseCommand() : this(default, LogPageControl.CurrentCumulativeValues, 0, 0, false, false, 600)
    {
    }

    public static bool TryExecute(
        IScsiDrive drive,
        LogSenseCommand request,
        out ScsiCommandResult result,
        out LogSenseResponse response)
    {
        if (request.AllocationLength == 0)
        {
            var okHeader = TryReadPage(drive, request, 4, out var headerResult, out var headerData);
            if (!okHeader || headerData.Length < 4)
            {
                result = headerResult;
                response = LogSenseResponse.FromRaw(headerData);
                return okHeader;
            }

            var pageLength = ScsiCdbWriter.ReadUInt16BigEndian(headerData, 2);
            var totalLength = Math.Min(pageLength + 4, ushort.MaxValue);

            var ok = TryReadPage(drive, request, (ushort)totalLength, out var pageResult, out var pageData);
            result = pageResult;
            response = LogSenseResponse.FromRaw(pageData);
            return ok;
        }

        var okDirect = TryReadPage(drive, request, request.AllocationLength, out var directResult, out var directData);
        result = directResult;
        response = LogSenseResponse.FromRaw(directData);
        return okDirect;
    }

    private static bool TryReadPage(
        IScsiDrive drive,
        LogSenseCommand request,
        ushort allocationLength,
        out ScsiCommandResult result,
        out byte[] data)
    {
        Span<byte> cdb = stackalloc byte[10];
        cdb.Clear();

        cdb[0] = 0x4D;
        if (request.ParameterPointerControl)
            cdb[1] |= 0x02;
        if (request.SaveParameters)
            cdb[1] |= 0x01;

        cdb[2] = (byte)(((byte)request.PageControl << 6) | (request.PageCode.Value & 0x3F));
        ScsiCdbWriter.WriteUInt16BigEndian(cdb, 5, request.ParameterPointer);
        ScsiCdbWriter.WriteUInt16BigEndian(cdb, 7, allocationLength);

        return ScsiCommandExecutor.TryExecuteRead(
            drive,
            cdb,
            allocationLength,
            request.TimeoutSeconds,
            out result,
            out data);
    }
}

public readonly record struct LogSenseResponse(
    LogPage Page,
    IReadOnlyList<LogParameter> Parameters,
    IReadOnlyList<byte> SupportedPageCodes)
{
    public static LogSenseResponse FromRaw(ReadOnlyMemory<byte> data)
    {
        if (data.Length < 4)
            return new LogSenseResponse(new LogPage(0, 0, 0, data, data), Array.Empty<LogParameter>(), Array.Empty<byte>());

        var rawSpan = data.Span;
        var rawPageCode = rawSpan[0];
        var pageCode = (byte)(rawPageCode & 0x3F);
        var pageLength = ScsiCdbWriter.ReadUInt16BigEndian(rawSpan, 2);

        var payloadLength = Math.Min(pageLength, data.Length - 4);
        var payload = data.Slice(4, payloadLength);
        var page = new LogPage(rawPageCode, pageCode, pageLength, payload, data);

        if (pageCode == 0x00)
        {
            var supported = payload.ToArray();
            return new LogSenseResponse(page, Array.Empty<LogParameter>(), supported);
        }

        var parameters = ParseParameters(data, payloadLength);
        return new LogSenseResponse(page, parameters, Array.Empty<byte>());
    }

    private static LogParameter[] ParseParameters(ReadOnlyMemory<byte> rawData, int payloadLength)
    {
        var list = new List<LogParameter>();
        var span = rawData.Span;
        var offset = 4;
        var end = 4 + payloadLength;

        while (offset + 4 <= end)
        {
            var parameterCode = ScsiCdbWriter.ReadUInt16BigEndian(span, offset);
            var control = span[offset + 2];
            var length = span[offset + 3];
            var valueOffset = offset + 4;
            var next = valueOffset + length;
            if (next > end)
                break;

            var value = rawData.Slice(valueOffset, length);
            list.Add(new LogParameter(parameterCode, control, value));
            offset = next;
        }

        return list.ToArray();
    }
}

public readonly record struct LogPage(
    byte RawPageCode,
    byte PageCode,
    ushort PageLength,
    ReadOnlyMemory<byte> Payload,
    ReadOnlyMemory<byte> RawData);

public readonly record struct LogParameter(
    ushort ParameterCode,
    byte Control,
    ReadOnlyMemory<byte> Value);
