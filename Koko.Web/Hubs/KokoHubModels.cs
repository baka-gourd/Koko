namespace Koko.Web.Hubs;

public sealed record KokoServerInfoDto(
    string AppName,
    string Version,
    string Environment,
    DateTimeOffset StartedAtUtc);

public sealed record KokoPingRequest(DateTimeOffset? ClientTimestampUtc);

public sealed record KokoPingResponse(
    DateTimeOffset? ClientTimestampUtc,
    DateTimeOffset ServerTimestampUtc,
    string? ConnectionId);

public sealed record KokoRealtimeEventDto(
    string Id,
    string Type,
    string Severity,
    string Message,
    DateTimeOffset TimestampUtc,
    string? OperationId = null,
    double? Progress = null)
{
    public static KokoRealtimeEventDto Create(string type, string severity, string message, string? operationId = null, double? progress = null)
        => new(Guid.NewGuid().ToString("N"), type, severity, message, DateTimeOffset.UtcNow, operationId, progress);

    public static KokoRealtimeEventDto Info(string type, string message, string? operationId = null, double? progress = null)
        => Create(type, "Info", message, operationId, progress);

    public static KokoRealtimeEventDto Success(string type, string message, string? operationId = null, double? progress = null)
        => Create(type, "Success", message, operationId, progress);

    public static KokoRealtimeEventDto Warning(string type, string message, string? operationId = null, double? progress = null)
        => Create(type, "Warning", message, operationId, progress);

    public static KokoRealtimeEventDto Error(string type, string message, string? operationId = null, double? progress = null)
        => Create(type, "Error", message, operationId, progress);
}
