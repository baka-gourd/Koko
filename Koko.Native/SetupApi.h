#pragma once

#include "NativeDevice.h"

using namespace System;

namespace Koko::Native
{
    public ref class SetupApi abstract sealed
    {
    public:
        static array<NativeDevice^>^ ListDevices(String^ enumerator);
    };
}
