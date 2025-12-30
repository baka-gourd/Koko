using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

using Koko.Core;
using Koko.Core.Scsi;
using Koko.DrivePages.LTODrive;

namespace Koko;

public sealed partial class MainWindowViewModel : ObservableObject, IMainTabHost
{
    public ObservableCollection<TabItemViewModel> Tabs { get; } = new();

    [ObservableProperty]
    public partial TabItemViewModel? SelectedTab { get; set; }

    public MainWindowViewModel()
    {
        var homeVm = new HomePageViewModel(this);
        // 初始 Home Tab
        var home = new TabItemViewModel
        {
            Header = "Home",
            IsClosable = false,
            PageType = typeof(HomePage),
            Parameter = homeVm
        };
        Tabs.Add(home);
        SelectedTab = home;
    }

    public void OpenDriveTab(string path, string header)
    {
        var existing = Tabs.FirstOrDefault(t =>
            t.Kind == TabKind.LtoDrive &&
            string.Equals(t.UniqueKey, path, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        var tab = new TabItemViewModel
        {
            Kind = TabKind.LtoDrive,
            UniqueKey = path,
            Header = header,
            IsClosable = true,
            PageType = typeof(LTOCommandPalette),
            Parameter = new LtoCommandPaletteNavArgs(path)
        };

        Tabs.Add(tab);
        SelectedTab = tab;
    }

    [RelayCommand]
    private void CloseTab(TabItemViewModel? tab)
    {
        if (tab is null) return;

        var index = Tabs.IndexOf(tab);
        if (index < 0) return;

        Tabs.Remove(tab);

        if (Tabs.Count == 0)
        {
            SelectedTab = null;
            return;
        }

        if (SelectedTab == tab)
        {
            var newIndex = Math.Clamp(index - 1, 0, Tabs.Count - 1);
            SelectedTab = Tabs[newIndex];
        }
    }
}

public sealed record DriveItem(string DisplayName, string Path);

public enum TabKind
{
    Home = 0,
    LtoDrive = 1,
    Other = 99,
}

public sealed partial class TabItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Header { get; set; } = "";

    [ObservableProperty]
    public partial bool IsClosable { get; set; } = true;

    [ObservableProperty]
    public partial Type? PageType { get; set; }

    [ObservableProperty]
    public partial object? Parameter { get; set; }

    public string? UniqueKey { get; set; }

    public TabKind Kind { get; set; } = TabKind.Other;
}