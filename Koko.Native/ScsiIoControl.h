#pragma once

using namespace System;
using namespace System::Runtime::InteropServices;

namespace Koko::Native
{
    public ref class ScsiIoControl abstract sealed
    {
    public:
        static property int SptdSize
        {
            int get();
        }

        static property int PacketSize
        {
            int get();
        }

        static property unsigned int SenseInfoOffset
        {
            unsigned int get();
        }

        static property int NoDataPacketSize
        {
            int get();
        }

        static property unsigned int NoDataSenseInfoOffset
        {
            unsigned int get();
        }

        static array<unsigned char>^ CreateNoDataPacket(
            array<unsigned char>^ cdb,
            unsigned char dataDirection,
            unsigned int timeoutSeconds);

        static bool IoctlDirect(
            IntPtr deviceHandle,
            IntPtr cdb,
            int cdbLength,
            IntPtr dataBuffer,
            int dataLength,
            IntPtr senseBuffer,
            int senseLength,
            unsigned char dataDirection,
            unsigned int timeoutSeconds,
            [Out] unsigned char% scsiStatus,
            [Out] unsigned int% bytesReturned,
            [Out] int% win32Error);
    };
}
