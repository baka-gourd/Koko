using Koko.Core.Scsi.Parsers;

namespace Koko.Core.Tests;

public sealed class CMParserTests
{
    [Test]
    public async Task Cm_fixture_parses_pages_and_reports_text()
    {
        var data = File.ReadAllBytes(TestDataPath("cm.bin"));

        var parser = CMParser.CreateFromSpan(data);

        await Assert.That(parser.PageData.Count).IsGreaterThan(0);
        await Assert.That(parser.GetModernReport().Length).IsGreaterThan(0);
        await Assert.That(parser.GetLegacyReport().Length).IsGreaterThan(0);
        await Assert.That(parser.GetCapacitySummary()).IsNotNull();
    }

    [Test]
    public async Task Cm_parser_rejects_truncated_buffer()
    {
        var action = () => CMParser.CreateFromSpan(new byte[128]);

        await Assert.That(action).ThrowsException();
    }

    private static string TestDataPath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Data", fileName);
    }
}
