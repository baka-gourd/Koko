#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <setupapi.h>
#include <initguid.h>
#include <coguid.h>
#include <devpkey.h>
#include <cwchar>
#include <vector>
#include <vcclr.h>

#include "SetupApi.h"

using namespace System::Collections::Generic;

namespace
{
    String^ ToManagedString(const wchar_t* value)
    {
        return value == nullptr || value[0] == L'\0' ? nullptr : gcnew String(value);
    }

    String^ ToManagedString(const std::vector<wchar_t>& value)
    {
        return value.empty() ? nullptr : ToManagedString(value.data());
    }

    array<String^>^ ToManagedStringList(const wchar_t* value)
    {
        auto list = gcnew List<String^>();
        if (value == nullptr)
            return list->ToArray();

        const wchar_t* current = value;
        while (*current != L'\0')
        {
            list->Add(gcnew String(current));
            current += std::wcslen(current) + 1;
        }

        return list->ToArray();
    }

    String^ GetDevicePropertyString(HDEVINFO info, SP_DEVINFO_DATA& devInfo, const DEVPROPKEY& key)
    {
        DEVPROPTYPE type = 0;
        DWORD requiredBytes = 0;
        std::vector<BYTE> buffer(512);

        if (!SetupDiGetDevicePropertyW(info, &devInfo, &key, &type, buffer.data(), static_cast<DWORD>(buffer.size()), &requiredBytes, 0))
        {
            if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || requiredBytes == 0)
                return nullptr;

            buffer.resize(requiredBytes);
            if (!SetupDiGetDevicePropertyW(info, &devInfo, &key, &type, buffer.data(), requiredBytes, &requiredBytes, 0))
                return nullptr;
        }

        if (type != DEVPROP_TYPE_STRING)
            return nullptr;

        return ToManagedString(reinterpret_cast<const wchar_t*>(buffer.data()));
    }

    array<String^>^ GetDevicePropertyStringList(HDEVINFO info, SP_DEVINFO_DATA& devInfo, const DEVPROPKEY& key)
    {
        DEVPROPTYPE type = 0;
        DWORD requiredBytes = 0;
        std::vector<BYTE> buffer(512);

        if (!SetupDiGetDevicePropertyW(info, &devInfo, &key, &type, buffer.data(), static_cast<DWORD>(buffer.size()), &requiredBytes, 0))
        {
            if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || requiredBytes == 0)
                return gcnew array<String^>(0);

            buffer.resize(requiredBytes);
            if (!SetupDiGetDevicePropertyW(info, &devInfo, &key, &type, buffer.data(), requiredBytes, &requiredBytes, 0))
                return gcnew array<String^>(0);
        }

        if (type != DEVPROP_TYPE_STRING_LIST)
            return gcnew array<String^>(0);

        return ToManagedStringList(reinterpret_cast<const wchar_t*>(buffer.data()));
    }

    Guid GetDevicePropertyGuid(HDEVINFO info, SP_DEVINFO_DATA& devInfo, const DEVPROPKEY& key)
    {
        DEVPROPTYPE type = 0;
        DWORD requiredBytes = 0;
        GUID guid = {};

        if (!SetupDiGetDevicePropertyW(info, &devInfo, &key, &type, reinterpret_cast<PBYTE>(&guid), sizeof(guid), &requiredBytes, 0) ||
            type != DEVPROP_TYPE_GUID)
        {
            return Guid::Empty;
        }

        return Guid(guid.Data1, guid.Data2, guid.Data3, guid.Data4[0], guid.Data4[1], guid.Data4[2], guid.Data4[3], guid.Data4[4], guid.Data4[5], guid.Data4[6], guid.Data4[7]);
    }

    bool GetDevicePropertyBool(HDEVINFO info, SP_DEVINFO_DATA& devInfo, const DEVPROPKEY& key)
    {
        DEVPROPTYPE type = 0;
        DWORD requiredBytes = 0;
        DEVPROP_BOOLEAN value = DEVPROP_FALSE;

        return SetupDiGetDevicePropertyW(info, &devInfo, &key, &type, reinterpret_cast<PBYTE>(&value), sizeof(value), &requiredBytes, 0) &&
            type == DEVPROP_TYPE_BOOLEAN &&
            value != DEVPROP_FALSE;
    }

    String^ GetDeviceRegistryString(HDEVINFO info, SP_DEVINFO_DATA& devInfo, DWORD property)
    {
        DWORD type = 0;
        DWORD requiredBytes = 0;
        std::vector<BYTE> buffer(512);

        if (!SetupDiGetDeviceRegistryPropertyW(info, &devInfo, property, &type, buffer.data(), static_cast<DWORD>(buffer.size()), &requiredBytes))
        {
            if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || requiredBytes == 0)
                return nullptr;

            buffer.resize(requiredBytes);
            if (!SetupDiGetDeviceRegistryPropertyW(info, &devInfo, property, &type, buffer.data(), requiredBytes, &requiredBytes))
                return nullptr;
        }

        if (type != REG_SZ && type != REG_EXPAND_SZ)
            return nullptr;

        return ToManagedString(reinterpret_cast<const wchar_t*>(buffer.data()));
    }

    String^ ClassNameFromGuid(const GUID& classGuid)
    {
        if (IsEqualGUID(classGuid, GUID_NULL))
            return "Unknown";

        DWORD requiredChars = 0;
        wchar_t stack[256] = {};

        if (SetupDiClassNameFromGuidExW(&classGuid, stack, ARRAYSIZE(stack), &requiredChars, nullptr, nullptr))
            return ToManagedString(stack);

        if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || requiredChars == 0)
            return nullptr;

        std::vector<wchar_t> buffer(requiredChars);
        if (SetupDiClassNameFromGuidExW(&classGuid, buffer.data(), requiredChars, &requiredChars, nullptr, nullptr))
            return ToManagedString(buffer);

        return nullptr;
    }

    String^ ClassDescriptionFromGuid(const GUID& classGuid)
    {
        if (IsEqualGUID(classGuid, GUID_NULL))
            return "Other devices";

        DWORD requiredChars = 0;
        wchar_t stack[256] = {};

        if (SetupDiGetClassDescriptionExW(&classGuid, stack, ARRAYSIZE(stack), &requiredChars, nullptr, nullptr))
            return ToManagedString(stack);

        if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || requiredChars == 0)
            return nullptr;

        std::vector<wchar_t> buffer(requiredChars);
        if (SetupDiGetClassDescriptionExW(&classGuid, buffer.data(), requiredChars, &requiredChars, nullptr, nullptr))
            return ToManagedString(buffer);

        return nullptr;
    }

    String^ GetDeviceInstanceId(HDEVINFO info, SP_DEVINFO_DATA& devInfo)
    {
        DWORD requiredChars = 0;
        wchar_t stack[260] = {};

        if (SetupDiGetDeviceInstanceIdW(info, &devInfo, stack, ARRAYSIZE(stack), &requiredChars))
            return ToManagedString(stack);

        if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || requiredChars == 0)
            return nullptr;

        std::vector<wchar_t> buffer(requiredChars);
        if (SetupDiGetDeviceInstanceIdW(info, &devInfo, buffer.data(), requiredChars, &requiredChars))
            return ToManagedString(buffer);

        return nullptr;
    }

    GUID ToNativeGuid(Guid value)
    {
        array<unsigned char>^ bytes = value.ToByteArray();
        pin_ptr<unsigned char> pBytes = &bytes[0];
        return *reinterpret_cast<GUID*>(pBytes);
    }
}

namespace Koko::Native
{
    array<NativeDevice^>^ SetupApi::ListDevices(String^ enumerator)
    {
        pin_ptr<const wchar_t> pEnumerator = String::IsNullOrEmpty(enumerator) ? nullptr : PtrToStringChars(enumerator);
        HDEVINFO info = SetupDiGetClassDevsW(
            nullptr,
            pEnumerator,
            nullptr,
            DIGCF_PRESENT | DIGCF_ALLCLASSES);

        auto devices = gcnew List<NativeDevice^>();
        if (info == INVALID_HANDLE_VALUE)
            return devices->ToArray();

        try
        {
            SP_DEVINFO_DATA devInfo = {};
            devInfo.cbSize = sizeof(SP_DEVINFO_DATA);

            for (DWORD index = 0; SetupDiEnumDeviceInfo(info, index, &devInfo); index++)
            {
                auto device = gcnew NativeDevice();
                GUID classGuid = ToNativeGuid(GetDevicePropertyGuid(info, devInfo, DEVPKEY_Device_ClassGuid));

                device->ClassGuid = Guid(classGuid.Data1, classGuid.Data2, classGuid.Data3, classGuid.Data4[0], classGuid.Data4[1], classGuid.Data4[2], classGuid.Data4[3], classGuid.Data4[4], classGuid.Data4[5], classGuid.Data4[6], classGuid.Data4[7]);
                device->ClassName = ClassNameFromGuid(classGuid);
                device->ClassDescription = ClassDescriptionFromGuid(classGuid);
                device->ContainerId = GetDevicePropertyGuid(info, devInfo, DEVPKEY_Device_ContainerId);
                device->CompatibleIds = GetDevicePropertyStringList(info, devInfo, DEVPKEY_Device_CompatibleIds);
                device->Description = GetDevicePropertyString(info, devInfo, DEVPKEY_Device_FriendlyName);
                device->Enumerator = GetDevicePropertyString(info, devInfo, DEVPKEY_Device_EnumeratorName);
                device->Name = GetDevicePropertyString(info, devInfo, DEVPKEY_Device_FriendlyName);
                device->PhysicalDeviceObjectName = GetDeviceRegistryString(info, devInfo, SPDRP_PHYSICAL_DEVICE_OBJECT_NAME);
                device->Present = GetDevicePropertyBool(info, devInfo, DEVPKEY_Device_IsPresent);
                device->HardwareIds = GetDevicePropertyStringList(info, devInfo, DEVPKEY_Device_HardwareIds);
                device->Manufacturer = GetDevicePropertyString(info, devInfo, DEVPKEY_Device_Manufacturer);
                device->InstanceId = GetDeviceInstanceId(info, devInfo);

                devices->Add(device);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(info);
        }

        return devices->ToArray();
    }
}
