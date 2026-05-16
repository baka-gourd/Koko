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

    internal Device(Koko.Native.NativeDevice nativeDevice)
    {
        ClassGuid = nativeDevice.ClassGuid;
        ClassName = nativeDevice.ClassName;
        ClassDescription = nativeDevice.ClassDescription;
        ContainerId = nativeDevice.ContainerId;
        CompatibleIds = nativeDevice.CompatibleIds?.ToList();
        Description = nativeDevice.Description;
        Enumerator = nativeDevice.Enumerator;
        Name = nativeDevice.Name;
        PhysicalDeviceObjectName = nativeDevice.PhysicalDeviceObjectName;
        Present = nativeDevice.Present;
        HardwareIds = nativeDevice.HardwareIds?.ToList();
        Manufacturer = nativeDevice.Manufacturer;
        InstanceId = nativeDevice.InstanceId;
    }
}
