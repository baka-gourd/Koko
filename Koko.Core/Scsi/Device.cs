using System.Text;

using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;

namespace Koko.Core.Scsi;

public class Device
{
    public string? ClassDescription { get; set; }
    public Guid ClassGuid { get; set; }
    public string? ClassName { get; set; }
    public List<string>? CompatibleIds { get; set; }
    public Guid ContainerId { get; set; }
    public string? Description { get; set; }
    public string? Enumerator { get; set; }
    public List<string>? HardwareIds { get; set; }
    public string? InstanceId { get; set; }
    public string? Manufacturer { get; set; }
    public string? Name { get; set; }
    public string? PhysicalDeviceObjectName { get; set; }
    public bool Present { get; set; }

    internal Device(SetupDiDestroyDeviceInfoListSafeHandle info, SP_DEVINFO_DATA devinfoData)
    {
        ClassGuid = SetupAPI.GetDeviceProperty<Guid>(info, devinfoData, PInvoke.DEVPKEY_Device_ClassGuid);
        ClassName = SetupAPI.ClassNameFromGuidEx(ClassGuid);
        ClassDescription = SetupAPI.GetClassDescription(ClassGuid);
        ContainerId = SetupAPI.GetDeviceProperty<Guid>(info, devinfoData, PInvoke.DEVPKEY_Device_ContainerId);
        CompatibleIds =
            SetupAPI.GetDeviceProperty<List<string>>(info, devinfoData, PInvoke.DEVPKEY_Device_CompatibleIds);
        Description =
            SetupAPI.GetDeviceProperty<string>(info, devinfoData, PInvoke.DEVPKEY_Device_FriendlyName);
        Enumerator =
            SetupAPI.GetDeviceProperty<string>(info, devinfoData, PInvoke.DEVPKEY_Device_EnumeratorName);
        Name =
            SetupAPI.GetDeviceProperty<string>(info, devinfoData, PInvoke.DEVPKEY_Device_FriendlyName);
        PhysicalDeviceObjectName = SetupAPI.GetDeviceRegistryProperty<string>(info, devinfoData,
            SETUP_DI_REGISTRY_PROPERTY.SPDRP_PHYSICAL_DEVICE_OBJECT_NAME);
        Present = SetupAPI.GetDeviceProperty<bool>(info, devinfoData, PInvoke.DEVPKEY_Device_IsPresent);
        HardwareIds =
            SetupAPI.GetDeviceProperty<List<string>>(info, devinfoData, PInvoke.DEVPKEY_Device_HardwareIds);
        Manufacturer =
            SetupAPI.GetDeviceProperty<string>(info, devinfoData, PInvoke.DEVPKEY_Device_Manufacturer);
        InstanceId = SetupAPI.GetDeviceInstanceId(info, devinfoData);
    }
}