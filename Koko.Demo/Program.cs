using Koko.Core.Scsi.Parsers;

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

//var device = SetupAPI.ListDevices("SCSI").First(x => x.ClassName.Equals("TapeDrive", StringComparison.InvariantCultureIgnoreCase));

//var manager = DriveSessionManager.Instance.Value;
//using var tape = manager.Lease("tape0", (id) =>
//    LtoTapeDrive.OpenDriveByPath($"\\\\.\\globalroot{device.PhysicalDeviceObjectName}"));

//if (tape.Drive is not LtoTapeDrive lto)
//{
//    return;
//}

//lto.GetInquiry();
var data = File.ReadAllBytes("R:/test2.cm");

var cm = CMParser.CreateFromSpan(data);
Log.Information("{@cm}", cm);

Console.ReadLine();