using Microsoft.Win32.SafeHandles;

using System.Runtime.InteropServices;

using Serilog;

using Windows.Win32;
using Windows.Win32.Storage.IscsiDisc;

namespace Koko.Core.Scsi;

public enum DataDirection : byte
{
    In = 1,
    Out = 0,
    Unspecified = 2
}

public sealed record ScsiTransportError(int ErrorCode, string Message);

internal sealed record ScsiPassThroughPacketSnapshot(
    byte Opcode,
    string Cdb,
    byte CdbLength,
    byte DataIn,
    uint DataTransferLength,
    nint DataBuffer,
    uint SenseInfoOffset,
    int PacketSize);

public class IOControl
{
    private const uint IoctlScsiPassThrough = 0x4D004;
    private const uint IoctlScsiPassThroughDirect = 0x4D014;

    public const int DefaultSenseLength = 64;
    private const int DefaultCommandBlockLength = 16;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SptdWithSense64
    {
        public SCSI_PASS_THROUGH_DIRECT Sptd;
        public uint Filler;
        public fixed byte Sense[DefaultSenseLength];
    }

    public static unsafe byte[] CreateNoDataPacketBytesForDebug(
        ReadOnlySpan<byte> commandBlock,
        DataDirection dataDirection,
        uint timeoutSeconds)
    {
        if (commandBlock.IsEmpty)
            throw new ArgumentException("CDB cannot be empty.", nameof(commandBlock));

        if (commandBlock.Length > DefaultCommandBlockLength)
            throw new ArgumentOutOfRangeException(nameof(commandBlock), "CDB length must be <= 16.");

        var packet = CreatePacket(commandBlock, 0, dataDirection, timeoutSeconds);
        var bytes = new byte[sizeof(SptdWithSense64)];
        MemoryMarshal.Write(bytes, in packet);
        return bytes;
    }

    internal static unsafe ScsiPassThroughPacketSnapshot CreatePacketSnapshot(
        ReadOnlySpan<byte> commandBlock,
        int dataTransferLength,
        DataDirection dataDirection,
        uint timeoutSeconds)
    {
        var packet = CreatePacket(commandBlock, dataTransferLength, dataDirection, timeoutSeconds);

        return new ScsiPassThroughPacketSnapshot(
            commandBlock[0],
            FormatCdb(commandBlock),
            packet.Sptd.CdbLength,
            packet.Sptd.DataIn,
            packet.Sptd.DataTransferLength,
            (nint)packet.Sptd.DataBuffer,
            packet.Sptd.SenseInfoOffset,
            sizeof(SptdWithSense64));
    }

    internal static bool IOCtlDirect(
        SafeFileHandle deviceHandle,
        ReadOnlySpan<byte> commandBlock,
        Span<byte> dataBuffer,
        DataDirection dataDirection,
        Span<byte> senseBuffer,
        uint timeoutSeconds,
        out byte scsiStatus,
        out uint bytesReturned,
        out ScsiTransportError? transportError)
    {
        unsafe
        {
            scsiStatus = 0;
            bytesReturned = 0;
            transportError = null;

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

            var packet = CreatePacket(commandBlock, dataBuffer.Length, dataDirection, timeoutSeconds);

            Span<byte> io = stackalloc byte[sizeof(SptdWithSense64)];
            MemoryMarshal.Write(io, in packet);

            if (dataBuffer.IsEmpty)
            {
                ref var pkt = ref MemoryMarshal.AsRef<SptdWithSense64>(io);
                pkt.Sptd.DataBuffer = null;
                LogPacket(commandBlock, pkt);

                var ok = PInvoke.DeviceIoControl(
                    deviceHandle,
                    IoctlScsiPassThroughDirect,
                    io,
                    io,
                    out var br,
                    null);

                bytesReturned = br;

                if (!ok)
                {
                    transportError = GetLastTransportError();
                    return false;
                }

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
                    LogPacket(commandBlock, pkt);

                    var ok = PInvoke.DeviceIoControl(
                        deviceHandle,
                        IoctlScsiPassThroughDirect,
                        io,
                        io,
                        out var br,
                        null);

                    bytesReturned = br;

                    if (!ok)
                    {
                        transportError = GetLastTransportError();
                        return false;
                    }

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

    private static unsafe SptdWithSense64 CreatePacket(
        ReadOnlySpan<byte> commandBlock,
        int dataTransferLength,
        DataDirection dataDirection,
        uint timeoutSeconds)
    {
        SptdWithSense64 packet = default;
        packet.Sptd.Length = (ushort)sizeof(SCSI_PASS_THROUGH_DIRECT);
        packet.Sptd.CdbLength = (byte)commandBlock.Length;
        packet.Sptd.SenseInfoLength = DefaultSenseLength;
        packet.Sptd.DataIn = (byte)dataDirection;
        packet.Sptd.DataTransferLength = (uint)dataTransferLength;
        packet.Sptd.TimeOutValue = timeoutSeconds;
        packet.Sptd.SenseInfoOffset =
            (uint)Marshal.OffsetOf<SptdWithSense64>(nameof(SptdWithSense64.Sense));

        for (var i = 0; i < commandBlock.Length; i++)
            packet.Sptd.Cdb[i] = commandBlock[i];

        return packet;
    }

    private static unsafe void LogPacket(ReadOnlySpan<byte> commandBlock, in SptdWithSense64 packet)
    {
        Log.Debug(
            "SCSI_PASS_THROUGH_DIRECT Opcode=0x{Opcode:X2}, CDB={Cdb}, CdbLength={CdbLength}, DataIn={DataIn}, DataTransferLength={DataTransferLength}, DataBuffer=0x{DataBuffer:X}, SenseInfoOffset={SenseInfoOffset}, PacketSize={PacketSize}",
            commandBlock[0],
            FormatCdb(commandBlock),
            packet.Sptd.CdbLength,
            packet.Sptd.DataIn,
            packet.Sptd.DataTransferLength,
            (nint)packet.Sptd.DataBuffer,
            packet.Sptd.SenseInfoOffset,
            sizeof(SptdWithSense64));
    }

    private static string FormatCdb(ReadOnlySpan<byte> commandBlock)
    {
        return string.Join(" ", commandBlock.ToArray().Select(x => x.ToString("X2")));
    }

    private static ScsiTransportError GetLastTransportError()
    {
        var error = Marshal.GetLastPInvokeError();
        return new ScsiTransportError(error, Marshal.GetPInvokeErrorMessage(error));
    }
}
