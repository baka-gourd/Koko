#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <winioctl.h>
#include <ntddscsi.h>
#include <cstddef>
#include <cstring>

#include "ScsiIoControl.h"

namespace
{
	constexpr int DefaultSenseLength = 64;
	constexpr int DefaultCommandBlockLength = 16;

	struct SptdWithSense64
	{
		SCSI_PASS_THROUGH_DIRECT Sptd;
		unsigned char Sense[DefaultSenseLength];
	};

	struct SptWithSense64
	{
		SCSI_PASS_THROUGH Spt;
		unsigned char Sense[DefaultSenseLength];
	};
}

namespace Koko::Native
{
	int ScsiIoControl::SptdSize::get()
	{
		return sizeof(SCSI_PASS_THROUGH_DIRECT);
	}

	int ScsiIoControl::PacketSize::get()
	{
		return sizeof(SptdWithSense64);
	}

	unsigned int ScsiIoControl::SenseInfoOffset::get()
	{
		return static_cast<unsigned int>(offsetof(SptdWithSense64, Sense));
	}

	int ScsiIoControl::NoDataPacketSize::get()
	{
		return sizeof(SptWithSense64);
	}

	unsigned int ScsiIoControl::NoDataSenseInfoOffset::get()
	{
		return static_cast<unsigned int>(offsetof(SptWithSense64, Sense));
	}

	array<unsigned char>^ ScsiIoControl::CreateNoDataPacket(
		array<unsigned char>^ cdb,
		unsigned char dataDirection,
		unsigned int timeoutSeconds)
	{
		if (cdb == nullptr || cdb->Length <= 0 || cdb->Length > DefaultCommandBlockLength)
			throw gcnew ArgumentOutOfRangeException("cdb", "CDB length must be between 1 and 16.");

		array<unsigned char>^ bytes = gcnew array<unsigned char>(sizeof(SptWithSense64));
		pin_ptr<unsigned char> pBytes = &bytes[0];
		SptWithSense64* packet = reinterpret_cast<SptWithSense64*>(pBytes);
		memset(packet, 0, sizeof(SptWithSense64));

		packet->Spt.Length = static_cast<USHORT>(sizeof(SCSI_PASS_THROUGH));
		packet->Spt.CdbLength = static_cast<UCHAR>(cdb->Length);
		packet->Spt.SenseInfoLength = DefaultSenseLength;
		packet->Spt.DataIn = dataDirection;
		packet->Spt.DataTransferLength = 0;
		packet->Spt.TimeOutValue = timeoutSeconds;
		packet->Spt.DataBufferOffset = 0;
		packet->Spt.SenseInfoOffset = static_cast<ULONG>(offsetof(SptWithSense64, Sense));

		pin_ptr<unsigned char> pCdb = &cdb[0];
		memcpy(packet->Spt.Cdb, pCdb, static_cast<size_t>(cdb->Length));

		return bytes;
	}

	bool ScsiIoControl::IoctlDirect(
		IntPtr deviceHandle,
		IntPtr cdb,
		int cdbLength,
		IntPtr dataBuffer,
		int dataLength,
		IntPtr senseBuffer,
		int senseLength,
		unsigned char dataDirection,
		unsigned int timeoutSeconds,
		unsigned char% scsiStatus,
		unsigned int% bytesReturned,
		int% win32Error)
	{
		scsiStatus = 0;
		bytesReturned = 0;
		win32Error = 0;

		if (deviceHandle == IntPtr::Zero || cdb == IntPtr::Zero || cdbLength <= 0 ||
			cdbLength > DefaultCommandBlockLength || dataLength < 0 || senseLength < 0)
		{
			win32Error = ERROR_INVALID_PARAMETER;
			return false;
		}

		if (dataLength == 0)
		{
			SptWithSense64 packet = {};
			packet.Spt.Length = static_cast<USHORT>(sizeof(SCSI_PASS_THROUGH));
			packet.Spt.CdbLength = static_cast<UCHAR>(cdbLength);
			packet.Spt.SenseInfoLength = DefaultSenseLength;
			packet.Spt.DataIn = dataDirection;
			packet.Spt.DataTransferLength = 0;
			packet.Spt.TimeOutValue = timeoutSeconds;
			packet.Spt.DataBufferOffset = 0;
			packet.Spt.SenseInfoOffset = static_cast<ULONG>(offsetof(SptWithSense64, Sense));

			memcpy(packet.Spt.Cdb, cdb.ToPointer(), static_cast<size_t>(cdbLength));

			DWORD nativeBytesReturned = 0;
			const BOOL ok = ::DeviceIoControl(
				static_cast<HANDLE>(deviceHandle.ToPointer()),
				IOCTL_SCSI_PASS_THROUGH,
				&packet,
				sizeof(packet),
				&packet,
				sizeof(packet),
				&nativeBytesReturned,
				nullptr);

			bytesReturned = nativeBytesReturned;

			if (!ok)
			{
				win32Error = static_cast<int>(::GetLastError());
				return false;
			}

			scsiStatus = packet.Spt.ScsiStatus;

			if (senseBuffer != IntPtr::Zero && senseLength > 0)
			{
				const auto copyLength = senseLength < DefaultSenseLength ? senseLength : DefaultSenseLength;
				memcpy(senseBuffer.ToPointer(), packet.Sense, static_cast<size_t>(copyLength));
			}

			return true;
		}

		SptdWithSense64 packet = {};
		packet.Sptd.Length = static_cast<USHORT>(sizeof(SCSI_PASS_THROUGH_DIRECT));
		packet.Sptd.CdbLength = static_cast<UCHAR>(cdbLength);
		packet.Sptd.SenseInfoLength = DefaultSenseLength;
		packet.Sptd.DataIn = dataDirection;
		packet.Sptd.DataTransferLength = static_cast<ULONG>(dataLength);
		packet.Sptd.TimeOutValue = timeoutSeconds;
		packet.Sptd.DataBuffer = dataLength == 0 ? nullptr : dataBuffer.ToPointer();
		packet.Sptd.SenseInfoOffset = static_cast<ULONG>(offsetof(SptdWithSense64, Sense));

		memcpy(packet.Sptd.Cdb, cdb.ToPointer(), static_cast<size_t>(cdbLength));

		DWORD nativeBytesReturned = 0;
		const BOOL ok = ::DeviceIoControl(
			static_cast<HANDLE>(deviceHandle.ToPointer()),
			IOCTL_SCSI_PASS_THROUGH_DIRECT,
			&packet,
			sizeof(packet),
			&packet,
			sizeof(packet),
			&nativeBytesReturned,
			nullptr);

		bytesReturned = nativeBytesReturned;

		if (!ok)
		{
			win32Error = static_cast<int>(::GetLastError());
			return false;
		}

		scsiStatus = packet.Sptd.ScsiStatus;

		if (senseBuffer != IntPtr::Zero && senseLength > 0)
		{
			const auto copyLength = senseLength < DefaultSenseLength ? senseLength : DefaultSenseLength;
			memcpy(senseBuffer.ToPointer(), packet.Sense, static_cast<size_t>(copyLength));
		}

		return true;
	}
}
