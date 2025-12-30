namespace Koko.Core.Scsi.Codes.Cartridges;

public sealed class SuspendWrite
{
    public List<DataInfo> DataSetList { get; } = [];
    public List<DataInfo> WTapePassList { get; } = [];

    public sealed record class DataInfo
    {
        public int Index { get; set; }
        public int Value { get; set; }
    }

    public DataInfo? GetDataSetId(int index, bool createNew = true)
    {
        foreach (var di in DataSetList.Where(di => di.Index == index))
        {
            return di;
        }

        if (!createNew)
            return null;

        var din = new DataInfo { Index = index };
        DataSetList.Add(din);
        return din;
    }

    public DataInfo? GetWTapePass(int index, bool createNew = true)
    {
        foreach (var di in WTapePassList.Where(di => di.Index == index))
        {
            return di;
        }

        if (!createNew)
            return null;

        var din = new DataInfo { Index = index };
        WTapePassList.Add(din);
        return din;
    }
}