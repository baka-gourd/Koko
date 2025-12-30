namespace Koko.Core.Scsi.Codes.Cartridges;

public readonly record struct TapeStatus(
    int ThreadCount,
    bool EncryptedData,
    int LastLocation
)
{
    public static TapeStatus Create(
        int threadCount,
        bool encryptedData,
        int lastLocation = 0)
        => new(threadCount, encryptedData, lastLocation);
}