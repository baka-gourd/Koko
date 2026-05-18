using System.Formats.Tar;
using System.IO.Compression;
using System.IO.Hashing;
using System.Threading.Channels;

using Koko.Core.Ltfs;
using Koko.Core.Scsi.Parsers;
using Koko.Web.Data;
using Koko.Web.Hubs;
using Koko.Web.Storage;

using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Koko.Web.Services;

public sealed class TapeMetadataIndexService : BackgroundService
{
    private static readonly TimeSpan WatchDebounce = TimeSpan.FromMilliseconds(750);

    private readonly IDbContextFactory<TapeMetaDbContext> dbFactory;
    private readonly KokoStoragePaths paths;
    private readonly IHubContext<KokoHub> hubContext;
    private readonly ILogger<TapeMetadataIndexService> logger;
    private readonly Channel<TapeMetadataWorkItem> workQueue = Channel.CreateUnbounded<TapeMetadataWorkItem>();
    private FileSystemWatcher? watcher;

    public TapeMetadataIndexService(
        IDbContextFactory<TapeMetaDbContext> dbFactory,
        KokoStoragePaths paths,
        IHubContext<KokoHub> hubContext,
        ILogger<TapeMetadataIndexService> logger)
    {
        this.dbFactory = dbFactory;
        this.paths = paths;
        this.hubContext = hubContext;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        paths.EnsureDirectories();
        await EnsureTapeMetaDatabaseAsync(stoppingToken).ConfigureAwait(false);

        await QueueFullScanAsync(stoppingToken).ConfigureAwait(false);
        StartWatcher();

        await foreach (var item in workQueue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                if (item.Kind != TapeMetadataWorkKind.FullScan)
                    await Task.Delay(WatchDebounce, stoppingToken).ConfigureAwait(false);

                await ProcessWorkItemAsync(item, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tape metadata indexing work item failed. Kind={Kind}, Path={Path}", item.Kind, item.Path);
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        watcher?.Dispose();
        watcher = null;
        return base.StopAsync(cancellationToken);
    }

    public async Task<TapeMetadataOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var archives = db.TapeArchives.AsNoTracking();
        var tapeCount = await archives.Select(x => x.Barcode).Distinct().CountAsync(cancellationToken).ConfigureAwait(false);
        var archiveCount = await archives.CountAsync(cancellationToken).ConfigureAwait(false);
        var missingCount = await archives.CountAsync(x => x.Missing, cancellationToken).ConfigureAwait(false);
        var lastIndexed = await archives
            .Select(x => x.IndexedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new TapeMetadataOverviewDto(
            tapeCount,
            archiveCount,
            missingCount,
            lastIndexed.Count == 0 ? null : lastIndexed.Max());
    }

    public async Task<TapeMetadataQueryResultDto> QueryAsync(TapeMetadataQueryDto? query, CancellationToken cancellationToken = default)
    {
        query ??= new TapeMetadataQueryDto();
        var take = Math.Clamp(query.Take, 1, 1000);
        var skip = Math.Max(0, query.Skip);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var source = ApplyQuery(db.TapeArchives.AsNoTracking(), query.Search, query.IncludeMissing, query.Barcode);

        var total = await source.CountAsync(cancellationToken).ConfigureAwait(false);
        var filteredRows = await source
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = filteredRows
            .OrderBy(x => x.Barcode, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(x => x.GenerationNumber ?? long.MinValue)
            .ThenByDescending(x => x.ArchiveLastWriteTimeUtc)
            .Skip(skip)
            .Take(take)
            .ToArray();

        return new TapeMetadataQueryResultDto(total, rows.Select(ToDto).ToArray());
    }

    public async Task<TapeMetadataBarcodeGroupResultDto> GetBarcodeGroupsAsync(TapeMetadataBarcodeGroupQueryDto? query, CancellationToken cancellationToken = default)
    {
        query ??= new TapeMetadataBarcodeGroupQueryDto();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ApplyQuery(db.TapeArchives.AsNoTracking(), query.Search, query.IncludeMissing, barcode: null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var groups = rows
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Barcode) ? "Unknown" : x.Barcode, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var sorted = SortArchives(group).ToArray();
                return new TapeMetadataBarcodeGroupDto(group.Key, sorted.Length, ToDto(sorted[0]));
            })
            .OrderBy(x => x.Barcode, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new TapeMetadataBarcodeGroupResultDto(groups.Length, groups);
    }

    public async Task<TapeMetadataQueryResultDto> GetArchivesByBarcodeAsync(string barcode, TapeMetadataQueryDto? query, CancellationToken cancellationToken = default)
    {
        query ??= new TapeMetadataQueryDto();
        var take = Math.Clamp(query.Take, 1, 1000);
        var skip = Math.Max(0, query.Skip);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var source = ApplyQuery(db.TapeArchives.AsNoTracking(), query.Search, query.IncludeMissing, barcode);
        var total = await source.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await source
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var items = SortArchives(rows)
            .Skip(skip)
            .Take(take)
            .Select(ToDto)
            .ToArray();

        return new TapeMetadataQueryResultDto(total, items);
    }

    public async Task QueueFullScanAsync(CancellationToken cancellationToken = default)
    {
        await workQueue.Writer.WriteAsync(new TapeMetadataWorkItem(TapeMetadataWorkKind.FullScan, null), cancellationToken).ConfigureAwait(false);
    }

    public async Task<TapeMetadataPruneResultDto> PruneAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.TapeArchives.ToListAsync(cancellationToken).ConfigureAwait(false);
        var missing = rows.Where(x => x.Missing || !File.Exists(x.ArchivePath)).ToArray();
        db.TapeArchives.RemoveRange(missing);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await PublishAsync("metadata.prune.completed", "Success", $"Pruned {missing.Length} missing tape metadata records.", null, cancellationToken).ConfigureAwait(false);
        return new TapeMetadataPruneResultDto(missing.Length);
    }

    private async Task ProcessWorkItemAsync(TapeMetadataWorkItem item, CancellationToken cancellationToken)
    {
        switch (item.Kind)
        {
            case TapeMetadataWorkKind.FullScan:
                await FullScanAsync(cancellationToken).ConfigureAwait(false);
                break;
            case TapeMetadataWorkKind.Upsert when item.Path is not null:
                await IndexArchiveAsync(item.Path, cancellationToken).ConfigureAwait(false);
                break;
            case TapeMetadataWorkKind.MarkMissing when item.Path is not null:
                await MarkMissingAsync(item.Path, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task FullScanAsync(CancellationToken cancellationToken)
    {
        await PublishAsync("metadata.scan.started", "Info", "Tape metadata scan started.", null, cancellationToken).ConfigureAwait(false);
        var observedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in Directory.EnumerateFiles(paths.TapeDataDirectory, "*.tar.zst", SearchOption.AllDirectories))
        {
            if (archive.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                continue;

            var hash = await IndexArchiveAsync(archive, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(hash))
                observedHashes.Add(hash);
        }

        await MarkMissingArchivesAsync(observedHashes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> IndexArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        if (!IsTapeArchivePath(archivePath) || !File.Exists(archivePath))
            return null;

        var fullPath = Path.GetFullPath(archivePath);
        var now = DateTimeOffset.UtcNow;
        var archiveHash = string.Empty;

        try
        {
            archiveHash = await ComputeArchiveXxHash128Async(fullPath, cancellationToken).ConfigureAwait(false);
            var info = new FileInfo(fullPath);
            var indexed = await ReadArchiveAsync(fullPath, cancellationToken).ConfigureAwait(false);
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.TapeArchives.SingleOrDefaultAsync(x => x.ArchiveXxHash128 == archiveHash, cancellationToken).ConfigureAwait(false)
                ?? new TapeMetadataArchive { ArchiveXxHash128 = archiveHash, ArchivePath = fullPath };

            if (db.Entry(row).State == EntityState.Detached)
                db.TapeArchives.Add(row);

            var oldRowsForPath = await db.TapeArchives
                .Where(x => x.ArchivePath == fullPath && x.ArchiveXxHash128 != archiveHash && !x.Missing)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var oldRow in oldRowsForPath)
            {
                oldRow.Missing = true;
                oldRow.Status = "Missing";
                oldRow.IndexedAtUtc = now;
            }

            ApplyIndexedArchive(row, indexed, info, now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await PublishAsync("metadata.archive.indexed", "Success", $"Indexed tape archive {Path.GetFileName(fullPath)}.", fullPath, cancellationToken).ConfigureAwait(false);
            return archiveHash;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(archiveHash))
                await SaveFailedArchiveAsync(fullPath, archiveHash, ex.Message, now, cancellationToken).ConfigureAwait(false);
            await PublishAsync("metadata.archive.failed", "Error", $"Failed to index tape archive {Path.GetFileName(fullPath)}: {ex.Message}", fullPath, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(archiveHash) ? null : archiveHash;
        }
    }

    private async Task<IndexedTapeArchive> ReadArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        LtfsIndex? index = null;
        CmCapacitySummary? capacity = null;

        await using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        await using var zstandardStream = new ZstandardStream(fileStream, CompressionMode.Decompress, leaveOpen: false);
        using var tarReader = new TarReader(zstandardStream, leaveOpen: false);

        while (tarReader.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.DataStream is null)
                continue;

            if (entry.Name.EndsWith(".schema", StringComparison.OrdinalIgnoreCase))
            {
                index = LtfsSchemaReader.Read(entry.DataStream);
                continue;
            }

            if (entry.Name.EndsWith(".cm.bin", StringComparison.OrdinalIgnoreCase))
            {
                using var cm = new MemoryStream();
                await entry.DataStream.CopyToAsync(cm, cancellationToken).ConfigureAwait(false);
                capacity = CMParser.CreateFromSpan(cm.ToArray()).GetCapacitySummary();
            }
        }

        if (index is null)
            throw new InvalidDataException("Archive does not contain an LTFS .schema entry.");

        var counts = CountIndex(index);
        return new IndexedTapeArchive(index, counts, capacity);
    }

    private async Task SaveFailedArchiveAsync(string archivePath, string archiveHash, string error, DateTimeOffset indexedAt, CancellationToken cancellationToken)
    {
        var info = new FileInfo(archivePath);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.TapeArchives.SingleOrDefaultAsync(x => x.ArchiveXxHash128 == archiveHash, cancellationToken).ConfigureAwait(false)
            ?? new TapeMetadataArchive { ArchiveXxHash128 = archiveHash, ArchivePath = archivePath };

        if (db.Entry(row).State == EntityState.Detached)
            db.TapeArchives.Add(row);

        row.Barcode = GetBarcode(archivePath);
        row.ArchiveName = Path.GetFileName(archivePath);
        row.ArchiveSizeBytes = info.Exists ? info.Length : 0;
        row.ArchiveLastWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : indexedAt;
        row.IndexedAtUtc = indexedAt;
        row.Missing = !info.Exists;
        row.Status = "Failed";
        row.Error = error;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkMissingAsync(string archivePath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(archivePath);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.TapeArchives
            .Where(x => x.ArchivePath == fullPath && !x.Missing)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            row.Missing = true;
            row.Status = "Missing";
            row.IndexedAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await PublishAsync("metadata.archive.missing", "Warning", $"Tape archive is missing: {Path.GetFileName(fullPath)}.", fullPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkMissingArchivesAsync(IReadOnlySet<string> observedHashes, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.TapeArchives.Where(x => !x.Missing).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows.Where(row => !observedHashes.Contains(row.ArchiveXxHash128)))
        {
            row.Missing = true;
            row.Status = "Missing";
            row.IndexedAtUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private void StartWatcher()
    {
        watcher = new FileSystemWatcher(paths.TapeDataDirectory, "*.tar.zst")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
        };

        watcher.Created += (_, e) => QueueWatcherItem(TapeMetadataWorkKind.Upsert, e.FullPath);
        watcher.Changed += (_, e) => QueueWatcherItem(TapeMetadataWorkKind.Upsert, e.FullPath);
        watcher.Deleted += (_, e) => QueueWatcherItem(TapeMetadataWorkKind.MarkMissing, e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            QueueWatcherItem(TapeMetadataWorkKind.MarkMissing, e.OldFullPath);
            QueueWatcherItem(TapeMetadataWorkKind.Upsert, e.FullPath);
        };
        watcher.EnableRaisingEvents = true;
    }

    private void QueueWatcherItem(TapeMetadataWorkKind kind, string path)
    {
        if (path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            return;

        _ = workQueue.Writer.TryWrite(new TapeMetadataWorkItem(kind, path));
    }

    private bool IsTapeArchivePath(string archivePath)
    {
        var relative = Path.GetRelativePath(paths.TapeDataDirectory, Path.GetFullPath(archivePath));
        return !relative.StartsWith("..", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative)
            && relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length >= 2
            && archivePath.EndsWith(".tar.zst", StringComparison.OrdinalIgnoreCase);
    }

    private string GetBarcode(string archivePath)
    {
        var relative = Path.GetRelativePath(paths.TapeDataDirectory, Path.GetFullPath(archivePath));
        var firstSeparator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return firstSeparator <= 0 ? "unknown" : relative[..firstSeparator];
    }

    private static IQueryable<TapeMetadataArchive> ApplyQuery(
        IQueryable<TapeMetadataArchive> source,
        string? search,
        bool includeMissing,
        string? barcode)
    {
        if (!includeMissing)
            source = source.Where(x => !x.Missing);
        if (!string.IsNullOrWhiteSpace(barcode))
            source = source.Where(x => x.Barcode == barcode);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmed = search.Trim();
            source = source.Where(x =>
                x.Barcode.Contains(trimmed)
                || x.ArchiveName.Contains(trimmed)
                || x.ArchivePath.Contains(trimmed));
        }

        return source;
    }

    private static IOrderedEnumerable<TapeMetadataArchive> SortArchives(IEnumerable<TapeMetadataArchive> rows)
        => rows
            .OrderBy(x => x.Barcode, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(x => x.GenerationNumber ?? long.MinValue)
            .ThenByDescending(x => x.ArchiveLastWriteTimeUtc);

    private TapeMetadataArchiveDto ToDto(TapeMetadataArchive row)
    {
        var relative = Path.GetRelativePath(paths.TapeDataDirectory, row.ArchivePath);
        return new TapeMetadataArchiveDto(
            row.ArchiveXxHash128,
            row.Barcode,
            row.ArchivePath,
            relative,
            row.ArchiveName,
            row.ArchiveSizeBytes,
            row.ArchiveLastWriteTimeUtc,
            row.IndexedAtUtc,
            row.Missing,
            row.Status,
            row.Error,
            row.VolumeUuid,
            row.GenerationNumber,
            row.LtfsUpdateTime,
            row.LocationPartition,
            row.LocationStartBlock,
            row.FileCount,
            row.DirectoryCount,
            row.LogicalBytes,
            row.TotalBytes,
            row.UsedBytes,
            row.AvailableBytes);
    }

    private void ApplyIndexedArchive(TapeMetadataArchive row, IndexedTapeArchive indexed, FileInfo info, DateTimeOffset indexedAt)
    {
        var index = indexed.Index;
        row.Barcode = GetBarcode(info.FullName);
        row.ArchiveName = info.Name;
        row.ArchiveSizeBytes = info.Length;
        row.ArchiveLastWriteTimeUtc = info.LastWriteTimeUtc;
        row.IndexedAtUtc = indexedAt;
        row.Missing = false;
        row.Status = "Ready";
        row.Error = indexed.Capacity is null ? "Archive does not contain .cm.bin; capacity fields are unavailable." : null;
        row.VolumeUuid = index.VolumeUuid;
        row.GenerationNumber = ToInt64OrNull(index.GenerationNumber);
        row.LtfsUpdateTime = index.UpdateTime;
        row.LocationPartition = index.Location.Partition.ToString();
        row.LocationStartBlock = ToInt64OrNull(index.Location.StartBlock);
        row.FileCount = indexed.Counts.FileCount;
        row.DirectoryCount = indexed.Counts.DirectoryCount;
        row.LogicalBytes = indexed.Counts.LogicalBytes;
        row.TotalBytes = indexed.Capacity?.TotalBytes;
        row.UsedBytes = indexed.Capacity?.UsedBytes;
        row.AvailableBytes = indexed.Capacity?.AvailableBytes;
    }

    private async Task PublishAsync(string type, string severity, string message, string? operationId, CancellationToken cancellationToken)
    {
        await hubContext.Clients.All.SendAsync(
            "ReceiveEvent",
            KokoRealtimeEventDto.Create(type, severity, message, operationId),
            cancellationToken).ConfigureAwait(false);
    }

    private static LtfsIndexCounts CountIndex(LtfsIndex index)
    {
        long fileCount = 0;
        long directoryCount = 0;
        long logicalBytes = 0;

        foreach (var file in index.RootFiles)
            CountFile(file);
        foreach (var directory in index.RootDirectories)
            CountDirectory(directory);

        return new LtfsIndexCounts(fileCount, directoryCount, logicalBytes);

        void CountDirectory(LtfsDirectory directory)
        {
            directoryCount += 1;
            foreach (var file in directory.Files)
                CountFile(file);
            foreach (var child in directory.Directories)
                CountDirectory(child);
        }

        void CountFile(LtfsFile file)
        {
            fileCount += 1;
            logicalBytes += file.Length;
        }
    }

    private async Task EnsureTapeMetaDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        if (await HasArchiveHashColumnAsync(db, cancellationToken).ConfigureAwait(false))
            return;

        logger.LogWarning("TapeMeta database schema is incompatible with archive hash metadata. Recreating cache database.");
        await db.Database.EnsureDeletedAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> HasArchiveHashColumnAsync(TapeMetaDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('TapeArchives')";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), nameof(TapeMetadataArchive.ArchiveXxHash128), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static async Task<string> ComputeArchiveXxHash128Async(string archivePath, CancellationToken cancellationToken)
    {
        var hasher = new XxHash128();
        var buffer = new byte[1024 * 1024];
        await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer.Length, FileOptions.SequentialScan);

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            hasher.Append(buffer.AsSpan(0, read));
        }

        return Convert.ToHexString(hasher.GetCurrentHash());
    }

    private static long? ToInt64OrNull(ulong value)
        => value > long.MaxValue ? null : (long)value;

    private enum TapeMetadataWorkKind
    {
        FullScan,
        Upsert,
        MarkMissing
    }

    private sealed record TapeMetadataWorkItem(TapeMetadataWorkKind Kind, string? Path);

    private sealed record IndexedTapeArchive(LtfsIndex Index, LtfsIndexCounts Counts, CmCapacitySummary? Capacity);

    private sealed record LtfsIndexCounts(long FileCount, long DirectoryCount, long LogicalBytes);
}
