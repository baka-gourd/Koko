using System.ComponentModel;
using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Devices.Properties;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

using Serilog;

namespace Koko.Core.Scsi;

public static partial class SetupApi
{
    public static IEnumerable<Device> ListDevices(string? enumerator = null)
    {
        using var hDevInfo = PInvoke.SetupDiGetClassDevs(null, enumerator, HWND.Null,
            Flags: SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_PRESENT | SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_ALLCLASSES);

        if (hDevInfo.IsInvalid)
        {
            yield break;
        }

        uint index = 0;
        var devInfo = new SP_DEVINFO_DATA
        {
            cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
        };

        while (PInvoke.SetupDiEnumDeviceInfo(hDevInfo, index, ref devInfo))
        {
            var dev = new Device(hDevInfo, devInfo);
            Log.Verbose("Get device: {@Device}", dev);
            yield return dev;
            index++;
        }
    }

    internal static T? GetDeviceRegistryProperty<T>(
        SetupDiDestroyDeviceInfoListSafeHandle info,
        SP_DEVINFO_DATA devInfo,
        SETUP_DI_REGISTRY_PROPERTY property)
    {
        Span<byte> stackBuffer = stackalloc byte[512];

        return TryGetDeviceRegistryPropertyRaw(info, devInfo, property, stackBuffer, out var valueType, out var data) ? ConvertRegistryData<T>(valueType, data) : default;
    }

    private static bool TryGetDeviceRegistryPropertyRaw(
        SetupDiDestroyDeviceInfoListSafeHandle info,
        SP_DEVINFO_DATA devInfo,
        SETUP_DI_REGISTRY_PROPERTY property,
        Span<byte> stackBuffer,
        out REG_VALUE_TYPE valueType,
        out ReadOnlySpan<byte> data)
    {
        valueType = 0;
        data = default;

        bool ok = PInvoke.SetupDiGetDeviceRegistryProperty(
            info,
            devInfo,
            property,
            out var rawType,
            stackBuffer,
            out var requiredSizeBytes);

        valueType = (REG_VALUE_TYPE)rawType;

        if (ok)
        {
            data = stackBuffer[..checked((int)requiredSizeBytes)];
            return true;
        }

        var error = (WIN32_ERROR)Marshal.GetLastWin32Error();
        if (error != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
        {
            Log.Error(
                new Win32Exception((int)error),
                "SetupDiGetDeviceRegistryProperty failed for {DevInfo} prop={Property} type={Type}",
                devInfo,
                property,
                valueType);

            return false;
        }

        if (requiredSizeBytes == 0)
        {
            Log.Error(
                new Win32Exception((int)error),
                "SetupDiGetDeviceRegistryProperty returned INSUFFICIENT_BUFFER but requiredSizeBytes=0 for {DevInfo} prop={Property}",
                devInfo,
                property);

            return false;
        }

        var heap = new byte[requiredSizeBytes];

        ok = PInvoke.SetupDiGetDeviceRegistryProperty(
            info,
            devInfo,
            property,
            out rawType,
            heap,
            out requiredSizeBytes);

        valueType = (REG_VALUE_TYPE)rawType;

        if (!ok)
        {
            error = (WIN32_ERROR)Marshal.GetLastWin32Error();
            Log.Error(
                new Win32Exception((int)error),
                "SetupDiGetDeviceRegistryProperty failed after retry for {DevInfo} prop={Property} type={Type}",
                devInfo,
                property,
                valueType);

            return false;
        }

        data = heap.AsSpan(0, checked((int)requiredSizeBytes));
        return true;
    }

    internal static string? ClassNameFromGuidEx(Guid classGuid, string? machineName = null)
    {
        if (classGuid == Guid.Empty)
        {
            return "Unknown";
        }

        Span<char> stackBuffer = stackalloc char[256];

        if (PInvoke.SetupDiClassNameFromGuidEx(
                classGuid,
                stackBuffer,
                out var requiredSize,
                machineName))
        {
            return new string(stackBuffer[..((int)requiredSize - 1)]);
        }

        var error = (WIN32_ERROR)Marshal.GetLastWin32Error();
        if (error != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
        {
            Log.Error(
                new Win32Exception((int)error),
                "Failed to get class name for {Guid}",
                classGuid);

            return null;
        }

        var heapBuffer = new char[requiredSize];

        if (PInvoke.SetupDiClassNameFromGuidEx(
                classGuid,
                heapBuffer,
                out _,
                machineName)) return new string(heapBuffer, 0, (int)requiredSize - 1);
        error = (WIN32_ERROR)Marshal.GetLastWin32Error();
        Log.Error(
            new Win32Exception((int)error),
            "Failed to get class name for {Guid} after retry",
            classGuid);

        return null;
    }

    internal static string? GetClassDescription(Guid classGuid, string? machineName = null)
    {
        if (classGuid == Guid.Empty)
            return "Other devices";

        Span<char> stackBuffer = stackalloc char[256];

        if (PInvoke.SetupDiGetClassDescriptionEx(classGuid, stackBuffer, out var requiredSize, machineName))
        {
            return new string(stackBuffer[..((int)requiredSize - 1)]);
        }

        var error = (WIN32_ERROR)Marshal.GetLastWin32Error();
        if (error != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
        {
            Log.Error(
                new Win32Exception((int)error),
                "Failed to get class description for {Guid}",
                classGuid);
            return null;
        }

        var heapBuffer = new char[requiredSize];

        if (PInvoke.SetupDiGetClassDescriptionEx(classGuid, stackBuffer, out var size, machineName))
        {
            return new string(heapBuffer, 0, (int)size - 1);
        }

        error = (WIN32_ERROR)Marshal.GetLastWin32Error();
        Log.Error(
            new Win32Exception((int)error),
            "Failed to get class description for {Guid} after retry",
            classGuid);

        return null;
    }

    internal static T? GetDeviceProperty<T>(
        SetupDiDestroyDeviceInfoListSafeHandle info,
        SP_DEVINFO_DATA devInfo,
        DEVPROPKEY key)
    {
        Span<byte> stack = stackalloc byte[256];

        if (TryGetDevicePropertyRaw(info, devInfo, key, stack, out var propType, out var data))
            return ConvertDevPropData<T>(propType, data);

        return default;
    }


    private static bool TryGetDevicePropertyRaw(
        SetupDiDestroyDeviceInfoListSafeHandle info,
        SP_DEVINFO_DATA devInfo,
        DEVPROPKEY key,
        Span<byte> stackBuffer,
        out DEVPROPTYPE propType,
        out ReadOnlySpan<byte> data)
    {
        propType = default;
        data = default;

        bool ok = PInvoke.SetupDiGetDeviceProperty(
            info,
            devInfo,
            key,
            out propType,
            stackBuffer,
            out uint requiredBytes,
            0);

        if (ok)
        {
            data = stackBuffer[..checked((int)requiredBytes)];
            return true;
        }

        var error = (WIN32_ERROR)Marshal.GetLastWin32Error();
        if (error == WIN32_ERROR.ERROR_NOT_FOUND)
            return false;

        if (error != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
        {
            Log.Error(new Win32Exception((int)error),
                "SetupDiGetDeviceProperty failed. key={Key} type={Type}", key, propType);
            return false;
        }

        if (requiredBytes == 0)
            return false;

        var heap = new byte[requiredBytes];

        ok = PInvoke.SetupDiGetDeviceProperty(
            info,
            devInfo,
            key,
            out propType,
            heap,
            out requiredBytes,
            0);

        if (!ok)
        {
            error = (WIN32_ERROR)Marshal.GetLastWin32Error();
            if (error == WIN32_ERROR.ERROR_NOT_FOUND)
                return false;

            Log.Error(new Win32Exception((int)error),
                "SetupDiGetDeviceProperty failed after retry. key={Key} type={Type}", key, propType);
            return false;
        }

        data = heap.AsSpan(0, checked((int)requiredBytes));
        return true;
    }

    internal static string? GetDeviceInstanceId(SetupDiDestroyDeviceInfoListSafeHandle info, SP_DEVINFO_DATA devInfo)
    {
        Span<char> stack = stackalloc char[260];

        if (PInvoke.SetupDiGetDeviceInstanceId(info, devInfo, stack, out var required))
            return new string(stack[..((int)required - 1)]);

        var error = (WIN32_ERROR)Marshal.GetLastWin32Error();
        if (error != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            return null;

        var heap = new char[required];
        if (PInvoke.SetupDiGetDeviceInstanceId(info, devInfo, heap, out _))
            return new string(heap, 0, (int)required - 1);

        return null;
    }
}