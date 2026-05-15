using System.ComponentModel;
using System.Runtime.InteropServices;

using Serilog;

namespace Koko.Core.Scsi;

public sealed record TapeDeviceInterface(string DevicePath);

public static partial class SetupAPI
{
    private static readonly Guid GuidDevinterfaceTape = new("53f5630b-b6bf-11d0-94f2-00a0c91efb8b");

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static IEnumerable<TapeDeviceInterface> ListTapeDeviceInterfaces()
    {
        var classGuid = GuidDevinterfaceTape;
        var hDevInfo = SetupDiGetClassDevs(
            ref classGuid,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);

        if (hDevInfo == InvalidHandleValue)
            yield break;

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>(),
                };

                if (!SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref classGuid, index, ref interfaceData))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorNoMoreItems)
                    {
                        Log.Error(
                            new Win32Exception(error),
                            "SetupDiEnumDeviceInterfaces failed for tape interface index {Index}",
                            index);
                    }

                    yield break;
                }

                SetupDiGetDeviceInterfaceDetail(
                    hDevInfo,
                    ref interfaceData,
                    IntPtr.Zero,
                    0,
                    out var requiredBytes,
                    IntPtr.Zero);

                var detailError = Marshal.GetLastWin32Error();
                if (requiredBytes == 0 || detailError != ErrorInsufficientBuffer)
                {
                    Log.Error(
                        new Win32Exception(detailError),
                        "SetupDiGetDeviceInterfaceDetail did not report a tape interface path buffer size at index {Index}",
                        index);
                    continue;
                }

                var detailData = Marshal.AllocHGlobal((int)requiredBytes);
                try
                {
                    Marshal.WriteInt32(detailData, IntPtr.Size == 8 ? 8 : 6);

                    if (!SetupDiGetDeviceInterfaceDetail(
                            hDevInfo,
                            ref interfaceData,
                            detailData,
                            requiredBytes,
                            out _,
                            IntPtr.Zero))
                    {
                        var error = Marshal.GetLastWin32Error();
                        Log.Error(
                            new Win32Exception(error),
                            "SetupDiGetDeviceInterfaceDetail failed for tape interface index {Index}",
                            index);
                        continue;
                    }

                    var devicePath = Marshal.PtrToStringUni(IntPtr.Add(detailData, 4));
                    if (!string.IsNullOrWhiteSpace(devicePath))
                        yield return new TapeDeviceInterface(devicePath);
                }
                finally
                {
                    Marshal.FreeHGlobal(detailData);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hDevInfo);
        }
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nuint Reserved;
    }
}
