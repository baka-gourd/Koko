using BenchmarkDotNet.Attributes;

using Koko.Core.Scsi.Parsers;

namespace Koko.Demo;

[MemoryDiagnoser]
public class CmBench
{
    private byte[] _data = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = File.ReadAllBytes("R:/cm.bin");
        _ = CMParser.CreateFromSpan(_data); // warmup
    }

    [Benchmark]
    public CMParser Parse() => CMParser.CreateFromSpan(_data);
}
