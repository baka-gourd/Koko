using System.Globalization;

namespace Koko.Core.Ltfs;

public enum LtfsPartition
{
    A,
    B
}

public enum LtfsVolumeLockState
{
    Unlocked,
    Locked,
    PermLocked
}

public sealed class LtfsIndex
{
    public const string DefaultVersion = "2.4.0";

    public string Version { get; set; } = DefaultVersion;
    public string Creator { get; set; } = string.Empty;
    public Guid VolumeUuid { get; set; }
    public ulong GenerationNumber { get; set; }
    public string UpdateTime { get; set; } = FormatLtfsTime(DateTimeOffset.UtcNow);
    public LtfsLocation Location { get; set; } = new();
    public LtfsLocation PreviousGenerationLocation { get; set; } = new();
    public bool AllowPolicyUpdate { get; set; }
    public LtfsVolumeLockState VolumeLockState { get; set; } = LtfsVolumeLockState.Unlocked;
    public long HighestFileUid { get; set; }
    public List<LtfsFile> RootFiles { get; } = [];
    public List<LtfsDirectory> RootDirectories { get; } = [];
    public List<string> UnknownElements { get; } = [];

    public LtfsDirectory? RootDirectory => RootDirectories.Count == 0 ? null : RootDirectories[0];

    public LtfsIndex Clone()
    {
        var clone = new LtfsIndex
        {
            Version = Version,
            Creator = Creator,
            VolumeUuid = VolumeUuid,
            GenerationNumber = GenerationNumber,
            UpdateTime = UpdateTime,
            Location = Location.Clone(),
            PreviousGenerationLocation = PreviousGenerationLocation.Clone(),
            AllowPolicyUpdate = AllowPolicyUpdate,
            VolumeLockState = VolumeLockState,
            HighestFileUid = HighestFileUid,
        };

        clone.RootFiles.AddRange(RootFiles.Select(x => x.Clone()));
        clone.RootDirectories.AddRange(RootDirectories.Select(x => x.Clone()));
        clone.UnknownElements.AddRange(UnknownElements);
        return clone;
    }

    public static string FormatLtfsTime(DateTimeOffset time)
    {
        return time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z", CultureInfo.InvariantCulture);
    }
}

public sealed class LtfsLocation
{
    public LtfsPartition Partition { get; set; }
    public ulong StartBlock { get; set; }

    public LtfsLocation Clone() => new()
    {
        Partition = Partition,
        StartBlock = StartBlock,
    };
}

public sealed class LtfsDirectory
{
    public string Name { get; set; } = string.Empty;
    public bool ReadOnly { get; set; }
    public string CreationTime { get; set; } = string.Empty;
    public string ChangeTime { get; set; } = string.Empty;
    public string ModifyTime { get; set; } = string.Empty;
    public string AccessTime { get; set; } = string.Empty;
    public string BackupTime { get; set; } = string.Empty;
    public long FileUid { get; set; }
    public List<LtfsFile> Files { get; } = [];
    public List<LtfsDirectory> Directories { get; } = [];
    public List<string> UnknownElements { get; } = [];
    public List<string> UnknownContentElements { get; } = [];

    public LtfsDirectory Clone()
    {
        var clone = new LtfsDirectory
        {
            Name = Name,
            ReadOnly = ReadOnly,
            CreationTime = CreationTime,
            ChangeTime = ChangeTime,
            ModifyTime = ModifyTime,
            AccessTime = AccessTime,
            BackupTime = BackupTime,
            FileUid = FileUid,
        };

        clone.Files.AddRange(Files.Select(x => x.Clone()));
        clone.Directories.AddRange(Directories.Select(x => x.Clone()));
        clone.UnknownElements.AddRange(UnknownElements);
        clone.UnknownContentElements.AddRange(UnknownContentElements);
        return clone;
    }
}

public sealed class LtfsFile
{
    public string Name { get; set; } = string.Empty;
    public long Length { get; set; }
    public bool ReadOnly { get; set; }
    public bool OpenForWrite { get; set; } = true;
    public string CreationTime { get; set; } = string.Empty;
    public string ChangeTime { get; set; } = string.Empty;
    public string ModifyTime { get; set; } = string.Empty;
    public string AccessTime { get; set; } = string.Empty;
    public string BackupTime { get; set; } = string.Empty;
    public long FileUid { get; set; }
    public string? Symlink { get; set; }
    public List<LtfsExtendedAttribute> ExtendedAttributes { get; } = [];
    public List<LtfsExtent> Extents { get; } = [];
    public List<string> UnknownElements { get; } = [];

    public string? GetExtendedAttribute(string key)
    {
        foreach (var attribute in ExtendedAttributes)
        {
            if (string.Equals(attribute.Key, key, StringComparison.OrdinalIgnoreCase))
                return attribute.Value;
        }

        return null;
    }

    public void SetExtendedAttribute(string key, string value, bool ignoreBlank = false)
    {
        if (ignoreBlank && value.Length == 0)
            return;

        foreach (var attribute in ExtendedAttributes)
        {
            if (!string.Equals(attribute.Key, key, StringComparison.OrdinalIgnoreCase))
                continue;

            attribute.Value = value;
            return;
        }

        ExtendedAttributes.Add(new LtfsExtendedAttribute { Key = key, Value = value });
    }

    public LtfsFile Clone()
    {
        var clone = new LtfsFile
        {
            Name = Name,
            Length = Length,
            ReadOnly = ReadOnly,
            OpenForWrite = OpenForWrite,
            CreationTime = CreationTime,
            ChangeTime = ChangeTime,
            ModifyTime = ModifyTime,
            AccessTime = AccessTime,
            BackupTime = BackupTime,
            FileUid = FileUid,
            Symlink = Symlink,
        };

        clone.ExtendedAttributes.AddRange(ExtendedAttributes.Select(x => x.Clone()));
        clone.Extents.AddRange(Extents.Select(x => x.Clone()));
        clone.UnknownElements.AddRange(UnknownElements);
        return clone;
    }
}

public sealed class LtfsExtendedAttribute
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public LtfsExtendedAttribute Clone() => new()
    {
        Key = Key,
        Value = Value,
    };
}

public sealed class LtfsExtent
{
    public long FileOffset { get; set; }
    public LtfsPartition Partition { get; set; }
    public long StartBlock { get; set; }
    public long ByteOffset { get; set; }
    public long ByteCount { get; set; }

    public LtfsExtent Clone() => new()
    {
        FileOffset = FileOffset,
        Partition = Partition,
        StartBlock = StartBlock,
        ByteOffset = ByteOffset,
        ByteCount = ByteCount,
    };
}
