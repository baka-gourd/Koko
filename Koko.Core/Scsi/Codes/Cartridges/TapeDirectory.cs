namespace Koko.Core.Scsi.Codes.Cartridges;

public sealed class TapeDirectory
{
    public byte Version { get; set; }

    public int FidTapeWritePassPartition0 { get; set; }
    public int FidTapeWritePassPartition1 { get; set; }
    public int FidTapeWritePassPartition2 { get; set; }
    public int FidTapeWritePassPartition3 { get; set; }

    public string Wrap { get; set; } = string.Empty;

    public List<WrapEntryItemSet> WrapEntryInfo { get; } = new();
    public List<double> CapacityLoss { get; } = new();
    public List<Dataset> DatasetsOnWrapData { get; } = new();

    public sealed record class Dataset
    {
        public int Index { get; set; }
        public int Data { get; set; }
    }

    public sealed record class WrapEntryItemSet
    {
        public int Index { get; set; }
        public string Content { get; set; } = string.Empty;
        public int[] RawData { get; set; } = Array.Empty<int>();
        public int RecCount { get; set; }
        public int FileMarkCount { get; set; }
    }

    public WrapEntryItemSet? GetWrapEntry(int index, bool createNew = true)
    {
        foreach (var d in WrapEntryInfo)
        {
            if (d.Index == index)
                return d;
        }

        if (!createNew)
            return null;

        var dn = new WrapEntryItemSet { Index = index };
        WrapEntryInfo.Add(dn);
        return dn;
    }

    public Dataset? GetDatasetsOnWrap(int index, bool createNew = true)
    {
        foreach (var d in DatasetsOnWrapData)
        {
            if (d.Index == index)
                return d;
        }

        if (!createNew)
            return null;

        var dn = new Dataset { Index = index };
        DatasetsOnWrapData.Add(dn);
        return dn;
    }
}