using Koko.Core.Scsi;

using Serilog;
var startStamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Verbose()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("App", "Koko")
    .Enrich.WithProperty("AppStartTime", startStamp)
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {MethodFmt}{Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var device = SetupAPI.ListDevices("SCSI").First(x => x.ClassName.Equals("TapeDrive", StringComparison.InvariantCultureIgnoreCase));

using var drv = LtoTapeDrive.OpenDriveByPath($"\\\\.\\globalroot{device.PhysicalDeviceObjectName}");
drv.GetInquiry();
Console.ReadLine();