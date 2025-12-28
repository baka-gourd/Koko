using System.Runtime.InteropServices;
using System.Text;

using Windows.Win32;
using Windows.Win32.Devices.Properties;
using Windows.Win32.System.Registry;

namespace Koko.Core.Scsi;

public static partial class SetupApi
{
    private static string DecodeRegSz(ReadOnlySpan<byte> data)
    {
        var len = data.Length;

        if (len >= 2 && data[len - 2] == 0 && data[len - 1] == 0)
            len -= 2;

        return Encoding.Unicode.GetString(data[..len]);
    }

    private static string[] DecodeRegMultiSz(ReadOnlySpan<byte> data)
    {
        var all = DecodeRegSz(data);

        return all.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static uint DecodeDword(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return 0;

        return MemoryMarshal.Read<uint>(data);
    }

    private static ulong DecodeQword(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            return 0;

        return MemoryMarshal.Read<ulong>(data);
    }

    private static T? ConvertRegistryData<T>(REG_VALUE_TYPE valueType, ReadOnlySpan<byte> data)
    {
        if (typeof(T) == typeof(Guid))
        {
            if (valueType is REG_VALUE_TYPE.REG_SZ or REG_VALUE_TYPE.REG_EXPAND_SZ)
            {
                var s = DecodeRegSz(data);
                if (Guid.TryParse(s, out var g))
                    return (T)(object)g;
            }

            return default;
        }

        object? boxed = valueType switch
        {
            REG_VALUE_TYPE.REG_SZ or REG_VALUE_TYPE.REG_EXPAND_SZ
                => DecodeRegSz(data),

            REG_VALUE_TYPE.REG_MULTI_SZ
                => DecodeRegMultiSz(data),

            REG_VALUE_TYPE.REG_DWORD
                => DecodeDword(data),

            REG_VALUE_TYPE.REG_QWORD
                => DecodeQword(data),

            REG_VALUE_TYPE.REG_BINARY or _
                => data.ToArray(),
        };

        if (boxed is T t)
            return t;

        try
        {
            return (T)Convert.ChangeType(boxed, typeof(T));
        }
        catch
        {
            return default;
        }
    }

    //new

    private static T? ConvertDevPropData<T>(DEVPROPTYPE type, ReadOnlySpan<byte> data)
    {
        // Guid
        if (typeof(T) == typeof(Guid))
        {
            if (type == DEVPROPTYPE.DEVPROP_TYPE_GUID && data.Length >= 16)
                return (T)(object)new Guid(data[..16]);
            return default;
        }

        // bool
        if (typeof(T) == typeof(bool))
        {
            if (type == DEVPROPTYPE.DEVPROP_TYPE_BOOLEAN && data.Length >= 1)
                return (T)(object)(data[0] != 0);
            return default;
        }

        // string
        if (typeof(T) == typeof(string))
        {
            if (type == DEVPROPTYPE.DEVPROP_TYPE_STRING)
                return (T)(object)DecodeDevPropString(data);
            return default;
        }

        // string[] / List<string>
        if (typeof(T) == typeof(string[]) || typeof(T) == typeof(List<string>))
        {
            if (type == DEVPROPTYPE.DEVPROP_TYPE_STRING_LIST)
            {
                var arr = DecodeDevPropStringList(data);
                if (typeof(T) == typeof(string[]))
                    return (T)(object)arr;

                return (T)(object)arr.ToList();
            }

            return default;
        }

        // uint / int / ulong / long
        object? boxed = type switch
        {
            DEVPROPTYPE.DEVPROP_TYPE_UINT32 when data.Length >= 4 => MemoryMarshal.Read<uint>(data),
            DEVPROPTYPE.DEVPROP_TYPE_INT32 when data.Length >= 4 => MemoryMarshal.Read<int>(data),
            DEVPROPTYPE.DEVPROP_TYPE_UINT64 when data.Length >= 8 => MemoryMarshal.Read<ulong>(data),
            DEVPROPTYPE.DEVPROP_TYPE_INT64 when data.Length >= 8 => MemoryMarshal.Read<long>(data),
            _ => null,
        };

        if (boxed is null)
            return default;

        if (boxed is T t2)
            return t2;

        try { return (T)Convert.ChangeType(boxed, typeof(T)); }
        catch { return default; }
    }

    private static string DecodeDevPropString(ReadOnlySpan<byte> data)
    {
        // DEVPROP_TYPE_STRING: UTF-16LE, usually NUL-terminated; requiredBytes includes terminator.
        int len = data.Length;
        if (len >= 2 && data[len - 2] == 0 && data[len - 1] == 0)
            len -= 2;

        return Encoding.Unicode.GetString(data[..len]);
    }

    private static string[] DecodeDevPropStringList(ReadOnlySpan<byte> data)
    {
        // DEVPROP_TYPE_STRING_LIST: REG_MULTI_SZ-like UTF-16 list
        var all = DecodeDevPropString(data);
        return all.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}