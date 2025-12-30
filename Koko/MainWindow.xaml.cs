using Koko.Core;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Serilog;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Koko
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Closed += (_, _) => LogUtil.CloseAndFlush();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBar);
        }

        private void TabFrame_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (sender is not Frame frame) return;
            if (args.NewValue is not TabItemViewModel tab) return;

            if (tab.PageType is null) return;

            // 避免重复导航
            if (frame.CurrentSourcePageType == tab.PageType) return;

            var ok = frame.Navigate(tab.PageType, tab.Parameter);

            Log.Debug("Navigate {Page} => {Ok}", tab.PageType, ok);
        }

        private void TabView_OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (sender.DataContext is MainWindowViewModel vm &&
                args.Item is TabItemViewModel tab)
            {
                vm.CloseTabCommand.Execute(tab);
            }
        }
    }
}
