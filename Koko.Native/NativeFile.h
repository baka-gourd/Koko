#pragma once

using namespace System;
using namespace Microsoft::Win32::SafeHandles;
using namespace System::Runtime::InteropServices;

namespace Koko::Native
{
    public ref class NativeFile abstract sealed
    {
    public:
        static SafeFileHandle^ OpenExistingReadWrite(String^ path, [Out] int% win32Error);
    };
}
