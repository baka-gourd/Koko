using Windows.Win32;

using Serilog;

namespace Koko.Core
{
    public static class LogUtil
    {
        public static void CloseAndFlush()
        {
            try
            {
                Log.CloseAndFlush();
            }
            catch
            {
                // ignored
            }
        }
    }
}
