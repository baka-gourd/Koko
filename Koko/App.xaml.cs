using Koko.Core;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

using Serilog;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;

using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

using Path = System.IO.Path;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Koko
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();

            var startStamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.WithProperty("App", "Koko")
                .Enrich.WithProperty("AppStartTime", startStamp)
                .WriteTo.File(Path.Combine("logs", $"koko-{startStamp}.log"), rollingInterval: RollingInterval.Infinite)
                .CreateLogger();

            UnhandledException += (_, e) =>
            {
                Log.Error(e.Exception, "UI UnhandledException");
                Log.CloseAndFlush();
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Log.Error(e.ExceptionObject as Exception, "AppDomain UnhandledException");
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error(e.Exception, "UnobservedTaskException");
                e.SetObserved();
            };
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();
            Log.Information("Koko launched");
        }
    }
}
