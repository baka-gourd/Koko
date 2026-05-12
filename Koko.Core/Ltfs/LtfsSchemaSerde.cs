using System.Globalization;
using System.Text;
using System.Xml;

namespace Koko.Core.Ltfs;

public enum LtfsSchemaWriteMode
{
    LegacyReduced,
    StrictWrapped
}

public sealed record LtfsSchemaReaderOptions(bool LegacyDecodePercent25 = false);

public sealed record LtfsSchemaWriterOptions(
    LtfsSchemaWriteMode Mode = LtfsSchemaWriteMode.LegacyReduced,
    bool LeaveOpen = false);

public static class LtfsSchemaReader
{
    public static LtfsIndex ReadFile(string path, LtfsSchemaReaderOptions? options = null)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, options);
    }

    public static LtfsIndex Read(Stream stream, LtfsSchemaReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new LtfsSchemaReaderOptions();

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            XmlResolver = null,
        };

        using var reader = XmlReader.Create(stream, settings);
        reader.MoveToContent();

        if (!IsElement(reader, "ltfsindex"))
            throw new XmlException("Expected ltfsindex root element.");

        var index = new LtfsIndex
        {
            Version = ReadRootVersion(reader),
        };

        if (reader.IsEmptyElement)
        {
            reader.ReadStartElement();
            return index;
        }

        reader.ReadStartElement();
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            switch (reader.LocalName)
            {
                case "creator":
                    index.Creator = ReadText(reader, options);
                    break;
                case "volumeuuid":
                    index.VolumeUuid = Guid.Parse(ReadText(reader, options));
                    break;
                case "generationnumber":
                    index.GenerationNumber = ulong.Parse(ReadText(reader, options), CultureInfo.InvariantCulture);
                    break;
                case "updatetime":
                    index.UpdateTime = ReadText(reader, options);
                    break;
                case "location":
                    index.Location = ReadLocation(reader, options);
                    break;
                case "previousgenerationlocation":
                    index.PreviousGenerationLocation = ReadLocation(reader, options);
                    break;
                case "allowpolicyupdate":
                    index.AllowPolicyUpdate = ReadBoolean(reader, options);
                    break;
                case "volumelockstate":
                    index.VolumeLockState = ParseVolumeLockState(ReadText(reader, options));
                    break;
                case "highestfileuid":
                    index.HighestFileUid = long.Parse(ReadText(reader, options), CultureInfo.InvariantCulture);
                    break;
                case "file":
                    index.RootFiles.Add(ReadFileElement(reader, options));
                    break;
                case "directory":
                    index.RootDirectories.Add(ReadDirectory(reader, options));
                    break;
                case "_file":
                    ReadFileWrapper(reader, index.RootFiles, options);
                    break;
                case "_directory":
                    ReadDirectoryWrapper(reader, index.RootDirectories, options);
                    break;
                default:
                    index.UnknownElements.Add(reader.ReadOuterXml());
                    break;
            }
        }

        reader.ReadEndElement();
        return index;
    }

    private static LtfsLocation ReadLocation(XmlReader reader, LtfsSchemaReaderOptions options)
    {
        var location = new LtfsLocation();
        if (reader.IsEmptyElement)
        {
            reader.ReadStartElement();
            return location;
        }

        reader.ReadStartElement();
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            switch (reader.LocalName)
            {
                case "partition":
                    location.Partition = ParsePartition(ReadText(reader, options));
                    break;
                case "startblock":
                    location.StartBlock = ulong.Parse(ReadText(reader, options), CultureInfo.InvariantCulture);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        reader.ReadEndElement();
        return location;
    }

    private static LtfsDirectory ReadDirectory(XmlReader reader, LtfsSchemaReaderOptions options)
    {
        var directory = new LtfsDirectory();
        if (reader.IsEmptyElement)
        {
            reader.ReadStartElement();
            return directory;
        }

        reader.ReadStartElement();
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            switch (reader.LocalName)
            {
                case "name":
                    directory.Name = ReadText(reader, options);
                    break;
                case "readonly":
                    directory.ReadOnly = ReadBoolean(reader, options);
                    break;
                case "creationtime":
                    directory.CreationTime = ReadText(reader, options);
                    break;
                case "changetime":
                    directory.ChangeTime = ReadText(reader, options);
                    break;
                case "modifytime":
                    directory.ModifyTime = ReadText(reader, options);
                    break;
                case "accesstime":
                    directory.AccessTime = ReadText(reader, options);
                    break;
                case "backuptime":
                    directory.BackupTime = ReadText(reader, options);
                    break;
                case "fileuid":
                    directory.FileUid = long.Parse(ReadText(reader, options), CultureInfo.InvariantCulture);
                    break;
                case "contents":
                    ReadContents(reader, directory, options);
                    break;
                default:
                    directory.UnknownElements.Add(reader.ReadOuterXml());
                    break;
            }
        }

        reader.ReadEndElement();
        return directory;
    }

    private static void ReadContents(XmlReader reader, LtfsDirectory directory, LtfsSchemaReaderOptions options)
    {
        if (reader.IsEmptyElement)
        {
            reader.ReadStartElement();
            return;
        }

        reader.ReadStartElement();
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            switch (reader.LocalName)
            {
                case "file":
                    directory.Files.Add(ReadFileElement(reader, options));
                    break;
                case "directory":
                    directory.Directories.Add(ReadDirectory(reader, options));
                    break;
                case "_file":
                    ReadFileWrapper(reader, directory.Files, options);
                    break;
                case "_directory":
                    ReadDirectoryWrapper(reader, directory.Directories, options);
                    break;
                default:
                    directory.UnknownContentElements.Add(reader.ReadOuterXml());
                    break;
            }
        }

        reader.ReadEndElement();
    }

    private static LtfsFile ReadFileElement(XmlReader reader, LtfsSchemaReaderOptions options)
    {
        var file = new LtfsFile();
        if (reader.IsEmptyElement)
        {
            reader.ReadStartElement();
            return file;
        }

        reader.ReadStartElement();
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            switch (reader.LocalName)
            {
                case "name":
                    file.Name = ReadText(reader, options);
                    break;
                case "length":
                    file.Length = long.Parse(ReadText(reader, options), CultureInfo.InvariantCulture);
                    break;
                case "readonly":
                    file.ReadOnly = ReadBoolean(reader, options);
                    break;
                case "openforwrite":
                    file.OpenForWrite = ReadBoolean(reader, options);
                    break;
                case "creationtime":
                    file.CreationTime = ReadText(reader, options);
                    break;
                case "changetime":
                    file.ChangeTime = ReadText(reader, options);
                    break;
                case "modifytime":
                    file.ModifyTime = ReadText(reader, options);
                    break;
                case "accesstime":
                    file.AccessTime = ReadText(reader, options);
                    break;
                case "backuptime":
                    file.BackupTime = ReadText(reader, options);
                    break;
                case "fileuid":
                    file.FileUid = long.Parse(ReadText(reader, options), CultureInfo.InvariantCulture);
                    break;
                case "symlink":
                    file.Symlink = ReadText(reader, options);
                    break;
                case "extendedattributes":
                    ReadExtendedAttributes(reader, file, options);
                    break;
                case "extentinfo":
                    ReadExtents(reader, file, options);
                    break;
                default:
                    file.UnknownElements.Add(reader.ReadOuterXml());
                    break;
            }
        }

        reader.ReadEndElement();
        return file;
    }

    private static void ReadExtendedAttributes(XmlReader reader, LtfsFile file, LtfsSchemaReaderOptions options)
    {
        if (reader.IsEmptyElement)
        {
            reader.ReadStartElement();
            return;
        }

        reader.ReadStartElement();
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            if (!IsElement(reader, "xattr"))
            {
                file.UnknownElements.Add(reader.ReadOuterXml());
                continue;
            }

            file.ExtendedAttributes.Add(ReadXAttr(reader, options));
        }

        reader.ReadEndElement();
    }

    private static LtfsExtendedAttribute ReadXAttr(XmlReader reader, LtfsSchemaReaderOptions options)
    {
        var attribute = new LtfsExtendedAttribute();
        if (reader.IsEmptyElement)
        {
            reader.ReadStartElement();
            return attribute;
        }

        reader.ReadStartElement();
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            switch (reader.LocalName)
            {
                case "key":
                    attribute.Key = ReadText(reader, options);
                    break;
                case "value":
                    attribute.Value = ReadText(reader, options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        reader.ReadEndElement();
        return attribute;
    }

    private static void ReadExtents(XmlReader reader, LtfsFile file, LtfsSchemaReaderOptions options)
    {
        if (reader.IsEmptyElement)
        {
            reader.ReadStartElement();
            return;
        }

        reader.ReadStartElement();
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            if (IsElement(reader, "extent"))
                file.Extents.Add(ReadExtent(reader, options));
            else
                file.UnknownElements.Add(reader.ReadOuterXml());
        }

        reader.ReadEndElement();
    }

    private static LtfsExtent ReadExtent(XmlReader reader, LtfsSchemaReaderOptions options)
    {
        var extent = new LtfsExtent();
        if (reader.IsEmptyElement)
        {
            reader.ReadStartElement();
            return extent;
        }

        reader.ReadStartElement();
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            switch (reader.LocalName)
            {
                case "fileoffset":
                    extent.FileOffset = long.Parse(ReadText(reader, options), CultureInfo.InvariantCulture);
                    break;
                case "partition":
                    extent.Partition = ParsePartition(ReadText(reader, options));
                    break;
                case "startblock":
                    extent.StartBlock = long.Parse(ReadText(reader, options), CultureInfo.InvariantCulture);
                    break;
                case "byteoffset":
                    extent.ByteOffset = long.Parse(ReadText(reader, options), CultureInfo.InvariantCulture);
                    break;
                case "bytecount":
                    extent.ByteCount = long.Parse(ReadText(reader, options), CultureInfo.InvariantCulture);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        reader.ReadEndElement();
        return extent;
    }

    private static void ReadFileWrapper(XmlReader reader, List<LtfsFile> files, LtfsSchemaReaderOptions options)
    {
        if (reader.IsEmptyElement)
        {
            reader.ReadStartElement();
            return;
        }

        reader.ReadStartElement();
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType == XmlNodeType.Element && IsElement(reader, "file"))
            {
                files.Add(ReadFileElement(reader, options));
            }
            else if (reader.NodeType == XmlNodeType.Element)
            {
                reader.Skip();
            }
            else
            {
                reader.Read();
            }
        }

        reader.ReadEndElement();
    }

    private static void ReadDirectoryWrapper(XmlReader reader, List<LtfsDirectory> directories, LtfsSchemaReaderOptions options)
    {
        if (reader.IsEmptyElement)
        {
            reader.ReadStartElement();
            return;
        }

        reader.ReadStartElement();
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType == XmlNodeType.Element && IsElement(reader, "directory"))
            {
                directories.Add(ReadDirectory(reader, options));
            }
            else if (reader.NodeType == XmlNodeType.Element)
            {
                reader.Skip();
            }
            else
            {
                reader.Read();
            }
        }

        reader.ReadEndElement();
    }

    private static string ReadRootVersion(XmlReader reader)
    {
        return reader.GetAttribute("version")
            ?? reader.GetAttribute("v", "http://www.w3.org/2000/xmlns/")
            ?? LtfsIndex.DefaultVersion;
    }

    private static string ReadText(XmlReader reader, LtfsSchemaReaderOptions options)
    {
        var value = reader.ReadElementContentAsString();
        return options.LegacyDecodePercent25 ? value.Replace("%25", "%", StringComparison.Ordinal) : value;
    }

    private static bool ReadBoolean(XmlReader reader, LtfsSchemaReaderOptions options)
    {
        return bool.Parse(ReadText(reader, options));
    }

    private static bool IsElement(XmlReader reader, string localName)
    {
        return reader.NodeType == XmlNodeType.Element && string.Equals(reader.LocalName, localName, StringComparison.Ordinal);
    }

    public static LtfsPartition ParsePartition(string value)
    {
        return value.Trim() switch
        {
            "a" or "A" or "0" => LtfsPartition.A,
            "b" or "B" or "1" => LtfsPartition.B,
            _ => throw new XmlException($"Unsupported LTFS partition '{value}'."),
        };
    }

    private static LtfsVolumeLockState ParseVolumeLockState(string value)
    {
        return value.Trim() switch
        {
            "unlocked" => LtfsVolumeLockState.Unlocked,
            "locked" => LtfsVolumeLockState.Locked,
            "permlocked" => LtfsVolumeLockState.PermLocked,
            _ => throw new XmlException($"Unsupported LTFS volume lock state '{value}'."),
        };
    }
}

public static class LtfsSchemaWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly byte[] XmlDeclaration = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"u8.ToArray();

    public static void WriteFile(string path, LtfsIndex index, LtfsSchemaWriterOptions? options = null)
    {
        options ??= new LtfsSchemaWriterOptions();
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
        Write(stream, index, options with { LeaveOpen = false });
    }

    public static void Write(Stream stream, LtfsIndex index, LtfsSchemaWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(index);
        options ??= new LtfsSchemaWriterOptions();

        stream.Write(XmlDeclaration);

        var settings = new XmlWriterSettings
        {
            Async = false,
            CloseOutput = !options.LeaveOpen,
            Encoding = Utf8NoBom,
            Indent = false,
            NewLineHandling = NewLineHandling.None,
            OmitXmlDeclaration = true,
        };

        using var writer = XmlWriter.Create(stream, settings);
        WriteIndex(writer, index, options.Mode);
    }

    private static void WriteIndex(XmlWriter writer, LtfsIndex index, LtfsSchemaWriteMode mode)
    {
        writer.WriteStartElement("ltfsindex");
        if (mode == LtfsSchemaWriteMode.LegacyReduced)
            writer.WriteAttributeString("version", string.IsNullOrWhiteSpace(index.Version) ? LtfsIndex.DefaultVersion : index.Version);
        else
            writer.WriteAttributeString("xmlns", "v", null, string.IsNullOrWhiteSpace(index.Version) ? LtfsIndex.DefaultVersion : index.Version);

        WriteElement(writer, "creator", index.Creator);
        WriteElement(writer, "volumeuuid", index.VolumeUuid.ToString("D"));
        WriteElement(writer, "generationnumber", index.GenerationNumber.ToString(CultureInfo.InvariantCulture));
        WriteElement(writer, "updatetime", index.UpdateTime);
        WriteLocation(writer, "location", index.Location);
        WriteLocation(writer, "previousgenerationlocation", index.PreviousGenerationLocation);
        WriteElement(writer, "allowpolicyupdate", FormatBoolean(index.AllowPolicyUpdate));

        foreach (var raw in index.UnknownElements)
            writer.WriteRaw(raw);

        WriteElement(writer, "volumelockstate", FormatVolumeLockState(index.VolumeLockState));
        WriteElement(writer, "highestfileuid", index.HighestFileUid.ToString(CultureInfo.InvariantCulture));

        WriteFileList(writer, index.RootFiles, mode);
        WriteDirectoryList(writer, index.RootDirectories, mode);
        writer.WriteEndElement();
    }

    private static void WriteDirectory(XmlWriter writer, LtfsDirectory directory, LtfsSchemaWriteMode mode)
    {
        writer.WriteStartElement("directory");
        WriteElement(writer, "name", directory.Name);
        WriteElement(writer, "readonly", FormatBoolean(directory.ReadOnly));
        WriteElement(writer, "creationtime", directory.CreationTime);
        WriteElement(writer, "changetime", directory.ChangeTime);
        WriteElement(writer, "modifytime", directory.ModifyTime);
        WriteElement(writer, "accesstime", directory.AccessTime);
        WriteElement(writer, "backuptime", directory.BackupTime);
        WriteElement(writer, "fileuid", directory.FileUid.ToString(CultureInfo.InvariantCulture));

        foreach (var raw in directory.UnknownElements)
            writer.WriteRaw(raw);

        writer.WriteStartElement("contents");
        WriteFileList(writer, directory.Files, mode);
        WriteDirectoryList(writer, directory.Directories, mode);
        foreach (var raw in directory.UnknownContentElements)
            writer.WriteRaw(raw);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteFile(XmlWriter writer, LtfsFile file)
    {
        writer.WriteStartElement("file");
        WriteElement(writer, "name", file.Name);
        WriteElement(writer, "length", file.Length.ToString(CultureInfo.InvariantCulture));
        WriteElement(writer, "readonly", FormatBoolean(file.ReadOnly));
        WriteElement(writer, "openforwrite", FormatBoolean(file.OpenForWrite));
        WriteElement(writer, "creationtime", file.CreationTime);
        WriteElement(writer, "changetime", file.ChangeTime);
        WriteElement(writer, "modifytime", file.ModifyTime);
        WriteElement(writer, "accesstime", file.AccessTime);
        WriteElement(writer, "backuptime", file.BackupTime);
        WriteElement(writer, "fileuid", file.FileUid.ToString(CultureInfo.InvariantCulture));

        if (file.Symlink is not null)
            WriteElement(writer, "symlink", file.Symlink);

        foreach (var raw in file.UnknownElements)
            writer.WriteRaw(raw);

        if (file.ExtendedAttributes.Count > 0)
        {
            writer.WriteStartElement("extendedattributes");
            foreach (var attribute in file.ExtendedAttributes)
            {
                writer.WriteStartElement("xattr");
                WriteElement(writer, "key", attribute.Key);
                WriteElement(writer, "value", attribute.Value);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        if (file.Extents.Count > 0)
        {
            writer.WriteStartElement("extentinfo");
            foreach (var extent in file.Extents)
            {
                writer.WriteStartElement("extent");
                WriteElement(writer, "fileoffset", extent.FileOffset.ToString(CultureInfo.InvariantCulture));
                WriteElement(writer, "partition", FormatPartition(extent.Partition));
                WriteElement(writer, "startblock", extent.StartBlock.ToString(CultureInfo.InvariantCulture));
                WriteElement(writer, "byteoffset", extent.ByteOffset.ToString(CultureInfo.InvariantCulture));
                WriteElement(writer, "bytecount", extent.ByteCount.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteFileList(XmlWriter writer, IReadOnlyList<LtfsFile> files, LtfsSchemaWriteMode mode)
    {
        if (mode == LtfsSchemaWriteMode.StrictWrapped)
            writer.WriteStartElement("_file");

        foreach (var file in files)
            WriteFile(writer, file);

        if (mode == LtfsSchemaWriteMode.StrictWrapped)
            writer.WriteEndElement();
    }

    private static void WriteDirectoryList(XmlWriter writer, IReadOnlyList<LtfsDirectory> directories, LtfsSchemaWriteMode mode)
    {
        if (mode == LtfsSchemaWriteMode.StrictWrapped)
            writer.WriteStartElement("_directory");

        foreach (var directory in directories)
            WriteDirectory(writer, directory, mode);

        if (mode == LtfsSchemaWriteMode.StrictWrapped)
            writer.WriteEndElement();
    }

    private static void WriteLocation(XmlWriter writer, string elementName, LtfsLocation location)
    {
        writer.WriteStartElement(elementName);
        WriteElement(writer, "partition", FormatPartition(location.Partition));
        WriteElement(writer, "startblock", location.StartBlock.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
    }

    private static void WriteElement(XmlWriter writer, string elementName, string value)
    {
        writer.WriteStartElement(elementName);
        writer.WriteString(value);
        writer.WriteEndElement();
    }

    public static string FormatPartition(LtfsPartition partition) => partition switch
    {
        LtfsPartition.A => "a",
        LtfsPartition.B => "b",
        _ => throw new ArgumentOutOfRangeException(nameof(partition)),
    };

    private static string FormatVolumeLockState(LtfsVolumeLockState value) => value switch
    {
        LtfsVolumeLockState.Unlocked => "unlocked",
        LtfsVolumeLockState.Locked => "locked",
        LtfsVolumeLockState.PermLocked => "permlocked",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string FormatBoolean(bool value) => value ? "true" : "false";
}
