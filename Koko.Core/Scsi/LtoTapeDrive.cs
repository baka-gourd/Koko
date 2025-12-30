using Microsoft.Win32.SafeHandles;

using System.Buffers;
using System.Text;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

using Serilog;

namespace Koko.Core.Scsi;

public sealed class LtoTapeDrive(SafeFileHandle handle) : DriveBase
{
    public string Vendor { get; private set; } = "";
    public string Product { get; private set; } = "";
    public string SerialNumber { get; private set; } = "";

    public static LtoTapeDrive OpenDriveByPath(string path)
    {
        using (Log.PushMethod())
        {
            Log.Debug("Path={Path}", path);
            var handle = PInvoke.CreateFile(path,
                (uint)(GENERIC_ACCESS_RIGHTS.GENERIC_READ | GENERIC_ACCESS_RIGHTS.GENERIC_WRITE),
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
                null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                FILE_FLAGS_AND_ATTRIBUTES.SECURITY_ANONYMOUS,
                null
            );

            return handle.IsInvalid ? throw new Exception("Failed to Open a LTO drive") : new LtoTapeDrive(handle);
        }
    }

    public override int BlockSizeLimit { get; set; } = 512_000;
    public override bool ScsiRead(
        ReadOnlySpan<byte> commandBlock,
        Span<byte> returnBuffer,
        uint timeoutSeconds,
        out byte scsiStatus,
        out uint bytesReturned,
        Span<byte> senseBuffer)
    {
        return IOControl.IOCtlDirect(
            handle,
            commandBlock,
            returnBuffer,
            DataDirection.In,
            senseBuffer,
            timeoutSeconds,
            out scsiStatus,
            out bytesReturned);
    }

    public override bool ScsiCommand(ReadOnlySpan<byte> commandBlock,
        DataDirection dataDirection,
        uint timeout,
        out byte scsiStatus,
        out uint bytesReturned,
        Span<byte> senseBuffer)
    {
        return IOControl.IOCtlDirect(
            handle,
            commandBlock,
            [],
            dataDirection,
            senseBuffer,
            timeout,
            out scsiStatus,
            out bytesReturned);
    }

    public override bool ScsiWrite(
        ReadOnlySpan<byte> commandBlock,
        Span<byte> dataBuffer,
        uint timeoutSeconds,
        out byte scsiStatus,
        out uint bytesReturned,
        Span<byte> senseBuffer)
    {
        return IOControl.IOCtlDirect(
            handle,
            commandBlock,
            dataBuffer,
            DataDirection.Out,
            senseBuffer,
            timeoutSeconds,
            out scsiStatus,
            out bytesReturned);
    }

    public bool GetInquiry(uint timeoutSeconds = 10)
    {
        using (Log.PushMethod())
        {
            Span<byte> sense = stackalloc byte[IOControl.DefaultSenseLength];

            // 1) VPD page 0x80 header: 4 bytes
            // INQUIRY(6): [0]=0x12, [1]=EVPD(1)/CmdDt(0), [2]=PageCode, [3]=Reserved, [4]=AllocLen, [5]=Control
            Span<byte> vpdHdr = stackalloc byte[4];
            if (!TryScsiRead(
                    commandBlock: [0x12, 0x01, 0x80, 0x00, 0x04, 0x00],
                    returnBuffer: vpdHdr,
                    timeoutSeconds: timeoutSeconds,
                    senseBuffer: sense,
                    out var scsiStatus,
                    out var bytesReturned))
            {
                return false;
            }

            if (bytesReturned < 4)
                return false;

            int pageLen = vpdHdr[3];
            int totalLen = pageLen + 4;

            // pageLen==0 => totalLen==4 => 没有有效数据
            if (totalLen <= 4)
                return false;

            // 2) 读完整 VPD 0x80
            byte[]? rented = null;
            var vpdPage = totalLen <= 256
                ? stackalloc byte[256]
                : (rented = ArrayPool<byte>.Shared.Rent(totalLen));

            try
            {
                var vpdSlice = vpdPage[..totalLen];

                if (!TryScsiRead(
                        commandBlock: [0x12, 0x01, 0x80, 0x00, (byte)(totalLen & 0xFF), 0x00],
                        returnBuffer: vpdSlice,
                        timeoutSeconds: timeoutSeconds,
                        senseBuffer: sense,
                        out scsiStatus,
                        out bytesReturned))
                {
                    return false;
                }

                // 有些设备会返回少于申请长度的数据；但至少应包含 header + pageLen
                if (bytesReturned < 4)
                    return false;

                // 以实际返回长度为准，避免越界；同时保证 offset=4 之后才是字符串
                int available = (int)bytesReturned;
                if (available <= 4)
                    return false;

                int revisionLen = Math.Min(pageLen, available - 4);
                SerialNumber = Encoding.ASCII.GetString(vpdSlice.Slice(4, revisionLen)).Trim();

                // 3) 标准 INQUIRY（EVPD=0），读 0x60
                Span<byte> std = stackalloc byte[0x60];
                std.Clear();

                if (!TryScsiRead(
                        commandBlock: [0x12, 0x00, 0x00, 0x00, 0x60, 0x00],
                        returnBuffer: std,
                        timeoutSeconds: timeoutSeconds,
                        senseBuffer: sense,
                        out scsiStatus,
                        out bytesReturned))
                {
                    return false;
                }

                // 标准 INQUIRY：Vendor(8) offset=8；Product(16) offset=16
                if (bytesReturned < 32) // 至少覆盖到 Product 字段末尾(16+16)
                    return false;

                Vendor = Encoding.ASCII.GetString(std.Slice(8, 8)).Trim();
                Product = Encoding.ASCII.GetString(std.Slice(16, 16)).Trim();
                Log.Debug("Drive Vendor={Vendor},Product={Product},Revision={Revision}", Vendor, Product, SerialNumber);
                return true;
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public bool TryScsiRead(
        ReadOnlySpan<byte> commandBlock,
        Span<byte> returnBuffer,
        uint timeoutSeconds,
        Span<byte> senseBuffer,
        out byte scsiStatus,
        out uint bytesReturned)
    {
        return ScsiRead(
            commandBlock: commandBlock,
            returnBuffer: returnBuffer,
            timeoutSeconds: timeoutSeconds,
            out scsiStatus,
            out bytesReturned,
            senseBuffer: senseBuffer);
    }

    protected override void DisposeCore()
    {
        handle.Dispose();
    }
}