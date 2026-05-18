using System.Reflection;
using Koko.Web.Services;
using Microsoft.AspNetCore.SignalR;

namespace Koko.Web.Hubs;

public sealed class KokoHub : Hub
{
    private static readonly DateTimeOffset StartedAtUtc = DateTimeOffset.UtcNow;

    private readonly IHostEnvironment environment;
    private readonly TapeMetadataIndexService tapeMetadata;
    private readonly TapeSchemaService tapeSchema;

    public KokoHub(IHostEnvironment environment, TapeMetadataIndexService tapeMetadata, TapeSchemaService tapeSchema)
    {
        this.environment = environment;
        this.tapeMetadata = tapeMetadata;
        this.tapeSchema = tapeSchema;
    }

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync(
            "ReceiveEvent",
            KokoRealtimeEventDto.Info("connection.opened", "Connected to Koko.Web.", Context.ConnectionId));

        await base.OnConnectedAsync();
    }

    public KokoServerInfoDto GetServerInfo()
    {
        var assembly = typeof(Program).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        return new KokoServerInfoDto(
            AppName: "Koko.Web",
            Version: version,
            Environment: environment.EnvironmentName,
            StartedAtUtc: StartedAtUtc);
    }

    public KokoPingResponse Ping(KokoPingRequest request)
        => new(
            ClientTimestampUtc: request.ClientTimestampUtc,
            ServerTimestampUtc: DateTimeOffset.UtcNow,
            ConnectionId: Context.ConnectionId);

    public Task<TapeMetadataOverviewDto> GetTapeMetadataOverview()
        => tapeMetadata.GetOverviewAsync(Context.ConnectionAborted);

    public Task<TapeMetadataQueryResultDto> QueryTapeMetadata(TapeMetadataQueryDto? query)
        => tapeMetadata.QueryAsync(query, Context.ConnectionAborted);

    public Task<TapeMetadataBarcodeGroupResultDto> GetTapeMetadataBarcodeGroups(TapeMetadataBarcodeGroupQueryDto? query)
        => tapeMetadata.GetBarcodeGroupsAsync(query, Context.ConnectionAborted);

    public Task<TapeMetadataQueryResultDto> GetTapeMetadataArchivesByBarcode(string barcode, TapeMetadataQueryDto? query)
        => tapeMetadata.GetArchivesByBarcodeAsync(barcode, query, Context.ConnectionAborted);

    public async Task ReindexTapeMetadata()
    {
        await tapeMetadata.QueueFullScanAsync(Context.ConnectionAborted);
    }

    public Task<TapeMetadataPruneResultDto> PruneTapeMetadata()
        => tapeMetadata.PruneAsync(Context.ConnectionAborted);

    public Task<TapeSchemaFileListDto> GetTapeSchemaFiles(string archiveXxHash128)
        => tapeSchema.GetAllFilesAsync(archiveXxHash128, Context.ConnectionAborted);

    public Task<TapeSchemaFileListDto> GetTapeSchemaDirectoryFiles(string archiveXxHash128, string? directoryPath)
        => tapeSchema.GetDirectoryFilesAsync(archiveXxHash128, directoryPath, Context.ConnectionAborted);

    public Task<TapeSchemaFileDto?> GetTapeSchemaFile(string archiveXxHash128, string filePath)
        => tapeSchema.GetFileAsync(archiveXxHash128, filePath, Context.ConnectionAborted);
}
