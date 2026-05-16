#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <vcclr.h>

#include "NativeFile.h"

namespace Koko::Native
{
    SafeFileHandle^ NativeFile::OpenExistingReadWrite(String^ path, int% win32Error)
    {
        win32Error = 0;
        if (String::IsNullOrWhiteSpace(path))
        {
            win32Error = ERROR_INVALID_PARAMETER;
            return nullptr;
        }

        pin_ptr<const wchar_t> pPath = PtrToStringChars(path);
        HANDLE handle = CreateFileW(
            pPath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr,
            OPEN_EXISTING,
            SECURITY_ANONYMOUS,
            nullptr);

        if (handle == INVALID_HANDLE_VALUE)
        {
            win32Error = static_cast<int>(GetLastError());
            return nullptr;
        }

        return gcnew SafeFileHandle(IntPtr(handle), true);
    }
}
