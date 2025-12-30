using Microsoft.Win32.SafeHandles;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.IscsiDisc;

namespace Koko.Core.Scsi;

public enum DataDirection : byte
{
    In = 1,
    Out = 0,
    Unspecified = 2
}

public class IOControl
{
    private const uint IoctlScsiPassThrough = 0x4D004;
    private const uint IoctlScsiPassThroughDirect = 0x4D014;

    public const int DefaultSenseLength = 64;
    private const int DefaultCommandBlockLength = 16;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct SptdWithSense64
    {
        public SCSI_PASS_THROUGH_DIRECT Sptd;
        public uint Filler;
        public fixed byte Sense[DefaultSenseLength];
    }

    internal static bool IOCtlDirect(
        SafeFileHandle deviceHandle,
        ReadOnlySpan<byte> commandBlock,
        Span<byte> dataBuffer,
        DataDirection dataDirection,
        Span<byte> senseBuffer,
        uint timeoutSeconds,
        out byte scsiStatus,
        out uint bytesReturned)
    {
        unsafe
        {
            scsiStatus = 0;
            bytesReturned = 0;

            if (deviceHandle.IsInvalid)
                throw new ArgumentException("Invalid device handle.", nameof(deviceHandle));

            if (commandBlock.IsEmpty)
                throw new ArgumentException("CDB cannot be empty.", nameof(commandBlock));

            if (commandBlock.Length > DefaultCommandBlockLength)
                throw new ArgumentOutOfRangeException(nameof(commandBlock), "CDB length must be <= 16.");

            if (dataDirection != DataDirection.Out &&
                dataDirection != DataDirection.In &&
                dataDirection != DataDirection.Unspecified)
                throw new ArgumentOutOfRangeException(nameof(dataDirection), "Invalid SCSI data direction.");

            SptdWithSense64 packet = default;
            packet.Sptd.Length = (ushort)sizeof(SCSI_PASS_THROUGH_DIRECT);
            packet.Sptd.CdbLength = (byte)commandBlock.Length;
            packet.Sptd.SenseInfoLength = DefaultSenseLength;
            packet.Sptd.DataIn = (byte)dataDirection;
            packet.Sptd.DataTransferLength = (uint)dataBuffer.Length;
            packet.Sptd.TimeOutValue = timeoutSeconds;
            packet.Sptd.SenseInfoOffset =
                (uint)Marshal.OffsetOf<SptdWithSense64>(nameof(SptdWithSense64.Sense));

            for (var i = 0; i < commandBlock.Length; i++)
                packet.Sptd.Cdb[i] = commandBlock[i];

            Span<byte> io = stackalloc byte[sizeof(SptdWithSense64)];
            MemoryMarshal.Write(io, in packet);

            if (dataBuffer.IsEmpty)
            {
                var pkt = MemoryMarshal.AsRef<SptdWithSense64>(io);
                pkt.Sptd.DataBuffer = null;

                var ok = PInvoke.DeviceIoControl(
                    deviceHandle,
                    IoctlScsiPassThroughDirect,
                    io,
                    io,
                    out var br,
                    null);

                bytesReturned = br;

                if (!ok) return false;

                var outPacket = MemoryMarshal.Read<SptdWithSense64>(io);
                scsiStatus = outPacket.Sptd.ScsiStatus;

                var copyLen = Math.Min(senseBuffer.Length, DefaultSenseLength);
                for (var i = 0; i < copyLen; i++)
                    senseBuffer[i] = outPacket.Sense[i];

                return true;
            }
            else
            {
                fixed (byte* pData = dataBuffer)
                {
                    ref var pkt = ref MemoryMarshal.AsRef<SptdWithSense64>(io);
                    pkt.Sptd.DataBuffer = pData;

                    var ok = PInvoke.DeviceIoControl(
                        deviceHandle,
                        IoctlScsiPassThroughDirect,
                        io,
                        io,
                        out var br,
                        null);

                    bytesReturned = br;

                    if (!ok) return false;

                    var outPacket = MemoryMarshal.Read<SptdWithSense64>(io);
                    scsiStatus = outPacket.Sptd.ScsiStatus;

                    var copyLen = Math.Min(senseBuffer.Length, DefaultSenseLength);
                    for (var i = 0; i < copyLen; i++)
                        senseBuffer[i] = outPacket.Sense[i];

                    return true;
                }
            }
        }
    }
}