using System.Runtime.InteropServices;
using Koko.Core.Ltfs;
using Koko.Core.Scsi.Commands;

using Microsoft.Win32.SafeHandles;

using Serilog;

using System.Text;

namespace Koko.Core.Scsi;

public sealed class LtoTapeDrive(SafeFileHandle handle) : DriveBase, ILtfsWriterDevice, ILtfsFormatDevice, ILtfsEncryptionCapableDevice, ILtfsMetadataExportDevice, ILtfsPartitionMamDevice, ILtfsWormDetectionDevice, ILtfsModeSenseDevice
{
    // Cannot use Lock, because inaccessible
    private readonly object _scsiGate = new();
    private ScsiLtfsWriterDevice? writerDevice;
    private ScsiLtfsFormatDevice? formatDevice;

    public string Vendor { get; private set; } = "";
    public string Product { get; private set; } = "";
    public string SerialNumber { get; private set; } = "";

    public static LtoTapeDrive OpenDriveByPath(string path)
    {
        using (Log.PushMethod())
        {
            Log.Debug("Path={Path}", path);
            var handle = Koko.Native.NativeFile.OpenExistingReadWrite(path, out var error);

            if (!handle.IsInvalid)
                return new LtoTapeDrive(handle);

            throw new InvalidOperationException($"Failed to open LTO drive '{path}'. Win32 error {error}: {Marshal.GetPInvokeErrorMessage(error)}");
        }
    }

    public override int BlockSizeLimit { get; set; } = 512_000;

    private ScsiLtfsWriterDevice WriterDevice => writerDevice ??= new ScsiLtfsWriterDevice(this);

    private ScsiLtfsFormatDevice FormatDevice => formatDevice ??= new ScsiLtfsFormatDevice(this);

    public override bool ScsiRead(
        ReadOnlySpan<byte> commandBlock,
        Span<byte> returnBuffer,
        uint timeoutSeconds,
        out byte scsiStatus,
        out uint bytesReturned,
        Span<byte> senseBuffer)
    {
        lock (_scsiGate)
        {
            var ok = IOControl.IOCtlDirect(
                handle,
                commandBlock,
                returnBuffer,
                DataDirection.In,
                senseBuffer,
                timeoutSeconds,
                out scsiStatus,
                out bytesReturned,
                out var transportError);
            LastTransportError = transportError;
            return ok;
        }
    }

    public override bool ScsiCommand(ReadOnlySpan<byte> commandBlock,
        DataDirection dataDirection,
        uint timeout,
        out byte scsiStatus,
        out uint bytesReturned,
        Span<byte> senseBuffer)
    {
        lock (_scsiGate)
        {
            var ok = IOControl.IOCtlDirect(
                handle,
                commandBlock,
                [],
                dataDirection,
                senseBuffer,
                timeout,
                out scsiStatus,
                out bytesReturned,
                out var transportError);
            LastTransportError = transportError;
            return ok;
        }
    }

    public override bool ScsiWrite(
        ReadOnlySpan<byte> commandBlock,
        Span<byte> dataBuffer,
        uint timeoutSeconds,
        out byte scsiStatus,
        out uint bytesReturned,
        Span<byte> senseBuffer)
    {
        lock (_scsiGate)
        {
            var ok = IOControl.IOCtlDirect(
                handle,
                commandBlock,
                dataBuffer,
                DataDirection.Out,
                senseBuffer,
                timeoutSeconds,
                out scsiStatus,
                out bytesReturned,
                out var transportError);
            LastTransportError = transportError;
            return ok;
        }
    }

    public ValueTask ReserveAsync(CancellationToken cancellationToken = default) => WriterDevice.ReserveAsync(cancellationToken);

    public ValueTask ReleaseAsync(CancellationToken cancellationToken = default) => WriterDevice.ReleaseAsync(cancellationToken);

    public ValueTask PreventRemovalAsync(bool prevent, CancellationToken cancellationToken = default) => WriterDevice.PreventRemovalAsync(prevent, cancellationToken);

    public ValueTask TestUnitReadyAsync(CancellationToken cancellationToken = default) => WriterDevice.TestUnitReadyAsync(cancellationToken);

    public ValueTask<long> ReadMaximumBlockSizeAsync(CancellationToken cancellationToken = default) => FormatDevice.ReadMaximumBlockSizeAsync(cancellationToken);

    public ValueTask<byte> ReadMaximumExtraPartitionCountAsync(CancellationToken cancellationToken = default) => FormatDevice.ReadMaximumExtraPartitionCountAsync(cancellationToken);

    public ValueTask SetCapacityAsync(ushort capacity, CancellationToken cancellationToken = default) => FormatDevice.SetCapacityAsync(capacity, cancellationToken);

    public ValueTask ConfigureTwoPartitionAsync(ushort p0Size, ushort p1Size, CancellationToken cancellationToken = default) => FormatDevice.ConfigureTwoPartitionAsync(p0Size, p1Size, cancellationToken);

    public ValueTask FormatMediumAsync(byte formatCode, CancellationToken cancellationToken = default) => FormatDevice.FormatMediumAsync(formatCode, cancellationToken);

    public ValueTask SetBlockSizeAsync(long blockSizeBytes, CancellationToken cancellationToken = default) => WriterDevice.SetBlockSizeAsync(blockSizeBytes, cancellationToken);

    public ValueTask LocateAsync(LtfsPartition partition, ulong block, CancellationToken cancellationToken = default) => WriterDevice.LocateAsync(partition, block, cancellationToken);

    ValueTask ILtfsBlockReader.LocateAsync(LtfsPartition partition, long block, CancellationToken cancellationToken)
    {
        if (block < 0)
            throw new ArgumentOutOfRangeException(nameof(block));
        return LocateAsync(partition, checked((ulong)block), cancellationToken);
    }

    public ValueTask LocateEndOfDataAsync(LtfsPartition partition, CancellationToken cancellationToken = default) => WriterDevice.LocateEndOfDataAsync(partition, cancellationToken);

    public ValueTask LocateFilemarkAsync(LtfsPartition partition, ulong filemark, CancellationToken cancellationToken = default) => WriterDevice.LocateFilemarkAsync(partition, filemark, cancellationToken);

    public ValueTask<LtfsTapePosition> ReadPositionAsync(CancellationToken cancellationToken = default) => WriterDevice.ReadPositionAsync(cancellationToken);

    public ValueTask<byte[]> ReadBlockAsync(long maximumBytes, CancellationToken cancellationToken = default) => WriterDevice.ReadBlockAsync(maximumBytes, cancellationToken);

    public ValueTask AdvancePastFilemarkAsync(CancellationToken cancellationToken = default) => WriterDevice.AdvancePastFilemarkAsync(cancellationToken);

    public ValueTask<int> ReadBlockAsync(LtfsPartition partition, long block, Memory<byte> buffer, CancellationToken cancellationToken = default) => WriterDevice.ReadBlockAsync(partition, block, buffer, cancellationToken);

    public ValueTask<byte[]> ReadToFilemarkAsync(long blockSizeBytes, CancellationToken cancellationToken = default) => WriterDevice.ReadToFilemarkAsync(blockSizeBytes, cancellationToken);

    public ValueTask WriteBlockAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => WriterDevice.WriteBlockAsync(data, cancellationToken);

    public ValueTask WriteFilemarksAsync(uint count, CancellationToken cancellationToken = default) => WriterDevice.WriteFilemarksAsync(count, cancellationToken);

    public ValueTask WriteMamAttributesAsync(LtfsPartition partition, IReadOnlyList<MamAttribute> attributes, CancellationToken cancellationToken = default) => FormatDevice.WriteMamAttributesAsync(partition, attributes, cancellationToken);

    public ValueTask WriteVciAsync(ulong generation, ulong? indexPartitionBlock, ulong dataPartitionBlock, Guid volumeUuid, CancellationToken cancellationToken = default) => WriterDevice.WriteVciAsync(generation, indexPartitionBlock, dataPartitionBlock, volumeUuid, cancellationToken);

    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => WriterDevice.FlushAsync(cancellationToken);

    public ValueTask LoadUnloadAsync(bool load, CancellationToken cancellationToken = default) => WriterDevice.LoadUnloadAsync(load, cancellationToken);

    public ValueTask<LogSenseResponse> ReadLogSenseAsync(LogPageCode pageCode, CancellationToken cancellationToken = default) => WriterDevice.ReadLogSenseAsync(pageCode, cancellationToken);

    public ValueTask SetEncryptionAsync(ReadOnlyMemory<byte>? key, CancellationToken cancellationToken = default) => WriterDevice.SetEncryptionAsync(key, cancellationToken);

    public ValueTask<IReadOnlyList<MamAttribute>> ReadMamAttributesAsync(CancellationToken cancellationToken = default) => WriterDevice.ReadMamAttributesAsync(cancellationToken);

    public ValueTask<IReadOnlyList<MamAttribute>> ReadMamAttributesAsync(LtfsPartition partition, CancellationToken cancellationToken = default) => WriterDevice.ReadMamAttributesAsync(partition, cancellationToken);

    public ValueTask<LtfsPartitionModeSense> ReadPartitionModeSenseAsync(CancellationToken cancellationToken = default) => FormatDevice.ReadPartitionModeSenseAsync(cancellationToken);

    public bool GetInquiry(uint timeoutSeconds = 10)
    {
        using (Log.PushMethod())
        {
            // 1) VPD page 0x80 header: 4 bytes
            var headerRequest = new InquiryCommand(
                EnableVitalProductData: true,
                PageCode: 0x80,
                AllocationLength: 4,
                TimeoutSeconds: timeoutSeconds);

            if (!InquiryCommand.TryExecute(this, headerRequest, out _, out var vpdHdr))
                return false;

            if (vpdHdr.Length < 4)
                return false;

            int pageLen = vpdHdr[3];
            int totalLen = pageLen + 4;

            // pageLen==0 => totalLen==4 => 没有有效数据
            if (totalLen <= 4)
                return false;

            // 2) 读完整 VPD 0x80
            var pageRequest = new InquiryCommand(
                EnableVitalProductData: true,
                PageCode: 0x80,
                AllocationLength: (ushort)totalLen,
                TimeoutSeconds: timeoutSeconds);

            if (!InquiryCommand.TryExecute(this, pageRequest, out _, out var vpdPage))
                return false;

            // 有些设备会返回少于申请长度的数据；但至少应包含 header + pageLen
            if (vpdPage.Length < 4)
                return false;

            // 以实际返回长度为准，避免越界；同时保证 offset=4 之后才是字符串
            int available = vpdPage.Length;
            if (available <= 4)
                return false;

            int revisionLen = Math.Min(pageLen, available - 4);
            SerialNumber = Encoding.ASCII.GetString(vpdPage.AsSpan(4, revisionLen)).Trim();

            // 3) 标准 INQUIRY（EVPD=0），读 0x60
            var stdRequest = new InquiryCommand(
                EnableVitalProductData: false,
                PageCode: 0x00,
                AllocationLength: 0x60,
                TimeoutSeconds: timeoutSeconds);

            if (!InquiryCommand.TryExecute(this, stdRequest, out _, out var std))
                return false;

            // 标准 INQUIRY：Vendor(8) offset=8；Product(16) offset=16
            if (std.Length < 32) // 至少覆盖到 Product 字段末尾(16+16)
                return false;

            Vendor = Encoding.ASCII.GetString(std.AsSpan(8, 8)).Trim();
            Product = Encoding.ASCII.GetString(std.AsSpan(16, 16)).Trim();
            Log.Debug("Drive Vendor={Vendor},Product={Product},Revision={Revision}", Vendor, Product, SerialNumber);
            return true;
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
