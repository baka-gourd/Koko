using Serilog;

namespace Koko.Core.Scsi;

public static class SetupAPI
{
    public static IEnumerable<Device> ListDevices(string? enumerator = null)
    {
        foreach (var nativeDevice in Koko.Native.SetupApi.ListDevices(enumerator))
        {
            var device = new Device(nativeDevice);
            Log.Verbose("Get device: {@Device}", device);
            yield return device;
        }
    }
}
