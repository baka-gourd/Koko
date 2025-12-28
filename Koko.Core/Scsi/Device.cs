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
        ClassGuid = SetupApi.GetDeviceProperty<Guid>(info, devinfoData, PInvoke.DEVPKEY_Device_ClassGuid);
        ClassName = SetupApi.ClassNameFromGuidEx(ClassGuid);
        ClassDescription = SetupApi.GetClassDescription(ClassGuid);
        ContainerId = SetupApi.GetDeviceProperty<Guid>(info, devinfoData, PInvoke.DEVPKEY_Device_ContainerId);
        CompatibleIds =
            SetupApi.GetDeviceProperty<List<string>>(info, devinfoData, PInvoke.DEVPKEY_Device_CompatibleIds);
        Description =
            SetupApi.GetDeviceProperty<string>(info, devinfoData, PInvoke.DEVPKEY_Device_FriendlyName);
        Enumerator =
            SetupApi.GetDeviceProperty<string>(info, devinfoData, PInvoke.DEVPKEY_Device_EnumeratorName);
        Name =
            SetupApi.GetDeviceProperty<string>(info, devinfoData, PInvoke.DEVPKEY_Device_FriendlyName);
        PhysicalDeviceObjectName = SetupApi.GetDeviceRegistryProperty<string>(info, devinfoData,
            SETUP_DI_REGISTRY_PROPERTY.SPDRP_PHYSICAL_DEVICE_OBJECT_NAME);
        Present = SetupApi.GetDeviceProperty<bool>(info, devinfoData, PInvoke.DEVPKEY_Device_IsPresent);
        HardwareIds =
            SetupApi.GetDeviceProperty<List<string>>(info, devinfoData, PInvoke.DEVPKEY_Device_HardwareIds);
        Manufacturer =
            SetupApi.GetDeviceProperty<string>(info, devinfoData, PInvoke.DEVPKEY_Device_Manufacturer);
        InstanceId = SetupApi.GetDeviceInstanceId(info, devinfoData);
    }
}