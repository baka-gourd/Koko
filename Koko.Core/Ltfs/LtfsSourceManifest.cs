using Microsoft.Extensions.FileSystemGlobbing;

namespace Koko.Core.Ltfs;

public enum LtfsSourceManifestItemType
{
    Directory,
    File,
    Unsupported
}

public enum LtfsSourceManifestItemStatus
{
    Pending,
    Written,
    Skipped,
    Failed
}

public enum LtfsExistingTargetPolicy
{
    Skip,
    Overwrite
}

public enum LtfsSourceChangePolicy
{
    UpdateBeforeWrite,
    Skip,
    Abort
}

public sealed record LtfsSourceManifestOptions(
    bool SkipSymlinks = true,
    LtfsExistingTargetPolicy ExistingTargetPolicy = LtfsExistingTargetPolicy.Skip,
    LtfsSourceChangePolicy SourceChangePolicy = LtfsSourceChangePolicy.UpdateBeforeWrite,
    bool SkipXAttrSidecarFiles = true,
    int SourceReadBufferBytes = 4 * 1024 * 1024);

public sealed record LtfsSourceManifestItem(
    string SourcePath,
    string DestinationPath,
    LtfsSourceManifestItemType ItemType,
    long Length,
    DateTimeOffset CreationTime,
    DateTimeOffset ModifyTime,
    DateTimeOffset AccessTime,
    FileAttributes Attributes,
    long? PlannedFileUid = null,
    LtfsSourceManifestItemStatus Status = LtfsSourceManifestItemStatus.Pending,
    string? Error = null);

public sealed record LtfsSourceManifest(
    IReadOnlyList<string> SourceRoots,
    IReadOnlyList<LtfsSourceManifestItem> Items)
{
    public IReadOnlyList<LtfsSourceManifestItem> Files => Items.Where(x => x.ItemType == LtfsSourceManifestItemType.File).ToArray();

    public IReadOnlyList<LtfsSourceManifestItem> Directories => Items.Where(x => x.ItemType == LtfsSourceManifestItemType.Directory).ToArray();

    public long TotalBytes => Files.Sum(x => x.Length);
}

public sealed record LtfsSourceManifestRequest(
    IReadOnlyList<string> SourcePaths,
    string? GlobPatternText = null,
    LtfsSourceManifestOptions? Options = null);

public static class LtfsSourceManifestBuilder
{
    public static LtfsSourceManifest Build(LtfsSourceManifestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SourcePaths);

        var options = request.Options ?? new LtfsSourceManifestOptions();
        var matcher = BuildMatcher(request.GlobPatternText);
        var roots = request.SourcePaths
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var items = new List<LtfsSourceManifestItem>();
        var seenDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (File.Exists(root))
            {
                var fileName = Path.GetFileName(root);
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                var matchName = fileName.Replace(Path.DirectorySeparatorChar, '/');
                if (!matcher.Match(matchName).HasMatches)
                    continue;

                AddFile(root, fileName, options, seenDestinations, items);
                continue;
            }

            if (!Directory.Exists(root))
                continue;

            var rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(rootName))
                continue;

            AddDirectory(root, rootName, options, seenDestinations, items);
            foreach (var file in matcher.GetResultsInFullPath(root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(file))
                    continue;

                var relativeToParent = Path.GetRelativePath(Path.GetDirectoryName(root)!, file);
                var destination = NormalizeLtfsPath(relativeToParent);
                AddParentDirectories(root, destination, options, seenDestinations, items);
                AddFile(file, destination, options, seenDestinations, items);
            }
        }

        return new LtfsSourceManifest(roots, items);
    }

    public static IReadOnlyList<LtfsWriteSource> ToWriteSources(LtfsSourceManifest manifest, LtfsSourceManifestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        options ??= new LtfsSourceManifestOptions();
        return manifest.Files
            .Where(x => x.Status == LtfsSourceManifestItemStatus.Pending)
            .OrderBy(x => x.DestinationPath, StringComparer.OrdinalIgnoreCase)
            .Select(x => ToWriteSource(x, options.SourceReadBufferBytes))
            .ToArray();
    }

    private static LtfsWriteSource ToWriteSource(LtfsSourceManifestItem item, int sourceReadBufferBytes)
    {
        var path = item.SourcePath;
        return new LtfsWriteSource(
            Path.GetFileName(item.DestinationPath),
            item.Length,
            _ => ValueTask.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, sourceReadBufferBytes, FileOptions.SequentialScan)),
            item.CreationTime,
            item.ModifyTime,
            item.AccessTime,
            (item.Attributes & FileAttributes.ReadOnly) != 0,
            SourcePath: item.SourcePath,
            DestinationPath: item.DestinationPath,
            InitialLength: item.Length,
            InitialModifyTime: item.ModifyTime);
    }

    private static Matcher BuildMatcher(string? patternText)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(patternText))
        {
            matcher.AddInclude("**/*");
            return matcher;
        }

        var hasInclude = false;
        foreach (var raw in patternText.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith(@"\!") || line.StartsWith(@"\#") || line.StartsWith(@"\\"))
                line = line[1..];

            if (line.StartsWith('!'))
            {
                var exclude = line[1..].TrimStart();
                if (exclude.Length > 0)
                    matcher.AddExclude(exclude);
                continue;
            }

            matcher.AddInclude(line);
            hasInclude = true;
        }

        if (!hasInclude)
            matcher.AddInclude("**/*");

        return matcher;
    }

    private static void AddParentDirectories(
        string root,
        string destination,
        LtfsSourceManifestOptions options,
        HashSet<string> seenDestinations,
        List<LtfsSourceManifestItem> items)
    {
        var directory = Path.GetDirectoryName(destination.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(directory))
            return;

        var current = string.Empty;
        foreach (var part in directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrWhiteSpace(part))
                continue;

            current = current.Length == 0 ? part : $"{current}/{part}";
            var sourcePath = Path.Combine(Path.GetDirectoryName(root) ?? string.Empty, current.Replace('/', Path.DirectorySeparatorChar));
            AddDirectory(sourcePath, current, options, seenDestinations, items);
        }
    }

    private static void AddDirectory(
        string sourcePath,
        string destination,
        LtfsSourceManifestOptions options,
        HashSet<string> seenDestinations,
        List<LtfsSourceManifestItem> items)
    {
        var normalized = NormalizeLtfsPath(destination);
        if (!seenDestinations.Add(EnsureDirectoryMarker(normalized)))
            return;

        try
        {
            var info = new DirectoryInfo(sourcePath);
            var attributes = info.Exists ? info.Attributes : FileAttributes.Directory;
            if (options.SkipSymlinks && (attributes & FileAttributes.ReparsePoint) != 0)
                return;

            items.Add(new LtfsSourceManifestItem(
                sourcePath,
                normalized,
                LtfsSourceManifestItemType.Directory,
                0,
                info.Exists ? info.CreationTimeUtc : DateTimeOffset.UtcNow,
                info.Exists ? info.LastWriteTimeUtc : DateTimeOffset.UtcNow,
                info.Exists ? info.LastAccessTimeUtc : DateTimeOffset.UtcNow,
                attributes));
        }
        catch (Exception ex)
        {
            items.Add(new LtfsSourceManifestItem(sourcePath, normalized, LtfsSourceManifestItemType.Unsupported, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, Status: LtfsSourceManifestItemStatus.Failed, Error: ex.Message));
        }
    }

    private static void AddFile(
        string sourcePath,
        string destination,
        LtfsSourceManifestOptions options,
        HashSet<string> seenDestinations,
        List<LtfsSourceManifestItem> items)
    {
        var normalized = NormalizeLtfsPath(destination);
        if (options.SkipXAttrSidecarFiles && string.Equals(Path.GetExtension(sourcePath), ".xattr", StringComparison.OrdinalIgnoreCase))
            return;

        if (!seenDestinations.Add(normalized))
            return;

        try
        {
            var info = new FileInfo(sourcePath);
            if (options.SkipSymlinks && IsSymlinkOrUnderSymlink(info))
                return;

            items.Add(new LtfsSourceManifestItem(
                sourcePath,
                normalized,
                LtfsSourceManifestItemType.File,
                info.Length,
                info.CreationTimeUtc,
                info.LastWriteTimeUtc,
                info.LastAccessTimeUtc,
                info.Attributes));
        }
        catch (Exception ex)
        {
            items.Add(new LtfsSourceManifestItem(sourcePath, normalized, LtfsSourceManifestItemType.Unsupported, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, Status: LtfsSourceManifestItemStatus.Failed, Error: ex.Message));
        }
    }

    private static bool IsSymlinkOrUnderSymlink(FileInfo file)
    {
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            return true;

        var directory = file.Directory;
        while (directory is not null)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
            directory = directory.Parent;
        }

        return false;
    }

    private static string NormalizeFullPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeLtfsPath(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }

    private static string EnsureDirectoryMarker(string path) => path.EndsWith('/') ? path : $"{path}/";
}
