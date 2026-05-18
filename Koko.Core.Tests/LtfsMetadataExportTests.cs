using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

using Koko.Core.Ltfs;
using Koko.Core.Scsi;
using Koko.Core.Scsi.Commands;

namespace Koko.Core.Tests;

public sealed class LtfsMetadataExportTests
{
    [Test]
    public async Task Scsi_writer_reads_cartridge_memory_from_lto_buffer_id_0x10()
    {
        var cm = File.ReadAllBytes(TestDataPath("cm.bin"));
        var drive = new ScriptedScsiDrive(
            ScriptedScsiResult.GoodWrite(),
            ScriptedScsiResult.GoodRead(ReadBufferDescriptor(cm.Length)),
            ScriptedScsiResult.GoodRead(cm));

        var result = await new ScsiLtfsWriterDevice(drive).ReadCartridgeMemoryAsync();

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).IsEquivalentTo(cm);
        await Assert.That(drive.ReadCdbs[0][2]).IsEqualTo((byte)0x10);
    }

    [Test]
    public async Task Scsi_writer_falls_back_to_ibm_lto_buffer_id_0x05()
    {
        var cm = File.ReadAllBytes(TestDataPath("cm.bin"));
        var drive = new ScriptedScsiDrive(
            ScriptedScsiResult.GoodWrite(),
            ScriptedScsiResult.GoodRead(ReadBufferDescriptor(4)),
            ScriptedScsiResult.GoodRead([0x01, 0x02, 0x03, 0x04]),
            ScriptedScsiResult.GoodRead(ReadBufferDescriptor(cm.Length)),
            ScriptedScsiResult.GoodRead(cm));

        var result = await new ScsiLtfsWriterDevice(drive).ReadCartridgeMemoryAsync();

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).IsEquivalentTo(cm);
        await Assert.That(drive.ReadCdbs[0][2]).IsEqualTo((byte)0x10);
        await Assert.That(drive.ReadCdbs[2][2]).IsEqualTo((byte)0x05);
    }

    [Test]
    public async Task Scsi_writer_falls_back_to_diagnostic_cm_hex_payload()
    {
        var cm = File.ReadAllBytes(TestDataPath("cm.bin"));
        var drive = new ScriptedScsiDrive(
            ScriptedScsiResult.GoodWrite(),
            ScriptedScsiResult.GoodRead(ReadBufferDescriptor(4)),
            ScriptedScsiResult.GoodRead([0x01, 0x02, 0x03, 0x04]),
            ScriptedScsiResult.GoodRead(ReadBufferDescriptor(4)),
            ScriptedScsiResult.GoodRead([0x05, 0x06, 0x07, 0x08]),
            ScriptedScsiResult.GoodWrite(),
            ScriptedScsiResult.GoodRead(DiagnosticPayload(cm)));

        var result = await new ScsiLtfsWriterDevice(drive).ReadCartridgeMemoryAsync();

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).IsEquivalentTo(cm);
        await Assert.That(drive.WriteCdbs.Last()[0]).IsEqualTo((byte)0x1D);
        await Assert.That(drive.ReadCdbs.Last()[0]).IsEqualTo((byte)0x1C);
        await Assert.That(drive.ReadCdbs.Last()[2]).IsEqualTo((byte)0xB0);
    }

    [Test]
    public async Task Scsi_writer_ignores_write_protected_flush_before_cartridge_memory_read()
    {
        var cm = File.ReadAllBytes(TestDataPath("cm.bin"));
        var drive = new ScriptedScsiDrive(
            ScriptedScsiResult.CheckCondition(Sense(senseKey: 0x07, asc: 0x27, ascq: 0x00)),
            ScriptedScsiResult.GoodRead(ReadBufferDescriptor(cm.Length)),
            ScriptedScsiResult.GoodRead(cm));

        var result = await new ScsiLtfsWriterDevice(drive).ReadCartridgeMemoryAsync();

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).IsEquivalentTo(cm);
        await Assert.That(drive.CommandCdbs[0][0]).IsEqualTo((byte)0x10);
        await Assert.That(drive.ReadCdbs[0][0]).IsEqualTo((byte)0x3C);
    }

    [Test]
    public async Task Scsi_writer_uses_dtd_status_write_protect_bit_to_ignore_flush_failure()
    {
        var cm = File.ReadAllBytes(TestDataPath("cm.bin"));
        var drive = new ScriptedScsiDrive(
            ScriptedScsiResult.CheckCondition(Sense(senseKey: 0x07, asc: 0x00, ascq: 0x00)),
            ScriptedScsiResult.GoodRead(DtdStatusHeader(writeProtected: true)),
            ScriptedScsiResult.GoodRead(DtdStatusPage(writeProtected: true)),
            ScriptedScsiResult.GoodRead(ReadBufferDescriptor(cm.Length)),
            ScriptedScsiResult.GoodRead(cm));

        var result = await new ScsiLtfsWriterDevice(drive).ReadCartridgeMemoryAsync();

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).IsEquivalentTo(cm);
        await Assert.That(drive.ReadCdbs[0][0]).IsEqualTo((byte)0x4D);
        await Assert.That(drive.ReadCdbs[2][0]).IsEqualTo((byte)0x3C);
    }

    [Test]
    public async Task Scsi_writer_does_not_ignore_non_write_protected_flush_failure()
    {
        var drive = new ScriptedScsiDrive(
            ScriptedScsiResult.CheckCondition(Sense(senseKey: 0x05, asc: 0x20, ascq: 0x00)));

        var action = async () => await new ScsiLtfsWriterDevice(drive).ReadCartridgeMemoryAsync();

        await Assert.That(action).ThrowsException();
    }

    [Test]
    public async Task Autosave_exporter_writes_explicit_tar_zstandard_path()
    {
        var temp = Path.Combine(Path.GetTempPath(), "KokoLtfsManualExportTests", Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(temp, "manual-export.tar.zst");
        try
        {
            Directory.CreateDirectory(temp);
            var artifacts = await new LtfsAutosaveExporter().ExportAsync(new LtfsAutosaveRequest(
                OperationId: "test",
                Reason: "manual",
                Index: CreateIndex(),
                Label: CreateLabel(),
                Options: new LtfsAutosaveOptions(Enabled: true, OutputArchivePath: archivePath)));

            await Assert.That(artifacts).IsEquivalentTo([archivePath]);
            await Assert.That(File.Exists(archivePath)).IsTrue();

            var entryNames = new List<string>();
            await using var archiveStream = File.OpenRead(archivePath);
            await using var zstandardStream = new ZstandardStream(archiveStream, CompressionMode.Decompress, leaveOpen: false);
            using var tarReader = new TarReader(zstandardStream, leaveOpen: false);
            while (tarReader.GetNextEntry() is { } entry)
                entryNames.Add(entry.Name);

            await Assert.That(entryNames).Contains("manual-export.schema");
            await Assert.That(entryNames).Contains("manual-export.label");
            await Assert.That(Directory.Exists(Path.Combine(temp, CreateLabel().VolumeUuid.ToString("D")))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    private static byte[] ReadBufferDescriptor(int length)
    {
        return
        [
            0,
            (byte)(length >> 16),
            (byte)(length >> 8),
            (byte)length
        ];
    }

    private static byte[] DiagnosticPayload(byte[] cm)
    {
        var hex = string.Join(" ", cm.Select(x => x.ToString("X2")));
        return [0, 0, 0, 0, 0, 0, .. Encoding.ASCII.GetBytes(hex)];
    }

    private static byte[] DtdStatusHeader(bool writeProtected)
    {
        return DtdStatusPage(writeProtected)[..4];
    }

    private static byte[] DtdStatusPage(bool writeProtected)
    {
        return
        [
            0x11, 0x00, 0x00, 0x05,
            0x00, 0x00, 0x00, 0x01,
            writeProtected ? (byte)0x10 : (byte)0x00
        ];
    }

    private static byte[] Sense(byte senseKey, byte asc, byte ascq)
    {
        var sense = new byte[64];
        sense[0] = 0x70;
        sense[2] = senseKey;
        sense[7] = 0x0A;
        sense[12] = asc;
        sense[13] = ascq;
        return sense;
    }

    private static LtfsIndex CreateIndex()
    {
        var index = new LtfsIndex
        {
            Creator = "Koko.Core.Tests",
            VolumeUuid = Guid.Parse("129fa6c4-b043-4286-9188-0c588a94ad89"),
            GenerationNumber = 1,
            UpdateTime = LtfsIndex.FormatLtfsTime(DateTimeOffset.UnixEpoch),
            Location = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 5 },
            PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 0 },
            HighestFileUid = 1,
        };
        index.RootDirectories.Add(new LtfsDirectory { Name = "VOL", FileUid = 1 });
        return index;
    }

    private static LtfsLabel CreateLabel()
    {
        return new LtfsLabel
        {
            VolumeUuid = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            LocationPartition = LtfsPartition.B,
            IndexPartition = LtfsPartition.A,
            DataPartition = LtfsPartition.B,
            BlockSize = 512 * 1024,
        };
    }

    private static string TestDataPath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Data", fileName);
    }

    private sealed class ScriptedScsiDrive(params ScriptedScsiResult[] results) : IScsiDrive
    {
        private readonly Queue<ScriptedScsiResult> results = new(results);

        public int BlockSizeLimit { get; set; }

        public ScsiTransportError? LastTransportError => null;

        public List<byte[]> ReadCdbs { get; } = [];

        public List<byte[]> WriteCdbs { get; } = [];

        public List<byte[]> CommandCdbs { get; } = [];

        public bool ScsiRead(
            ReadOnlySpan<byte> commandBlock,
            Span<byte> returnBuffer,
            uint timeoutSeconds,
            out byte scsiStatus,
            out uint bytesReturned,
            Span<byte> senseBuffer)
        {
            ReadCdbs.Add(commandBlock.ToArray());
            var result = Dequeue();
            result.Data.Span[..Math.Min(result.Data.Length, returnBuffer.Length)].CopyTo(returnBuffer);
            scsiStatus = result.ScsiStatus;
            bytesReturned = (uint)Math.Min(result.Data.Length, returnBuffer.Length);
            senseBuffer.Clear();
            result.SenseData.Span[..Math.Min(result.SenseData.Length, senseBuffer.Length)].CopyTo(senseBuffer);
            return result.TransportOk;
        }

        public bool ScsiWrite(
            ReadOnlySpan<byte> commandBlock,
            Span<byte> dataBuffer,
            uint timeoutSeconds,
            out byte scsiStatus,
            out uint bytesReturned,
            Span<byte> senseBuffer)
        {
            WriteCdbs.Add(commandBlock.ToArray());
            var result = Dequeue();
            scsiStatus = result.ScsiStatus;
            bytesReturned = 0;
            senseBuffer.Clear();
            result.SenseData.Span[..Math.Min(result.SenseData.Length, senseBuffer.Length)].CopyTo(senseBuffer);
            return result.TransportOk;
        }

        public bool ScsiCommand(
            ReadOnlySpan<byte> commandBlock,
            DataDirection dataDirection,
            uint timeout,
            out byte scsiStatus,
            out uint bytesReturned,
            Span<byte> senseBuffer)
        {
            CommandCdbs.Add(commandBlock.ToArray());
            var result = Dequeue();
            scsiStatus = result.ScsiStatus;
            bytesReturned = 0;
            senseBuffer.Clear();
            result.SenseData.Span[..Math.Min(result.SenseData.Length, senseBuffer.Length)].CopyTo(senseBuffer);
            return result.TransportOk;
        }

        private ScriptedScsiResult Dequeue()
        {
            if (!results.TryDequeue(out var result))
                throw new InvalidOperationException("No scripted SCSI result remains.");
            return result;
        }
    }

    private readonly record struct ScriptedScsiResult(bool TransportOk, byte ScsiStatus, ReadOnlyMemory<byte> Data, ReadOnlyMemory<byte> SenseData)
    {
        public static ScriptedScsiResult GoodWrite() => new(true, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);

        public static ScriptedScsiResult GoodRead(byte[] data) => new(true, 0, data, ReadOnlyMemory<byte>.Empty);

        public static ScriptedScsiResult CheckCondition(byte[] sense) => new(true, 0x02, ReadOnlyMemory<byte>.Empty, sense);
    }
}
