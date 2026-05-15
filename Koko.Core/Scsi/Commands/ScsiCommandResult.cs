namespace Koko.Core.Scsi.Commands;

public readonly record struct ScsiCommandResult(
    bool Success,
    byte ScsiStatus,
    uint BytesReturned,
    byte[] SenseData,
    ScsiTransportError? TransportError = null)
{
    public bool IsGood => Success && ScsiStatus == 0;

    public static ScsiCommandResult From(
        bool success,
        byte scsiStatus,
        uint bytesReturned,
        byte[] senseData,
        ScsiTransportError? transportError = null)
        => new(success, scsiStatus, bytesReturned, senseData, transportError);
}
