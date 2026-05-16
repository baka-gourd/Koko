#pragma once

using namespace System;

namespace Koko::Native
{
    public ref class NativeDevice sealed
    {
    public:
        property String^ ClassDescription;
        property Guid ClassGuid;
        property String^ ClassName;
        property array<String^>^ CompatibleIds;
        property Guid ContainerId;
        property String^ Description;
        property String^ Enumerator;
        property array<String^>^ HardwareIds;
        property String^ InstanceId;
        property String^ Manufacturer;
        property String^ Name;
        property String^ PhysicalDeviceObjectName;
        property bool Present;
    };
}
