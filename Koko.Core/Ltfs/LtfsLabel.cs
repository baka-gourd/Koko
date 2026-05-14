using System.Globalization;
using System.Text;
using System.Xml;

namespace Koko.Core.Ltfs;

public sealed class LtfsLabel
{
    public const string DefaultVersion = "2.4.0";

    public string Version { get; set; } = DefaultVersion;
    public string Creator { get; set; } = "Koko.Core";
    public string FormatTime { get; set; } = LtfsIndex.FormatLtfsTime(DateTimeOffset.UtcNow);
    public Guid VolumeUuid { get; set; }
    public LtfsPartition LocationPartition { get; set; } = LtfsPartition.B;
    public LtfsPartition IndexPartition { get; set; } = LtfsPartition.A;
    public LtfsPartition DataPartition { get; set; } = LtfsPartition.B;
    public long BlockSize { get; set; } = 512 * 1024;
    public bool Compression { get; set; } = true;

    public LtfsLabel Clone() => new()
    {
        Version = Version,
        Creator = Creator,
        FormatTime = FormatTime,
        VolumeUuid = VolumeUuid,
        LocationPartition = LocationPartition,
        IndexPartition = IndexPartition,
        DataPartition = DataPartition,
        BlockSize = BlockSize,
        Compression = Compression,
    };
}

public sealed record LtfsLabelWriterOptions(bool LeaveOpen = false);

public static class LtfsLabelWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly byte[] XmlDeclaration = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"u8.ToArray();

    public static void Write(Stream stream, LtfsLabel label, LtfsLabelWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(label);
        options ??= new LtfsLabelWriterOptions();

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
        writer.WriteStartElement("ltfslabel");
        writer.WriteAttributeString("version", string.IsNullOrWhiteSpace(label.Version) ? LtfsLabel.DefaultVersion : label.Version);
        WriteElement(writer, "creator", label.Creator);
        WriteElement(writer, "formattime", label.FormatTime);
        WriteElement(writer, "volumeuuid", label.VolumeUuid.ToString("D"));

        writer.WriteStartElement("location");
        WriteElement(writer, "partition", FormatPartition(label.LocationPartition));
        writer.WriteEndElement();

        writer.WriteStartElement("partitions");
        WriteElement(writer, "index", FormatPartition(label.IndexPartition));
        WriteElement(writer, "data", FormatPartition(label.DataPartition));
        writer.WriteEndElement();

        WriteElement(writer, "blocksize", label.BlockSize.ToString(CultureInfo.InvariantCulture));
        WriteElement(writer, "compression", label.Compression ? "true" : "false");
        writer.WriteEndElement();
    }

    public static byte[] ToArray(LtfsLabel label)
    {
        using var stream = new MemoryStream();
        Write(stream, label, new LtfsLabelWriterOptions(LeaveOpen: true));
        return stream.ToArray();
    }

    private static void WriteElement(XmlWriter writer, string name, string value)
    {
        writer.WriteStartElement(name);
        writer.WriteString(value);
        writer.WriteEndElement();
    }

    private static string FormatPartition(LtfsPartition partition)
    {
        return partition == LtfsPartition.A ? "a" : "b";
    }
}

public static class LtfsLabelReader
{
    public static LtfsLabel Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            XmlResolver = null,
        };

        using var reader = XmlReader.Create(stream, settings);
        reader.MoveToContent();
        if (!string.Equals(reader.LocalName, "ltfslabel", StringComparison.Ordinal))
            throw new XmlException("Expected ltfslabel root element.");

        var label = new LtfsLabel
        {
            Version = reader.GetAttribute("version") ?? LtfsLabel.DefaultVersion,
        };

        if (reader.IsEmptyElement)
        {
            reader.ReadStartElement();
            return label;
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
                    label.Creator = reader.ReadElementContentAsString();
                    break;
                case "formattime":
                    label.FormatTime = reader.ReadElementContentAsString();
                    break;
                case "volumeuuid":
                    label.VolumeUuid = Guid.Parse(reader.ReadElementContentAsString());
                    break;
                case "location":
                    label.LocationPartition = ReadSinglePartitionElement(reader, "partition");
                    break;
                case "partitions":
                    ReadPartitions(reader, label);
                    break;
                case "blocksize":
                    label.BlockSize = long.Parse(reader.ReadElementContentAsString(), CultureInfo.InvariantCulture);
                    break;
                case "compression":
                    label.Compression = bool.Parse(reader.ReadElementContentAsString());
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        reader.ReadEndElement();
        return label;
    }

    public static LtfsLabel FromArray(ReadOnlyMemory<byte> data)
    {
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        return Read(stream);
    }

    private static LtfsPartition ReadSinglePartitionElement(XmlReader reader, string childName)
    {
        if (reader.IsEmptyElement)
        {
            reader.ReadStartElement();
            return LtfsPartition.A;
        }

        reader.ReadStartElement();
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType == XmlNodeType.Element && string.Equals(reader.LocalName, childName, StringComparison.Ordinal))
            {
                var value = LtfsSchemaReader.ParsePartition(reader.ReadElementContentAsString());
                while (reader.NodeType != XmlNodeType.EndElement)
                    reader.Skip();
                reader.ReadEndElement();
                return value;
            }

            reader.Skip();
        }

        reader.ReadEndElement();
        return LtfsPartition.A;
    }

    private static void ReadPartitions(XmlReader reader, LtfsLabel label)
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
                case "index":
                    label.IndexPartition = LtfsSchemaReader.ParsePartition(reader.ReadElementContentAsString());
                    break;
                case "data":
                    label.DataPartition = LtfsSchemaReader.ParsePartition(reader.ReadElementContentAsString());
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        reader.ReadEndElement();
    }
}

public static class LtfsVol1Label
{
    public static byte[] Create(string? barcode)
    {
        var raw = Enumerable.Repeat((byte)' ', 80).ToArray();
        WriteAscii(raw, 0, "VOL", 3);
        raw[3] = (byte)'1';
        WriteAscii(raw, 4, (barcode ?? string.Empty).ToUpperInvariant(), 6);
        raw[10] = (byte)'L';
        WriteAscii(raw, 24, "LTFS", 13);
        raw[79] = (byte)'4';
        return raw;
    }

    private static void WriteAscii(byte[] buffer, int offset, string value, int length)
    {
        var padded = value.PadRight(length)[..length];
        Encoding.ASCII.GetBytes(padded, buffer.AsSpan(offset, length));
    }
}
