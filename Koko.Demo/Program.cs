using Koko.Core.Scsi;

using Serilog;
var startStamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Verbose()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("App", "Koko")
    .Enrich.WithProperty("AppStartTime", startStamp)
    .WriteTo.Console()
    .CreateLogger();

foreach (var device in SetupApi.ListDevices())
{

}