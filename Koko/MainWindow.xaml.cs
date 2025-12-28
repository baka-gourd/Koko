using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

using Windows.Foundation;
using Windows.Foundation.Collections;

using Koko.Core;

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

        private int _tabCounter = 1;
        private void MainTabView_AddTabButtonClick(TabView sender, object args)
        {
            var tab = new TabViewItem
            {
                Header = $"Tab {_tabCounter++}",
                IsClosable = true,
                Content = new Grid
                {
                    Padding = new Thickness(12),
                    Children =
                    {
                        new TextBlock { Text = "New tab content" }
                    }
                }
            };

            sender.TabItems.Add(tab);
            sender.SelectedItem = tab;
        }

        private void MainTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            sender.TabItems.Remove(args.Item);
        }

        private void MainTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }
    }
}
