using System.Text;

using Koko.Core.Ltfs;

namespace Koko.Core.Tests;

public sealed class LtfsSchemaTests
{
    [Test]
    public async Task Reader_accepts_legacy_reduced_schema()
    {
        var index = ReadMinimalSchema();

        await Assert.That(index.Version).IsEqualTo("2.4.0");
        await Assert.That(index.VolumeUuid).IsEqualTo(Guid.Parse("129fa6c4-b043-4286-9188-0c588a94ad89"));
        await Assert.That(index.GenerationNumber).IsEqualTo(10UL);
        await Assert.That(index.Location.Partition).IsEqualTo(LtfsPartition.B);
        await Assert.That(index.Location.StartBlock).IsEqualTo(623202UL);
        await Assert.That(index.RootDirectory?.Name).IsEqualTo("S00007L6");

        var file = index.RootDirectory!.Directories[0].Files[0];
        await Assert.That(file.Name).IsEqualTo("readme.txt");
        await Assert.That(file.OpenForWrite).IsFalse();
        await Assert.That(file.GetExtendedAttribute("ltfs.hash.sha1sum")).IsEqualTo("0123456789ABCDEF0123456789ABCDEF01234567");
        await Assert.That(file.Extents[0].StartBlock).IsEqualTo(7L);
    }

    [Test]
    public async Task Validator_accepts_minimal_schema_fixture()
    {
        var index = ReadMinimalSchema();

        var result = LtfsIndexValidator.ValidateInternal(index, new LtfsIndexValidationOptions(LtfsBlockSizeBytes: 512 * 1024));

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Writer_outputs_legacy_reduced_schema_without_wrappers()
    {
        var index = ReadMinimalSchema();
        using var stream = new MemoryStream();

        LtfsSchemaWriter.Write(stream, index, new LtfsSchemaWriterOptions(LeaveOpen: true));

        var xml = Encoding.UTF8.GetString(stream.ToArray());
        await Assert.That(xml.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", StringComparison.Ordinal)).IsTrue();
        await Assert.That(xml.Contains("<ltfsindex version=\"2.4.0\">", StringComparison.Ordinal)).IsTrue();
        await Assert.That(xml.Contains("<_directory", StringComparison.Ordinal)).IsFalse();
        await Assert.That(xml.Contains("<_file", StringComparison.Ordinal)).IsFalse();

        stream.Position = 0;
        var roundTrip = LtfsSchemaReader.Read(stream);
        await Assert.That(roundTrip.GenerationNumber).IsEqualTo(index.GenerationNumber);
        await Assert.That(roundTrip.RootDirectory?.Directories[0].Files[0].Length).IsEqualTo(12L);
    }

    [Test]
    public async Task Strict_wrapped_schema_round_trips()
    {
        var index = ReadMinimalSchema();
        using var stream = new MemoryStream();

        LtfsSchemaWriter.Write(stream, index, new LtfsSchemaWriterOptions(LtfsSchemaWriteMode.StrictWrapped, LeaveOpen: true));

        var xml = Encoding.UTF8.GetString(stream.ToArray());
        await Assert.That(xml.Contains("<_directory>", StringComparison.Ordinal)).IsTrue();
        await Assert.That(xml.Contains("<_file>", StringComparison.Ordinal)).IsTrue();

        stream.Position = 0;
        var roundTrip = LtfsSchemaReader.Read(stream);
        await Assert.That(roundTrip.RootDirectory?.Directories[0].Files[0].Name).IsEqualTo("readme.txt");
    }

    private static LtfsIndex ReadMinimalSchema()
    {
        using var stream = File.OpenRead(TestDataPath("minimal-ltfs.schema"));
        return LtfsSchemaReader.Read(stream);
    }

    private static string TestDataPath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Data", fileName);
    }
}
