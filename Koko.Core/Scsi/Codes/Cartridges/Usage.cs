namespace Koko.Core.Scsi.Codes.Cartridges;

public record Usage(
    int Index,
    int PageID,
    string DriveSN,
    int ThreadCount,
    long SetsWritten,
    long SetsRead,
    long TotalSets,
    int WriteRetries,
    int ReadRetries,
    int UnRecovWrites,
    int UnRecovReads,
    int SuspendedWrites,
    int FatalSusWrites,
    int SuspendedAppendWrites,
    int LP3Passes,
    int MidpointPasses,
    int MaxTapeTemp,

    int CCQWriteFails,
    int C2RecovErrors,
    int DirectionChanges,
    int TapePullingTime,
    int TapeMetresPulled,
    int Repositions,
    int TotalLoadUnloads,
    int StreamFails,

    double MaxDriveTemp,
    double MinDriveTemp,

    long LifeSetsWritten,
    long LifeSetsRead,
    int LifeWriteRetries,
    int LifeReadRetries,
    int LifeUnRecoverWrites,
    int LifeUnRecoverReads,
    int LifeSuspendedWrites,
    int LifeFatalSuspendWrites,
    int LifeTapeMetresPulled,

    int LifeSuspendAppendWrites,
    int LifeLP3Passes,
    int LifeMidpointPasses
);
