using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;

using Windows.Foundation;
using Windows.Foundation.Collections;

using DevWinUI;
using Microsoft.UI.Dispatching;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Koko.DrivePages.LTODrive
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class LTOCommandPalette : Page
    {
        public LTOCommandPalette()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is LtoCommandPaletteNavArgs args)
                DataContext = new LTOCommandPaletteViewModel(args);
        }

        private void Tabs_OnSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {

            TabContent.ContentTemplate = Tabs.SelectedItem.Name switch
            {
                "Command" => (DataTemplate)Resources["TplCommand"],
                "Buffer" => (DataTemplate)Resources["TplBuffer"],
                "MAM" => (DataTemplate)Resources["TplMam"],
                "Log" => (DataTemplate)Resources["TplLog"],
                "Test" => (DataTemplate)Resources["TplTest"],
                _ => (DataTemplate)Resources["TplCommand"],
            };
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, UpdateIconColors);
        }

        private void UpdateIconColors()
        {
            var primaryBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            var secondaryBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

            var items = new[]
            {
                (Command, "CommandIcon"),
                (Buffer, "BufferIcon"),
                (MAM, "MAMIcon"),
                (Log, "LogIcon"),
                (Test, "TestIcon")
            };

            foreach (var (item, iconName) in items)
            {
                if (item.Icon is FontIcon icon)
                {
                    icon.Foreground = item.IsSelected ? primaryBrush : secondaryBrush;
                }
            }
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            TabContent.ContentTemplate = (DataTemplate)Resources["TplCommand"];
        }
    }
}
