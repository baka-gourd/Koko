namespace Koko.Core.Ltfs;

public sealed record LtfsIndexValidationOptions(
    long? LtfsBlockSizeBytes = null,
    bool AllowDuplicateNames = false,
    bool AllowLengthMismatchForSymlink = true,
    IReadOnlySet<LtfsPartition>? SupportedPartitions = null);

public sealed record LtfsIndexValidationResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

public static class LtfsIndexValidator
{
    private static readonly IReadOnlyDictionary<string, int> HashLengths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["ltfs.hash.crc32sum"] = 8,
        ["ltfs.hash.md5sum"] = 32,
        ["ltfs.hash.sha1sum"] = 40,
        ["ltfs.hash.sha256sum"] = 64,
        ["ltfs.hash.sha512sum"] = 128,
        ["ltfs.hash.blake3sum"] = 64,
        ["ltfs.hash.xxhash3sum"] = 16,
        ["ltfs.hash.xxhash128sum"] = 32,
    };

    public static LtfsIndexValidationResult ValidateInternal(LtfsIndex index, LtfsIndexValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        options ??= new LtfsIndexValidationOptions();

        var errors = new List<string>();
        var warnings = new List<string>();
        var fileUids = new HashSet<long>();
        long maxFileUid = 0;
        var supportedPartitions = options.SupportedPartitions ?? new HashSet<LtfsPartition> { LtfsPartition.A, LtfsPartition.B };

        if (index.VolumeUuid == Guid.Empty)
            errors.Add("volumeuuid is missing or empty.");

        if (index.RootDirectory is null)
            errors.Add("root directory is missing.");

        ValidateRootItems(index.RootFiles, index.RootDirectories, "", options, supportedPartitions, fileUids, ref maxFileUid, errors, warnings);

        if (index.HighestFileUid < maxFileUid)
            errors.Add($"highestfileuid {index.HighestFileUid} is smaller than actual max fileuid {maxFileUid}.");

        if (!supportedPartitions.Contains(index.Location.Partition))
            errors.Add($"index location references unsupported partition {index.Location.Partition}.");

        if (!supportedPartitions.Contains(index.PreviousGenerationLocation.Partition))
            errors.Add($"previous generation location references unsupported partition {index.PreviousGenerationLocation.Partition}.");

        if (index.UnknownElements.Count > 0)
            warnings.Add($"index contains {index.UnknownElements.Count} unsupported root element(s), preserved as raw XML.");

        return new LtfsIndexValidationResult(errors, warnings);
    }

    private static void ValidateRootItems(
        IReadOnlyList<LtfsFile> files,
        IReadOnlyList<LtfsDirectory> directories,
        string path,
        LtfsIndexValidationOptions options,
        IReadOnlySet<LtfsPartition> supportedPartitions,
        HashSet<long> fileUids,
        ref long maxFileUid,
        List<string> errors,
        List<string> warnings)
    {
        ValidateDuplicateNames(files, directories, path, options, errors);

        foreach (var file in files)
            ValidateFile(file, path, options, supportedPartitions, fileUids, ref maxFileUid, errors, warnings);

        foreach (var directory in directories)
            ValidateDirectory(directory, path, options, supportedPartitions, fileUids, ref maxFileUid, errors, warnings);
    }

    private static void ValidateDirectory(
        LtfsDirectory directory,
        string parentPath,
        LtfsIndexValidationOptions options,
        IReadOnlySet<LtfsPartition> supportedPartitions,
        HashSet<long> fileUids,
        ref long maxFileUid,
        List<string> errors,
        List<string> warnings)
    {
        var path = CombinePath(parentPath, directory.Name);
        ValidateFileUid(directory.FileUid, path, fileUids, ref maxFileUid, errors);
        ValidateDuplicateNames(directory.Files, directory.Directories, path, options, errors);

        if (directory.UnknownElements.Count > 0)
            warnings.Add($"directory '{path}' contains {directory.UnknownElements.Count} unsupported element(s), preserved as raw XML.");

        if (directory.UnknownContentElements.Count > 0)
            warnings.Add($"directory '{path}' contents contain {directory.UnknownContentElements.Count} unsupported element(s), preserved as raw XML.");

        foreach (var file in directory.Files)
            ValidateFile(file, path, options, supportedPartitions, fileUids, ref maxFileUid, errors, warnings);

        foreach (var child in directory.Directories)
            ValidateDirectory(child, path, options, supportedPartitions, fileUids, ref maxFileUid, errors, warnings);
    }

    private static void ValidateFile(
        LtfsFile file,
        string parentPath,
        LtfsIndexValidationOptions options,
        IReadOnlySet<LtfsPartition> supportedPartitions,
        HashSet<long> fileUids,
        ref long maxFileUid,
        List<string> errors,
        List<string> warnings)
    {
        var path = CombinePath(parentPath, file.Name);
        ValidateFileUid(file.FileUid, path, fileUids, ref maxFileUid, errors);

        if (file.Length < 0)
            errors.Add($"file '{path}' has negative length {file.Length}.");

        var extents = file.Extents.OrderBy(x => x.FileOffset).ToArray();
        long expectedOffset = 0;
        long totalByteCount = 0;
        foreach (var extent in extents)
        {
            if (!supportedPartitions.Contains(extent.Partition))
                errors.Add($"file '{path}' extent references unsupported partition {extent.Partition}.");

            if (extent.FileOffset < 0 || extent.StartBlock < 0 || extent.ByteOffset < 0 || extent.ByteCount < 0)
                errors.Add($"file '{path}' extent contains negative offset/block/count.");

            if (extent.FileOffset < expectedOffset)
                errors.Add($"file '{path}' extent at file offset {extent.FileOffset} overlaps a previous extent.");

            expectedOffset = Math.Max(expectedOffset, extent.FileOffset + extent.ByteCount);
            totalByteCount += extent.ByteCount;

            if (options.LtfsBlockSizeBytes is { } blockSize && extent.ByteOffset >= blockSize)
                errors.Add($"file '{path}' extent byteoffset exceeds LTFS block size {blockSize}.");
        }

        var canSkipLengthCheck = options.AllowLengthMismatchForSymlink && file.Symlink is not null;
        if (!canSkipLengthCheck && totalByteCount != file.Length)
            errors.Add($"file '{path}' length {file.Length} does not match extent bytecount sum {totalByteCount}.");

        foreach (var attribute in file.ExtendedAttributes)
        {
            if (!HashLengths.TryGetValue(attribute.Key, out var expectedLength))
                continue;

            if (attribute.Value.Length != expectedLength || !IsHex(attribute.Value))
                errors.Add($"file '{path}' hash xattr '{attribute.Key}' has invalid value length or format.");
        }

        if (file.UnknownElements.Count > 0)
            warnings.Add($"file '{path}' contains {file.UnknownElements.Count} unsupported element(s), preserved as raw XML.");
    }

    private static void ValidateDuplicateNames(
        IReadOnlyList<LtfsFile> files,
        IReadOnlyList<LtfsDirectory> directories,
        string path,
        LtfsIndexValidationOptions options,
        List<string> errors)
    {
        if (options.AllowDuplicateNames)
            return;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            if (!names.Add(directory.Name))
                errors.Add($"duplicate entry name '{directory.Name}' under '{DisplayPath(path)}'.");
        }

        foreach (var file in files)
        {
            if (!names.Add(file.Name))
                errors.Add($"duplicate entry name '{file.Name}' under '{DisplayPath(path)}'.");
        }
    }

    private static void ValidateFileUid(long fileUid, string path, HashSet<long> fileUids, ref long maxFileUid, List<string> errors)
    {
        if (fileUid <= 0)
            errors.Add($"entry '{DisplayPath(path)}' has non-positive fileuid {fileUid}.");

        if (!fileUids.Add(fileUid))
            errors.Add($"duplicate fileuid {fileUid} at '{DisplayPath(path)}'.");

        maxFileUid = Math.Max(maxFileUid, fileUid);
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }

        return true;
    }

    private static string CombinePath(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

    private static string DisplayPath(string path) => path.Length == 0 ? "/" : path;
}
