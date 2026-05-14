using System.Buffers.Binary;

using Koko.Core.Scsi.Commands;

namespace Koko.Core.Ltfs;

public sealed record LtfsCapacityPolicyOptions(
    bool Enabled = false,
    long SafetyReserveBytes = 64L * 1024 * 1024,
    double CompressionRatioEstimate = 1.0,
    LogPageCode? LogPage = null)
{
    public LogPageCode EffectiveLogPage => LogPage ?? LogPageCode.TapeCapacity;
}

public sealed record LtfsCapacitySample(
    long? RemainingBytes,
    long SafetyReserveBytes,
    DateTimeOffset SampledAt,
    IReadOnlyList<string> Warnings)
{
    public bool HasReserveFor(long logicalBytes, double compressionRatioEstimate)
    {
        if (RemainingBytes is null)
            return true;

        var ratio = compressionRatioEstimate <= 0 ? 1 : compressionRatioEstimate;
        var estimatedPhysicalBytes = checked((long)Math.Ceiling(logicalBytes / ratio));
        return RemainingBytes.Value - SafetyReserveBytes >= estimatedPhysicalBytes;
    }
}

public sealed class LtfsCapacityMonitor
{
    private readonly ILtfsWriterDevice device;
    private readonly LtfsCapacityPolicyOptions options;

    public LtfsCapacityMonitor(ILtfsWriterDevice device, LtfsCapacityPolicyOptions options)
    {
        this.device = device ?? throw new ArgumentNullException(nameof(device));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<LtfsCapacitySample> SampleAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
            return new LtfsCapacitySample(null, options.SafetyReserveBytes, DateTimeOffset.UtcNow, Array.Empty<string>());

        var warnings = new List<string>();
        try
        {
            var response = await device.ReadLogSenseAsync(options.EffectiveLogPage, cancellationToken).ConfigureAwait(false);
            var remaining = ParseRemainingBytes(response, warnings);
            return new LtfsCapacitySample(remaining, options.SafetyReserveBytes, DateTimeOffset.UtcNow, warnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"Capacity sample failed: {ex.Message}");
            return new LtfsCapacitySample(null, options.SafetyReserveBytes, DateTimeOffset.UtcNow, warnings);
        }
    }

    public static long? ParseRemainingBytes(LogSenseResponse response, List<string>? warnings = null)
    {
        long? best = null;
        foreach (var parameter in response.Parameters)
        {
            var value = ReadUnsigned(parameter.Value.Span);
            if (value is null)
                continue;

            if (best is null || value.Value < best.Value)
                best = value.Value;
        }

        if (best is null && response.Page.Payload.Length > 0)
            warnings?.Add("Tape capacity log page did not contain parseable numeric parameters.");

        return best;
    }

    private static long? ReadUnsigned(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0 || value.Length > 8)
            return null;

        Span<byte> padded = stackalloc byte[8];
        value.CopyTo(padded[(8 - value.Length)..]);
        var raw = BinaryPrimitives.ReadUInt64BigEndian(padded);
        return raw > long.MaxValue ? long.MaxValue : (long)raw;
    }
}
