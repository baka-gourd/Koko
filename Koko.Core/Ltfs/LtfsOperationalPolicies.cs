using System.Text;
using System.Text.Json;
using System.Formats.Tar;
using System.IO.Compression;

using Koko.Core.Events;
using Koko.Core.Scsi.Commands;
using Koko.Core.Scsi.Parsers;

namespace Koko.Core.Ltfs;

public enum LtfsWriterErrorKind
{
    Unknown,
    Transport,
    ScsiCheckCondition,
    EarlyWarningEndOfMedium,
    EndOfMedium,
    VolumeOverflow,
    WriteProtected,
    SourceRead,
    IndexCommit,
    Vci,
    Autosave,
    Encryption
}

public enum LtfsWriterRecoveryAction
{
    Abort,
    Retry,
    Ignore,
    ReloadThenRetry,
    CheckpointThenAbort
}

public sealed record LtfsWriterPolicyContext(
    string OperationId,
    LtfsWriterStepKind Step,
    string Message,
    Exception Exception,
    LtfsWriterErrorKind ErrorKind,
    int Attempt,
    LtfsTapePosition? TapePosition = null);

public sealed record LtfsWriterPolicyDecision(
    LtfsWriterRecoveryAction Action,
    string Reason)
{
    public static LtfsWriterPolicyDecision Abort(string reason) => new(LtfsWriterRecoveryAction.Abort, reason);

    public static LtfsWriterPolicyDecision Retry(string reason) => new(LtfsWriterRecoveryAction.Retry, reason);

    public static LtfsWriterPolicyDecision Ignore(string reason) => new(LtfsWriterRecoveryAction.Ignore, reason);
}

public sealed record LtfsWriterPolicyDecisionEvent(
    string OperationId,
    LtfsWriterStepKind Step,
    LtfsWriterErrorKind ErrorKind,
    LtfsWriterRecoveryAction Action,
    string Reason,
    int Attempt,
    DateTimeOffset? TimestampOverride = null) : IKokoEvent
{
    public DateTimeOffset Timestamp { get; } = TimestampOverride ?? DateTimeOffset.UtcNow;
}

public sealed class LtfsScsiCommandException : Exception
{
    public LtfsScsiCommandException(string message, bool transportOk, ScsiCommandResult result)
        : base(FormatMessage(message, result))
    {
        TransportOk = transportOk;
        Result = result;
        Operation = message;
    }

    public string Operation { get; }

    public bool TransportOk { get; }

    public ScsiCommandResult Result { get; }

    public byte[] SenseData => Result.SenseData;

    public byte SenseKey => SenseData.Length >= 3 ? (byte)(SenseData[2] & 0x0F) : (byte)0;

    public bool Filemark => SenseData.Length >= 3 && (SenseData[2] & 0x80) != 0;

    public bool EndOfMedium => SenseData.Length >= 3 && (SenseData[2] & 0x40) != 0;

    public bool VolumeOverflow => SenseKey == 0x0D;

    public byte AdditionalSenseCode => SenseData.Length >= 13 ? SenseData[12] : (byte)0;

    public byte AdditionalSenseCodeQualifier => SenseData.Length >= 14 ? SenseData[13] : (byte)0;

    public bool EarlyWarningEndOfMedium => EndOfMedium && SenseKey == 0x00 && AdditionalSenseCode == 0x00 && AdditionalSenseCodeQualifier == 0x02;

    private static string FormatMessage(string message, ScsiCommandResult result)
    {
        var transport = result.TransportError is null
            ? string.Empty
            : $"\ntransport error={result.TransportError.ErrorCode}: {result.TransportError.Message}";
        return $"{message} SCSI status=0x{result.ScsiStatus:X2}{transport}\nparsed sense={SenseParser.ParseSense(result.SenseData)}\nsense={Convert.ToHexString(result.SenseData)}.";
    }

    public bool WriteProtected => SenseKey == 0x07;
}

public enum LtfsEncryptionMode
{
    Disabled,
    ReadOnlyKey,
    WriteKeyRequired
}

public sealed record LtfsEncryptionOptions(
    LtfsEncryptionMode Mode = LtfsEncryptionMode.Disabled,
    ILtfsEncryptionKeyProvider? KeyProvider = null,
    string? KeyId = null,
    bool ClearDeviceKeyOnRelease = false);

public sealed record LtfsEncryptionKeyRequest(
    string OperationId,
    LtfsEncryptionMode Mode,
    string? KeyId);

public sealed record LtfsEncryptionKeyMaterial(
    ReadOnlyMemory<byte> Key,
    string? KeyFingerprint = null);

public interface ILtfsEncryptionKeyProvider
{
    ValueTask<LtfsEncryptionKeyMaterial?> ResolveKeyAsync(LtfsEncryptionKeyRequest request, CancellationToken cancellationToken = default);
}

public interface ILtfsEncryptionCapableDevice
{
    ValueTask SetEncryptionAsync(ReadOnlyMemory<byte>? key, CancellationToken cancellationToken = default);
}

public static class LtfsWormDetector
{
    public const ushort VolumeStatisticsWormParameterCode = 0x0081;

    public static bool? TryDetectFromVolumeStatistics(LogSenseResponse response)
    {
        var parameter = response.Parameters.FirstOrDefault(x => x.ParameterCode == VolumeStatisticsWormParameterCode);
        if (parameter.Value.IsEmpty)
            return null;

        return parameter.Value.Span[^1] != 0;
    }
}

public sealed record LtfsEncryptionEvent(
    string OperationId,
    string Message,
    string? KeyFingerprint,
    DateTimeOffset? TimestampOverride = null) : IKokoEvent
{
    public DateTimeOffset Timestamp { get; } = TimestampOverride ?? DateTimeOffset.UtcNow;
}

public sealed record LtfsAutosaveOptions(
    bool Enabled = false,
    string? RootDirectory = null,
    int RetainLastPerVolume = 5,
    bool ExportSchema = true,
    bool ExportLabel = true,
    bool ExportSessionJson = true,
    bool ExportManifestJson = true,
    bool ExportMam = true,
    bool ExportCartridgeMemory = true);

public sealed record LtfsAutosaveExportEvent(
    string OperationId,
    string Reason,
    string Directory,
    IReadOnlyList<string> Artifacts,
    bool Success,
    string? Error = null,
    DateTimeOffset? TimestampOverride = null) : IKokoEvent
{
    public DateTimeOffset Timestamp { get; } = TimestampOverride ?? DateTimeOffset.UtcNow;
}

public interface ILtfsMetadataExportDevice
{
    ValueTask<IReadOnlyList<MamAttribute>> ReadMamAttributesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<MamAttribute>>(Array.Empty<MamAttribute>());
    }

    ValueTask<byte[]?> ReadCartridgeMemoryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<byte[]?>(null);
    }
}

public sealed record LtfsAutosaveRequest(
    string OperationId,
    string Reason,
    LtfsIndex Index,
    LtfsLabel? Label,
    LtfsAutosaveOptions Options,
    IReadOnlyList<LtfsWriteSource>? Sources = null,
    ILtfsMetadataExportDevice? MetadataDevice = null,
    LtfsRemainingManifest? RemainingManifest = null);

public sealed class LtfsAutosaveExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IKokoEventBus eventBus;

    public LtfsAutosaveExporter(IKokoEventBus? eventBus = null)
    {
        this.eventBus = eventBus ?? NullKokoEventBus.Instance;
    }

    public async ValueTask<IReadOnlyList<string>> ExportAsync(LtfsAutosaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = request.Options;
        if (!options.Enabled)
            return Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(options.RootDirectory))
            throw new ArgumentException("Autosave root directory is required when autosave is enabled.", nameof(request));

        var safeVolume = SafeName(request.Label?.VolumeUuid.ToString("D") ?? request.Index.VolumeUuid.ToString("D"));
        var directory = Path.Combine(options.RootDirectory, safeVolume);
        Directory.CreateDirectory(directory);

        var generation = request.Index.GenerationNumber;
        var location = request.Index.Location;
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss.fffffff'Z'");
        var stem = $"LTFSIndex_Autosave_{safeVolume}_GEN{generation}_P{FormatPartition(location.Partition)}_B{location.StartBlock}_{timestamp}";
        var archivePath = Path.Combine(directory, stem + ".tar.zst");
        var partialArchivePath = archivePath + ".partial";
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "KokoLtfsAutosave", Guid.NewGuid().ToString("N"));
        var entries = new List<AutosaveArchiveEntry>();
        var artifacts = new List<string>();

        try
        {
            Directory.CreateDirectory(stagingDirectory);

            if (options.ExportSchema)
            {
                var path = Path.Combine(stagingDirectory, stem + ".schema");
                await WriteTempAsync(path, stream =>
                {
                    LtfsSchemaWriter.Write(stream, request.Index, new LtfsSchemaWriterOptions(LeaveOpen: true));
                    return ValueTask.CompletedTask;
                }, cancellationToken).ConfigureAwait(false);
                entries.Add(new AutosaveArchiveEntry(path, Path.GetFileName(path)));
            }

            if (options.ExportLabel && request.Label is not null)
            {
                var path = Path.Combine(stagingDirectory, stem + ".label");
                await WriteTempAsync(path, stream =>
                {
                    LtfsLabelWriter.Write(stream, request.Label, new LtfsLabelWriterOptions(LeaveOpen: true));
                    return ValueTask.CompletedTask;
                }, cancellationToken).ConfigureAwait(false);
                entries.Add(new AutosaveArchiveEntry(path, Path.GetFileName(path)));
            }

            if (options.ExportSessionJson)
            {
                var path = Path.Combine(stagingDirectory, stem + ".session.json");
                var session = new
                {
                    request.OperationId,
                    request.Reason,
                    ExportedAt = DateTimeOffset.UtcNow,
                    request.Index.GenerationNumber,
                    Location = new { Partition = FormatPartition(location.Partition), location.StartBlock },
                    VolumeUuid = request.Index.VolumeUuid,
                };
                await WriteJsonTempAsync(path, session, cancellationToken).ConfigureAwait(false);
                entries.Add(new AutosaveArchiveEntry(path, Path.GetFileName(path)));
            }

            if (options.ExportManifestJson)
            {
                var path = Path.Combine(stagingDirectory, stem + ".manifest.json");
                var manifest = new
                {
                    Files = request.Sources?.Select(x => new
                    {
                        x.Name,
                        x.Length,
                        x.SourcePath,
                        x.DestinationPath,
                        x.CreationTime,
                        x.ModifyTime,
                        x.AccessTime,
                        x.ReadOnly,
                    }).ToArray() ?? [],
                };
                await WriteJsonTempAsync(path, manifest, cancellationToken).ConfigureAwait(false);
                entries.Add(new AutosaveArchiveEntry(path, Path.GetFileName(path)));
            }

            if (request.RemainingManifest is not null)
            {
                var path = Path.Combine(stagingDirectory, stem + ".remaining.json");
                await WriteJsonTempAsync(path, request.RemainingManifest, cancellationToken).ConfigureAwait(false);
                entries.Add(new AutosaveArchiveEntry(path, Path.GetFileName(path)));
            }

            if (options.ExportMam && request.MetadataDevice is not null)
            {
                var attributes = await request.MetadataDevice.ReadMamAttributesAsync(cancellationToken).ConfigureAwait(false);
                if (attributes.Count > 0)
                {
                    var path = Path.Combine(stagingDirectory, stem + ".mam.json");
                    var mam = attributes.Select(x => new
                    {
                        Id = $"0x{x.Id:X4}",
                        Format = x.Format.ToString(),
                        ValueHex = Convert.ToHexString(x.Value.Span),
                    });
                    await WriteJsonTempAsync(path, mam, cancellationToken).ConfigureAwait(false);
                    entries.Add(new AutosaveArchiveEntry(path, Path.GetFileName(path)));
                }
            }

            if (options.ExportCartridgeMemory && request.MetadataDevice is not null)
            {
                var cm = await request.MetadataDevice.ReadCartridgeMemoryAsync(cancellationToken).ConfigureAwait(false);
                if (cm is { Length: > 0 })
                {
                    var path = Path.Combine(stagingDirectory, stem + ".cm.txt");
                    await WriteTextTempAsync(path, Convert.ToHexString(cm), cancellationToken).ConfigureAwait(false);
                    entries.Add(new AutosaveArchiveEntry(path, Path.GetFileName(path)));
                }
            }

            await WriteTarZstandardArchiveAtomicAsync(partialArchivePath, archivePath, entries, cancellationToken).ConfigureAwait(false);
            artifacts.Add(archivePath);
            PruneOldExports(directory, safeVolume, options.RetainLastPerVolume);
            eventBus.Publish(new LtfsAutosaveExportEvent(request.OperationId, request.Reason, directory, artifacts, Success: true));
            return artifacts;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            eventBus.Publish(new LtfsAutosaveExportEvent(request.OperationId, request.Reason, directory, artifacts, Success: false, ex.Message));
            throw;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private static async ValueTask WriteJsonTempAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await WriteTempAsync(path, async stream =>
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteTextTempAsync(string path, string text, CancellationToken cancellationToken)
    {
        await WriteTempAsync(path, async stream =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteTempAsync(string path, Func<Stream, ValueTask> writeAsync, CancellationToken cancellationToken)
    {
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
        {
            await writeAsync(stream).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask WriteTarZstandardArchiveAtomicAsync(
        string partialArchivePath,
        string archivePath,
        IReadOnlyList<AutosaveArchiveEntry> entries,
        CancellationToken cancellationToken)
    {
        await using (var fileStream = new FileStream(partialArchivePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
        await using (var zstandardStream = new ZstandardStream(fileStream, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            using var tarWriter = new TarWriter(zstandardStream, TarEntryFormat.Pax, leaveOpen: true);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tarWriter.WriteEntry(entry.Path, entry.EntryName);
            }
        }

        if (File.Exists(archivePath))
            File.Delete(archivePath);
        File.Move(partialArchivePath, archivePath);
    }

    private static void PruneOldExports(string directory, string safeVolume, int retainLastPerVolume)
    {
        if (retainLastPerVolume <= 0)
            return;

        var oldArchives = Directory.EnumerateFiles(directory, $"LTFSIndex_Autosave_{safeVolume}_GEN*_P*_B*_*.tar.zst")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(retainLastPerVolume);

        foreach (var archive in oldArchives)
            File.Delete(archive);
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        return builder.ToString();
    }

    private static string FormatPartition(LtfsPartition partition) => partition == LtfsPartition.A ? "a" : "b";

    private sealed record AutosaveArchiveEntry(string Path, string EntryName);
}

public static class LtfsEncryptionPayloadBuilder
{
    public static byte[] BuildSetEncryptionPayload(ReadOnlyMemory<byte>? key)
    {
        var payload = new byte[52];
        payload[1] = 0x10;
        payload[3] = 0x30;
        payload[4] = 0x40;
        payload[18] = 0x00;
        payload[19] = 0x20;

        if (key is { Length: 32 } material)
        {
            payload[5] = 0x34;
            payload[6] = 0x02;
            payload[7] = 0x03;
            payload[8] = 0x01;
            material.Span.CopyTo(payload.AsSpan(20, 32));
            return payload;
        }

        if (key is { Length: > 0 })
            throw new ArgumentException("LTFS encryption key must be exactly 32 bytes.", nameof(key));

        payload[8] = 0x01;
        return payload;
    }
}
