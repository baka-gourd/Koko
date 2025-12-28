using System.Collections.Frozen;

namespace Koko.Core.Scsi.Codes;

public class TapeAlertFlagCode
{
    private static readonly FrozenDictionary<byte, string> Map =
       new Dictionary<byte, string>
       {
            { 1,  "Read" },
            { 2,  "Write" },
            { 3,  "Hard Error" },
            { 4,  "Medium" },
            { 5,  "Read Failure" },
            { 6,  "Write Failure" },
            { 7,  "Medium Life" },
            { 8,  "Not Data Grade" },
            { 9,  "Write-Protect" },
            { 10, "Volume Removal Prevented" },
            { 11, "Cleaning Volume" },
            { 12, "Unsupported Format" },
            { 13, "Recoverable Mechanical Cartridge Failure" },
            { 14, "Unrecoverable Mechanical Cartridge Failure" },
            { 15, "Memory Chip in Cartridge Failure" },
            { 16, "Forced Eject" },
            { 17, "Read-Only Format" },
            { 18, "Tape Directory Corrupted on Load" },
            { 19, "Nearing Medium Life" },
            { 20, "Cleaning Required" },
            { 21, "Cleaning Requested" },
            { 22, "Expired Cleaning Volume" },
            { 23, "Invalid Cleaning Volume" },
            { 24, "Retension Requested" },
            { 25, "Multi-port Interface Error on Primary Port" },
            { 26, "Cooling Fan Failure" },
            { 27, "Power Supply Failure" },
            { 28, "Power Consumption" },
            { 29, "Drive Preventative Maintenance Required" },
            { 30, "Hardware A" },
            { 31, "Hardware B" },
            { 32, "Primary Interface" },
            { 33, "Eject Media" },
            { 34, "Microcode Update Failure" },
            { 35, "Drive Humidity" },
            { 36, "Drive Temperature" },
            { 37, "Drive Voltage" },
            { 38, "Predictive Failure" },
            { 39, "Diagnostics Required" },

            { 49, "Diminished Native Capacity" },
            { 50, "Lost Statistics" },
            { 51, "Tape Directory Invalid at Unload" },
            { 52, "Tape System Area Write Failure" },
            { 53, "Tape System Area Read Failure" },
            { 54, "No Start of Data" },
            { 55, "Loading or Threading Failure" },
            { 56, "Unrecoverable Unload Failure" },
            { 57, "Automation Interface Failure" },
            { 58, "Microcode Failure" },
            { 59, "WORM Medium — Integrity Check Failed" },
            { 60, "WORM Medium — Overwrite Attempted" },
            { 61, "Encryption Policy Violation" },
       }.ToFrozenDictionary();

    public static bool TryGet(byte flag, out string text)
        => Map.TryGetValue(flag, out text!);

    public static string GetOrUnknown(byte flag)
        => Map.TryGetValue(flag, out var text)
            ? text
            : $"UNKNOWN TAPE ALERT ({flag})";
}