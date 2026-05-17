using Koko.Core.Scsi;
using Koko.Core.Scsi.Commands;

namespace Koko.Core.Tests;

public sealed class ScsiPassThroughTests
{
    [Test]
    public async Task Test_unit_ready_uses_zero_cdb_and_in_direction()
    {
        var drive = new RecordingScsiDrive();

        var ok = TestUnitReadyCommand.TryExecute(drive, new TestUnitReadyCommand(), out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(drive.LastCommandCdb.SequenceEqual(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })).IsTrue();
        await Assert.That(drive.LastCommandDirection).IsEqualTo(DataDirection.In);
    }

    [Test]
    public async Task No_data_commands_use_in_direction_for_ibm_compatibility()
    {
        await AssertNoDataCommandDirection(
            drive => ReserveUnitCommand.TryExecute(drive, new ReserveUnitCommand(Use10Byte: false), out _),
            [0x16, 0x00, 0x00, 0x00, 0x00, 0x00]);

        await AssertNoDataCommandDirection(
            drive => ReleaseUnitCommand.TryExecute(drive, new ReleaseUnitCommand(Use10Byte: false), out _),
            [0x17, 0x00, 0x00, 0x00, 0x00, 0x00]);

        await AssertNoDataCommandDirection(
            drive => PreventAllowMediumRemovalCommand.TryExecute(drive, new PreventAllowMediumRemovalCommand(true), out _),
            [0x1E, 0x00, 0x00, 0x00, 0x01, 0x00]);

        await AssertNoDataCommandDirection(
            drive => RewindCommand.TryExecute(drive, new RewindCommand(Immediate: false), out _),
            [0x01, 0x00, 0x00, 0x00, 0x00, 0x00]);

        await AssertNoDataCommandDirection(
            drive => SpaceCommand.TryExecute(drive, new SpaceCommand(Use16Byte: false, Code: SpaceCode.Filemarks, Count: 1), out _),
            [0x11, 0x01, 0x00, 0x00, 0x01, 0x00]);

        await AssertNoDataCommandDirection(
            drive => FormatMediumCommand.TryExecute(drive, new FormatMediumCommand(Immediate: false, FormatCode: 1), out _),
            [0x04, 0x00, 0x01, 0x00, 0x00, 0x00]);
    }

    [Test]
    public async Task No_data_packet_keeps_null_data_buffer_and_uses_non_direct_layout()
    {
        var snapshot = IOControl.CreatePacketSnapshot(
            [0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            dataTransferLength: 0,
            DataDirection.In,
            timeoutSeconds: 10);

        await Assert.That(snapshot.Opcode).IsEqualTo((byte)0x00);
        await Assert.That(snapshot.Cdb).IsEqualTo("00 00 00 00 00 00");
        await Assert.That(snapshot.CdbLength).IsEqualTo((byte)6);
        await Assert.That(snapshot.DataIn).IsEqualTo((byte)DataDirection.In);
        await Assert.That(snapshot.DataTransferLength).IsEqualTo(0U);
        await Assert.That(snapshot.DataBuffer).IsEqualTo(nint.Zero);
        await Assert.That(snapshot.SenseInfoOffset).IsGreaterThan(0U);
        await Assert.That(snapshot.PacketSize).IsGreaterThanOrEqualTo((int)snapshot.SenseInfoOffset + IOControl.DefaultSenseLength);
    }

    [Test]
    public async Task Power_on_reset_unit_attention_match_requires_exact_sense()
    {
        await Assert.That(ScsiStartupUnitAttentionRetry.IsPowerOnResetUnitAttention(PowerOnResetResult())).IsTrue();
        await Assert.That(ScsiStartupUnitAttentionRetry.IsPowerOnResetUnitAttention(CheckConditionResult(0x06, 0x29, 0x02))).IsFalse();
        await Assert.That(ScsiStartupUnitAttentionRetry.IsPowerOnResetUnitAttention(CheckConditionResult(0x06, 0x29, 0x03))).IsFalse();
        await Assert.That(ScsiStartupUnitAttentionRetry.IsPowerOnResetUnitAttention(CheckConditionResult(0x02, 0x29, 0x01))).IsFalse();
        await Assert.That(ScsiStartupUnitAttentionRetry.IsPowerOnResetUnitAttention(ScsiCommandResult.From(true, 0, 0, new byte[IOControl.DefaultSenseLength]))).IsFalse();
    }

    [Test]
    public async Task Power_on_reset_unit_attention_does_not_retry_without_scope()
    {
        var drive = new ScriptedScsiDrive(
            new ScriptedScsiResult(true, 0x02, PowerOnResetSense()),
            new ScriptedScsiResult(true, 0x00, new byte[IOControl.DefaultSenseLength]));

        var ok = ScsiCommandExecutor.TryExecuteNoData(drive, [0x00, 0x00, 0x00, 0x00, 0x00, 0x00], DataDirection.In, 10, out var result);

        await Assert.That(ok).IsTrue();
        await Assert.That(result.IsGood).IsFalse();
        await Assert.That(drive.CommandCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task Power_on_reset_unit_attention_retries_twice_inside_scope()
    {
        var drive = new ScriptedScsiDrive(
            new ScriptedScsiResult(true, 0x02, PowerOnResetSense()),
            new ScriptedScsiResult(true, 0x02, PowerOnResetSense()),
            new ScriptedScsiResult(true, 0x00, new byte[IOControl.DefaultSenseLength]));

        bool ok;
        ScsiCommandResult result;
        using (ScsiStartupUnitAttentionRetry.SuppressPowerOnReset(scopeName: "test"))
        {
            ok = ScsiCommandExecutor.TryExecuteNoData(drive, [0x00, 0x00, 0x00, 0x00, 0x00, 0x00], DataDirection.In, 10, out result);
        }

        await Assert.That(ok).IsTrue();
        await Assert.That(result.IsGood).IsTrue();
        await Assert.That(drive.CommandCallCount).IsEqualTo(3);
    }

    [Test]
    public async Task Power_on_reset_unit_attention_reports_failure_after_two_retries()
    {
        var drive = new ScriptedScsiDrive(
            new ScriptedScsiResult(true, 0x02, PowerOnResetSense()),
            new ScriptedScsiResult(true, 0x02, PowerOnResetSense()),
            new ScriptedScsiResult(true, 0x02, PowerOnResetSense()),
            new ScriptedScsiResult(true, 0x00, new byte[IOControl.DefaultSenseLength]));

        bool ok;
        ScsiCommandResult result;
        using (ScsiStartupUnitAttentionRetry.SuppressPowerOnReset(scopeName: "test"))
        {
            ok = ScsiCommandExecutor.TryExecuteNoData(drive, [0x00, 0x00, 0x00, 0x00, 0x00, 0x00], DataDirection.In, 10, out result);
        }

        await Assert.That(ok).IsTrue();
        await Assert.That(result.IsGood).IsFalse();
        await Assert.That(drive.CommandCallCount).IsEqualTo(3);
    }

    [Test]
    public async Task Non_power_on_reset_unit_attention_does_not_retry_inside_scope()
    {
        var drive = new ScriptedScsiDrive(
            new ScriptedScsiResult(true, 0x02, Sense(0x06, 0x29, 0x02)),
            new ScriptedScsiResult(true, 0x00, new byte[IOControl.DefaultSenseLength]));

        using (ScsiStartupUnitAttentionRetry.SuppressPowerOnReset(scopeName: "test"))
        {
            var ok = ScsiCommandExecutor.TryExecuteNoData(drive, [0x00, 0x00, 0x00, 0x00, 0x00, 0x00], DataDirection.In, 10, out var result);
            await Assert.That(ok).IsTrue();
            await Assert.That(result.IsGood).IsFalse();
        }

        await Assert.That(drive.CommandCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task Power_on_reset_retry_applies_to_read_commands()
    {
        var drive = new ScriptedScsiDrive(
            new ScriptedScsiResult(true, 0x02, PowerOnResetSense()),
            new ScriptedScsiResult(true, 0x00, new byte[IOControl.DefaultSenseLength], [0xCA, 0xFE]));

        bool ok;
        ScsiCommandResult result;
        byte[] data;
        using (ScsiStartupUnitAttentionRetry.SuppressPowerOnReset(scopeName: "test"))
        {
            ok = ScsiCommandExecutor.TryExecuteRead(drive, [0x12, 0x00, 0x00, 0x00, 0x02, 0x00], 2, 10, out result, out data);
        }

        await Assert.That(ok).IsTrue();
        await Assert.That(result.IsGood).IsTrue();
        await Assert.That(data.SequenceEqual(new byte[] { 0xCA, 0xFE })).IsTrue();
        await Assert.That(drive.ReadCallCount).IsEqualTo(2);
    }

    [Test]
    public async Task Read_command_uses_sense_residual_for_short_incorrect_length_blocks()
    {
        const int allocationLength = 0x80000;
        const int actualLength = 471;
        var payload = new byte[allocationLength];
        payload[0] = (byte)'<';
        payload[actualLength - 1] = (byte)'>';
        var drive = new ScriptedScsiDrive(
            new ScriptedScsiResult(true, 0x02, ShortIncorrectLengthSense(allocationLength - actualLength), payload, BytesReturned: 80));

        var ok = ScsiCommandExecutor.TryExecuteRead(drive, [0x08, 0x00, 0x08, 0x00, 0x00, 0x00], allocationLength, 60, out var result, out var data);

        await Assert.That(ok).IsTrue();
        await Assert.That(result.ScsiStatus).IsEqualTo((byte)0x02);
        await Assert.That(result.BytesReturned).IsEqualTo(80U);
        await Assert.That(data.Length).IsEqualTo(actualLength);
        await Assert.That(data[0]).IsEqualTo((byte)'<');
        await Assert.That(data[^1]).IsEqualTo((byte)'>');
    }

    [Test]
    public async Task Read_command_ignores_ioctl_bytes_returned_for_successful_direct_reads()
    {
        const int allocationLength = 0x80000;
        var payload = new byte[allocationLength];
        payload[0] = (byte)'<';
        payload[255] = (byte)'>';
        var drive = new ScriptedScsiDrive(
            new ScriptedScsiResult(true, 0x00, new byte[IOControl.DefaultSenseLength], payload, BytesReturned: 56));

        var ok = ScsiCommandExecutor.TryExecuteRead(drive, [0x08, 0x00, 0x08, 0x00, 0x00, 0x00], allocationLength, 60, out var result, out var data);

        await Assert.That(ok).IsTrue();
        await Assert.That(result.IsGood).IsTrue();
        await Assert.That(result.BytesReturned).IsEqualTo(56U);
        await Assert.That(data.Length).IsEqualTo(allocationLength);
        await Assert.That(data[0]).IsEqualTo((byte)'<');
        await Assert.That(data[255]).IsEqualTo((byte)'>');
    }

    private static async Task AssertNoDataCommandDirection(
        Func<RecordingScsiDrive, bool> execute,
        byte[] expectedCdb)
    {
        var drive = new RecordingScsiDrive();

        var ok = execute(drive);

        await Assert.That(ok).IsTrue();
        await Assert.That(drive.LastCommandCdb.SequenceEqual(expectedCdb)).IsTrue();
        await Assert.That(drive.LastCommandDirection).IsEqualTo(DataDirection.In);
    }

    private static ScsiCommandResult PowerOnResetResult() => CheckConditionResult(0x06, 0x29, 0x01);

    private static ScsiCommandResult CheckConditionResult(byte senseKey, byte asc, byte ascq) =>
        ScsiCommandResult.From(true, 0x02, 0, Sense(senseKey, asc, ascq));

    private static byte[] PowerOnResetSense() => Sense(0x06, 0x29, 0x01);

    private static byte[] ShortIncorrectLengthSense(int residual)
    {
        var sense = new byte[IOControl.DefaultSenseLength];
        sense[0] = 0xF0;
        sense[2] = 0x20;
        sense[3] = (byte)(residual >> 24);
        sense[4] = (byte)(residual >> 16);
        sense[5] = (byte)(residual >> 8);
        sense[6] = (byte)residual;
        sense[7] = 0x10;
        sense[16] = 0x2C;
        sense[17] = 0x73;
        return sense;
    }

    private static byte[] Sense(byte senseKey, byte asc, byte ascq)
    {
        var sense = new byte[IOControl.DefaultSenseLength];
        sense[0] = 0x70;
        sense[2] = senseKey;
        sense[7] = 0x0A;
        sense[12] = asc;
        sense[13] = ascq;
        return sense;
    }

    private sealed class RecordingScsiDrive : IScsiDrive
    {
        public int BlockSizeLimit { get; set; }

        public ScsiTransportError? LastTransportError => null;

        public byte[] LastCommandCdb { get; private set; } = [];

        public DataDirection LastCommandDirection { get; private set; }

        public bool ScsiRead(
            ReadOnlySpan<byte> commandBlock,
            Span<byte> returnBuffer,
            uint timeoutSeconds,
            out byte scsiStatus,
            out uint bytesReturned,
            Span<byte> senseBuffer)
        {
            throw new NotSupportedException();
        }

        public bool ScsiWrite(
            ReadOnlySpan<byte> commandBlock,
            Span<byte> dataBuffer,
            uint timeoutSeconds,
            out byte scsiStatus,
            out uint bytesReturned,
            Span<byte> senseBuffer)
        {
            throw new NotSupportedException();
        }

        public bool ScsiCommand(
            ReadOnlySpan<byte> commandBlock,
            DataDirection dataDirection,
            uint timeout,
            out byte scsiStatus,
            out uint bytesReturned,
            Span<byte> senseBuffer)
        {
            LastCommandCdb = commandBlock.ToArray();
            LastCommandDirection = dataDirection;
            scsiStatus = 0;
            bytesReturned = 0;
            senseBuffer.Clear();
            return true;
        }
    }

    private sealed class ScriptedScsiDrive : IScsiDrive
    {
        private readonly Queue<ScriptedScsiResult> results;

        public ScriptedScsiDrive(params ScriptedScsiResult[] results)
        {
            this.results = new Queue<ScriptedScsiResult>(results);
        }

        public int BlockSizeLimit { get; set; }

        public ScsiTransportError? LastTransportError => null;

        public int CommandCallCount { get; private set; }

        public int ReadCallCount { get; private set; }

        public bool ScsiRead(
            ReadOnlySpan<byte> commandBlock,
            Span<byte> returnBuffer,
            uint timeoutSeconds,
            out byte scsiStatus,
            out uint bytesReturned,
            Span<byte> senseBuffer)
        {
            ReadCallCount++;
            var result = Dequeue();
            ApplyResult(result, senseBuffer, out scsiStatus, out bytesReturned);
            if (result.Data is not null)
                result.Data.AsSpan(0, Math.Min(result.Data.Length, returnBuffer.Length)).CopyTo(returnBuffer);
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
            var result = Dequeue();
            ApplyResult(result, senseBuffer, out scsiStatus, out bytesReturned);
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
            CommandCallCount++;
            var result = Dequeue();
            ApplyResult(result, senseBuffer, out scsiStatus, out bytesReturned);
            return result.TransportOk;
        }

        private ScriptedScsiResult Dequeue()
        {
            if (results.Count == 0)
                throw new InvalidOperationException("No scripted SCSI result is available.");

            return results.Dequeue();
        }

        private static void ApplyResult(ScriptedScsiResult result, Span<byte> senseBuffer, out byte scsiStatus, out uint bytesReturned)
        {
            senseBuffer.Clear();
            result.Sense.AsSpan(0, Math.Min(result.Sense.Length, senseBuffer.Length)).CopyTo(senseBuffer);
            scsiStatus = result.ScsiStatus;
            bytesReturned = result.BytesReturned != 0 || result.Data is null ? result.BytesReturned : (uint)result.Data.Length;
        }
    }

    private sealed record ScriptedScsiResult(
        bool TransportOk,
        byte ScsiStatus,
        byte[] Sense,
        byte[]? Data = null,
        uint BytesReturned = 0);
}
