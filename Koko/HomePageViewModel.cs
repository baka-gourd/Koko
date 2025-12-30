using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

using Koko.Core;
using Koko.Core.Scsi;

namespace Koko;

public sealed partial class HomePageViewModel : ObservableObject
{
    private readonly IMainTabHost _tabHost;
    public ObservableCollection<DriveItem> DriveItems { get; } = new();

    public HomePageViewModel(IMainTabHost tabHost)
    {
        _tabHost = tabHost ?? throw new ArgumentNullException(nameof(tabHost));
    }

    [ObservableProperty]
    public partial DriveItem? SelectedDrive { get; set; }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open()
    {
        if (SelectedDrive is null) return;
        _tabHost.OpenDriveTab(SelectedDrive.Path, SelectedDrive.DisplayName);
    }

    private bool CanOpen()
        => !string.IsNullOrWhiteSpace(SelectedDrive?.Path);

    partial void OnSelectedDriveChanged(DriveItem? value)
        => OpenCommand.NotifyCanExecuteChanged();


    [RelayCommand]
    private Task LoadDrives()
    {
        DriveItems.Clear();
        var manager = DriveSessionManager.Instance.Value;
        var drives = SetupAPI.ListDevices("SCSI").Where(x =>
            x.ClassName is not null && x.ClassName.Equals("TapeDrive", StringComparison.InvariantCultureIgnoreCase));

        foreach (var d in drives)
        {
            if (!string.IsNullOrWhiteSpace(d.PhysicalDeviceObjectName))
            {
                using var lease = manager.Lease(d.PhysicalDeviceObjectName,
                    id => LtoTapeDrive.OpenDriveByPath($"\\\\.\\globalroot{id}"));
                if (lease.Drive is not LtoTapeDrive lto)
                {
                    continue;
                }

                lto.GetInquiry();
                DriveItems.Add(new DriveItem(
                    $"{lto.Vendor} {lto.Product}[{lto.SerialNumber}]",
                    d.PhysicalDeviceObjectName));
            }
        }

        return Task.CompletedTask;
    }
}