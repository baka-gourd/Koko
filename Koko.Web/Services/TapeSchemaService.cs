using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;

using Koko.Core.Ltfs;
using Koko.Web.Data;

using Microsoft.EntityFrameworkCore;

namespace Koko.Web.Services;

public sealed class TapeSchemaService
{
    private readonly IDbContextFactory<TapeMetaDbContext> dbFactory;
    private readonly ConcurrentDictionary<string, Lazy<Task<LtfsIndex>>> cache = new(StringComparer.OrdinalIgnoreCase);

    public TapeSchemaService(IDbContextFactory<TapeMetaDbContext> dbFactory)
    {
        this.dbFactory = dbFactory;
    }

    public async Task<TapeSchemaFileListDto> GetAllFilesAsync(string archiveXxHash128, CancellationToken cancellationToken = default)
    {
        var normalizedHash = NormalizeHash(archiveXxHash128);
        var index = await GetIndexAsync(normalizedHash, cancellationToken).ConfigureAwait(false);
        var files = EnumerateFiles(index)
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.File.FileUid)
            .Select(x => ToListDto(x.Path, x.DirectoryPath, x.File))
            .ToArray();

        return new TapeSchemaFileListDto(normalizedHash, index.VolumeUuid, index.GenerationNumber, files.Length, files);
    }

    public async Task<TapeSchemaFileListDto> GetDirectoryFilesAsync(string archiveXxHash128, string? directoryPath, CancellationToken cancellationToken = default)
    {
        var normalizedHash = NormalizeHash(archiveXxHash128);
        var normalizedDirectory = NormalizeLtfsPath(directoryPath);
        var index = await GetIndexAsync(normalizedHash, cancellationToken).ConfigureAwait(false);
        var files = EnumerateFiles(index)
            .Where(x => string.Equals(x.DirectoryPath, normalizedDirectory, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.File.FileUid)
            .Select(x => ToDto(x.Path, x.DirectoryPath, x.File))
            .ToArray();

        return new TapeSchemaFileListDto(normalizedHash, index.VolumeUuid, index.GenerationNumber, files.Length, files);
    }

    public async Task<TapeSchemaFileDto?> GetFileAsync(string archiveXxHash128, string filePath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeLtfsPath(filePath);
        if (normalizedPath.Length == 0)
            return null;

        var index = await GetIndexAsync(NormalizeHash(archiveXxHash128), cancellationToken).ConfigureAwait(false);
        var match = EnumerateFiles(index)
            .FirstOrDefault(x => string.Equals(x.Path, normalizedPath, StringComparison.OrdinalIgnoreCase));

        return match.File is null ? null : ToDto(match.Path, match.DirectoryPath, match.File);
    }

    private async Task<LtfsIndex> GetIndexAsync(string archiveXxHash128, CancellationToken cancellationToken)
    {
        var archivePath = await GetArchivePathAsync(archiveXxHash128, cancellationToken).ConfigureAwait(false);
        var lazy = cache.GetOrAdd(archiveXxHash128, hash => new Lazy<Task<LtfsIndex>>(() => ReadSchemaAsync(hash, archivePath, cancellationToken)));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            cache.TryRemove(archiveXxHash128, out _);
            throw;
        }
    }

    private async Task<string> GetArchivePathAsync(string archiveXxHash128, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var archive = await db.TapeArchives
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.ArchiveXxHash128 == archiveXxHash128, cancellationToken)
            .ConfigureAwait(false);

        if (archive is null)
            throw new InvalidOperationException($"Tape archive hash was not found: {archiveXxHash128}.");
        if (archive.Missing)
            throw new InvalidOperationException($"Tape archive is marked missing: {archiveXxHash128}.");
        if (!File.Exists(archive.ArchivePath))
            throw new FileNotFoundException($"Tape archive file was not found: {archive.ArchivePath}", archive.ArchivePath);

        return archive.ArchivePath;
    }

    private static async Task<LtfsIndex> ReadSchemaAsync(string archiveXxHash128, string archivePath, CancellationToken cancellationToken)
    {
        await using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        await using var zstandardStream = new ZstandardStream(fileStream, CompressionMode.Decompress, leaveOpen: false);
        using var tarReader = new TarReader(zstandardStream, leaveOpen: false);

        while (tarReader.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.DataStream is null || !entry.Name.EndsWith(".schema", StringComparison.OrdinalIgnoreCase))
                continue;

            return LtfsSchemaReader.Read(entry.DataStream);
        }

        throw new InvalidDataException($"Tape archive does not contain an LTFS .schema entry: {archiveXxHash128}.");
    }

    private static IEnumerable<(string Path, string DirectoryPath, LtfsFile File)> EnumerateFiles(LtfsIndex index)
    {
        foreach (var file in index.RootFiles)
            yield return (NormalizeLtfsPath(file.Name), string.Empty, file);

        foreach (var directory in index.RootDirectories)
        {
            foreach (var item in EnumerateDirectoryFiles(directory, NormalizeLtfsPath(directory.Name)))
                yield return item;
        }
    }

    private static IEnumerable<(string Path, string DirectoryPath, LtfsFile File)> EnumerateDirectoryFiles(LtfsDirectory directory, string directoryPath)
    {
        foreach (var file in directory.Files)
        {
            var path = CombinePath(directoryPath, file.Name);
            yield return (path, directoryPath, file);
        }

        foreach (var child in directory.Directories)
        {
            var childPath = CombinePath(directoryPath, child.Name);
            foreach (var item in EnumerateDirectoryFiles(child, childPath))
                yield return item;
        }
    }

    private static TapeSchemaFileDto ToDto(string path, string directoryPath, LtfsFile file)
        => new(
            path,
            directoryPath,
            file.Name,
            file.Length,
            file.ReadOnly,
            file.OpenForWrite,
            file.CreationTime,
            file.ChangeTime,
            file.ModifyTime,
            file.AccessTime,
            file.BackupTime,
            file.FileUid,
            file.Symlink,
            file.ExtendedAttributes
                .Select(x => new TapeSchemaExtendedAttributeDto(x.Key, x.Value))
                .ToArray(),
            file.Extents
                .Select(x => new TapeSchemaExtentDto(x.FileOffset, x.Partition.ToString(), x.StartBlock, x.ByteOffset, x.ByteCount))
                .ToArray());

    private static TapeSchemaFileDto ToListDto(string path, string directoryPath, LtfsFile file)
        => new(
            path,
            directoryPath,
            file.Name,
            0,
            false,
            false,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            file.FileUid,
            null,
            [],
            []);

    private static string NormalizeHash(string archiveXxHash128)
    {
        var normalized = archiveXxHash128.Trim().ToUpperInvariant();
        if (normalized.Length == 0)
            throw new ArgumentException("Archive hash is required.", nameof(archiveXxHash128));

        return normalized;
    }

    private static string NormalizeLtfsPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
            return string.Empty;

        return path.Replace('\\', '/').Trim('/');
    }

    private static string CombinePath(string directoryPath, string name)
        => directoryPath.Length == 0 ? NormalizeLtfsPath(name) : directoryPath + "/" + NormalizeLtfsPath(name);
}
