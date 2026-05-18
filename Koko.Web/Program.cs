
using Koko.Web.Hubs;
using Koko.Web.Data;
using Koko.Web.Services;
using Koko.Web.Storage;

using Microsoft.Extensions.FileProviders;
using Microsoft.EntityFrameworkCore;

using Serilog;
using Serilog.Events;

namespace Koko.Web;

public class Program
{
    private const string DevelopmentCorsPolicy = "KokoDevelopmentCors";

    public static void Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .WriteTo.File("logs/Koko.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Host.UseSerilog(Log.Logger, dispose: true);

            builder.Services.Configure<KokoStorageOptions>(builder.Configuration.GetSection(KokoStorageOptions.SectionName));
            builder.Services.AddSingleton<KokoStoragePaths>();
            builder.Services.AddDbContextFactory<TapeMetaDbContext>((serviceProvider, options) =>
            {
                var storagePaths = serviceProvider.GetRequiredService<KokoStoragePaths>();
                storagePaths.EnsureDirectories();
                options.UseSqlite($"Data Source={storagePaths.TapeMetaDatabasePath}");
            });
            builder.Services.AddSingleton<TapeMetadataIndexService>();
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<TapeMetadataIndexService>());
            builder.Services.AddSingleton<TapeSchemaService>();
            builder.Services.AddSignalR();

            var developmentCorsOrigins = builder.Configuration
                .GetSection("Koko:Cors:AllowedOrigins")
                .Get<string[]>()
                is { Length: > 0 } configuredOrigins
                    ? configuredOrigins
                    : [
                        "http://localhost:5173",
                        "https://localhost:5173",
                        "http://localhost:5174",
                        "https://localhost:5174",
                        "http://localhost:4173",
                        "https://localhost:4173",
                        "http://localhost:3000",
                        "https://localhost:3000",
                    ];

            builder.Services.AddCors(options =>
            {
                options.AddPolicy(DevelopmentCorsPolicy, policy =>
                {
                    policy
                        .WithOrigins(developmentCorsOrigins)
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials();
                });
            });

            var app = builder.Build();

            app.UseHttpsRedirection();

            //if (app.Environment.IsDevelopment())
            app.UseCors(DevelopmentCorsPolicy);

            app.UseAuthorization();

            app.MapHub<KokoHub>("/hubs/koko");

            var uiRoot = Path.Combine(AppContext.BaseDirectory, "ui");
            if (Directory.Exists(uiRoot))
            {
                var uiFiles = new PhysicalFileProvider(uiRoot);
                var staticFileOptions = new StaticFileOptions
                {
                    FileProvider = uiFiles
                };

                app.UseStaticFiles(staticFileOptions);
                app.MapGet("/", async context => await SendUiIndexAsync(context, uiRoot));
                app.MapFallback(async context =>
                {
                    if (!IsUiFallbackRequest(context.Request))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    await SendUiIndexAsync(context, uiRoot);
                });
            }
            else
            {
                Log.Warning("Koko.Web UI directory was not found at {UiRoot}. Build Koko.Web to copy koko-web build output into the runtime ui directory.", uiRoot);
            }

            app.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Koko.Web terminated unexpectedly.");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static bool IsUiFallbackRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
            return false;

        var path = request.Path;
        return !path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase);
    }

    private static Task SendUiIndexAsync(HttpContext context, string uiRoot)
    {
        var indexPath = Path.Combine(uiRoot, "index.html");
        if (!File.Exists(indexPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.SendFileAsync(indexPath);
    }
}
