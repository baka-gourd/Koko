using Microsoft.Win32.SafeHandles;

using System.Runtime.InteropServices;

using Serilog;

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
    public const int DefaultSenseLength = 64;
    private const int DefaultCommandBlockLength = 16;

    public static byte[] CreateNoDataPacketBytesForDebug(
        ReadOnlySpan<byte> commandBlock,
        DataDirection dataDirection,
        uint timeoutSeconds)
    {
        ValidateCommandBlock(commandBlock);
        return Koko.Native.ScsiIoControl.CreateNoDataPacket(commandBlock.ToArray(), (byte)dataDirection, timeoutSeconds);
    }

    internal static ScsiPassThroughPacketSnapshot CreatePacketSnapshot(
        ReadOnlySpan<byte> commandBlock,
        int dataTransferLength,
        DataDirection dataDirection,
        uint timeoutSeconds)
    {
        ValidateCommandBlock(commandBlock);
        if (dataTransferLength < 0)
            throw new ArgumentOutOfRangeException(nameof(dataTransferLength), "Data transfer length cannot be negative.");

        return new ScsiPassThroughPacketSnapshot(
            commandBlock[0],
            FormatCdb(commandBlock),
            (byte)commandBlock.Length,
            (byte)dataDirection,
            (uint)dataTransferLength,
            nint.Zero,
            dataTransferLength == 0 ? Koko.Native.ScsiIoControl.NoDataSenseInfoOffset : Koko.Native.ScsiIoControl.SenseInfoOffset,
            dataTransferLength == 0 ? Koko.Native.ScsiIoControl.NoDataPacketSize : Koko.Native.ScsiIoControl.PacketSize);
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
        scsiStatus = 0;
        bytesReturned = 0;
        transportError = null;

        if (deviceHandle.IsInvalid)
            throw new ArgumentException("Invalid device handle.", nameof(deviceHandle));

        ValidateCommandBlock(commandBlock);
        ValidateDataDirection(dataDirection);

        unsafe
        {
            fixed (byte* pCdb = commandBlock)
            fixed (byte* pData = dataBuffer)
            fixed (byte* pSense = senseBuffer)
            {
                var dataPointer = dataBuffer.IsEmpty ? IntPtr.Zero : (IntPtr)pData;
                LogPacket(commandBlock, dataBuffer.Length, dataDirection, dataPointer);

                var handleRefAdded = false;
                try
                {
                    deviceHandle.DangerousAddRef(ref handleRefAdded);

                    var ok = Koko.Native.ScsiIoControl.IoctlDirect(
                        deviceHandle.DangerousGetHandle(),
                        (IntPtr)pCdb,
                        commandBlock.Length,
                        dataPointer,
                        dataBuffer.Length,
                        senseBuffer.IsEmpty ? IntPtr.Zero : (IntPtr)pSense,
                        senseBuffer.Length,
                        (byte)dataDirection,
                        timeoutSeconds,
                        out scsiStatus,
                        out bytesReturned,
                        out var win32Error);

                    if (!ok)
                    {
                        transportError = new ScsiTransportError(win32Error, Marshal.GetPInvokeErrorMessage(win32Error));
                        return false;
                    }

                    return true;
                }
                finally
                {
                    if (handleRefAdded)
                        deviceHandle.DangerousRelease();
                }
            }
        }
    }

    private static void ValidateCommandBlock(ReadOnlySpan<byte> commandBlock)
    {
        if (commandBlock.IsEmpty)
            throw new ArgumentException("CDB cannot be empty.", nameof(commandBlock));

        if (commandBlock.Length > DefaultCommandBlockLength)
            throw new ArgumentOutOfRangeException(nameof(commandBlock), "CDB length must be <= 16.");
    }

    private static void ValidateDataDirection(DataDirection dataDirection)
    {
        if (dataDirection != DataDirection.Out &&
            dataDirection != DataDirection.In &&
            dataDirection != DataDirection.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(dataDirection), "Invalid SCSI data direction.");
    }

    private static void LogPacket(
        ReadOnlySpan<byte> commandBlock,
        int dataTransferLength,
        DataDirection dataDirection,
        IntPtr dataBuffer)
    {
        Log.Debug(
            "SCSI_PASS_THROUGH Opcode=0x{Opcode:X2}, CDB={Cdb}, CdbLength={CdbLength}, DataIn={DataIn}, DataTransferLength={DataTransferLength}, DataBuffer=0x{DataBuffer:X}, SenseInfoOffset={SenseInfoOffset}, PacketSize={PacketSize}",
            commandBlock[0],
            FormatCdb(commandBlock),
            commandBlock.Length,
            (byte)dataDirection,
            dataTransferLength,
            dataBuffer,
            dataTransferLength == 0 ? Koko.Native.ScsiIoControl.NoDataSenseInfoOffset : Koko.Native.ScsiIoControl.SenseInfoOffset,
            dataTransferLength == 0 ? Koko.Native.ScsiIoControl.NoDataPacketSize : Koko.Native.ScsiIoControl.PacketSize);
    }

    private static string FormatCdb(ReadOnlySpan<byte> commandBlock)
    {
        return string.Join(" ", commandBlock.ToArray().Select(x => x.ToString("X2")));
    }
}
