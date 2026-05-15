using System.Runtime.CompilerServices;

using Serilog;
using Serilog.Context;

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

        extension(Log)
        {
            public static IDisposable PushMethod(
                [CallerMemberName] string method = "")
            {
                if (string.IsNullOrEmpty(method))
                    return LogContext.PushProperty("MethodFmt", "");

                return LogContext.PushProperty("MethodFmt", $"<{method}> ");
            }
        }
    }
}
