namespace Koko.Core.Scsi.Commands;

public readonly record struct ScsiCommandResult(
    bool Success,
    byte ScsiStatus,
    uint BytesReturned,
    byte[] SenseData)
{
    public bool IsGood => Success && ScsiStatus == 0;

    public static ScsiCommandResult From(
        bool success,
        byte scsiStatus,
        uint bytesReturned,
        byte[] senseData)
        => new(success, scsiStatus, bytesReturned, senseData);
}
