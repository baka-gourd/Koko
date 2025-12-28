using System.Collections.Frozen;

namespace Koko.Core.Scsi.Codes;

public static class AdditionalSenseCode
{
    private static readonly FrozenDictionary<ushort, string> Map =
        new Dictionary<ushort, string>
        {
            { 0x0000, "No addition sense" },
            { 0x0001, "Filemark detected" },
            { 0x0002, "End of Tape detected" },
            { 0x0004, "Beginning of Tape detected" },
            { 0x0005, "End of Data detected" },
            { 0x0016, "Operation in progress" },
            { 0x0018, "Erase operation in progress" },
            { 0x0019, "Locate operation in progress" },
            { 0x001A, "Rewind operation in progress" },

            { 0x0400, "LUN not ready, cause not reportable" },
            { 0x0401, "LUN in process of becoming ready" },
            { 0x0402, "LUN not ready, Initializing command required" },
            { 0x0404, "LUN not ready, format in progress" },
            { 0x0407, "Command in progress" },
            { 0x0409, "LUN not ready, self-test in progress" },
            { 0x040C, "LUN not accessible, port in unavailable state" },
            { 0x0412, "Logical unit offline" },

            { 0x0800, "Logical unit communication failure" },

            { 0x0B00, "Warning" },
            { 0x0B01, "Thermal limit exceeded" },

            { 0x0C00, "Write error" },

            { 0x0E01, "Information unit too short" },
            { 0x0E02, "Information unit too long" },
            { 0x0E03, "SK Illegal Request" },

            { 0x1001, "Logical block guard check failed" },

            { 0x1100, "Unrecovered read error" },
            { 0x1112, "Media Auxiliary Memory read error" },

            { 0x1400, "Recorded entity not found" },
            { 0x1403, "End of Data not found" },

            { 0x1A00, "Parameter list length error" },

            { 0x2000, "Invalid command operation code" },

            { 0x2400, "Invalid field in Command Descriptor Block" },

            { 0x2500, "LUN not supported" },

            { 0x2600, "Invalid field in parameter list" },
            { 0x2601, "Parameter not supported" },
            { 0x2602, "Parameter value invalid" },
            { 0x2604, "Invalid release of persistent reservation" },
            { 0x2610, "Data decryption key fail limit reached" },
            { 0x2680, "Invalid CA certificate" },

            { 0x2700, "Write-protected" },
            { 0x2708, "Too many logical objects on partition to support operation" },

            { 0x2800, "Not ready to ready transition, medium may have changed" },

            { 0x2901, "Power-on reset" },
            { 0x2902, "SCSI bus reset" },
            { 0x2903, "Bus device reset" },
            { 0x2904, "Internal firmware reboot" },
            { 0x2907, "I_T nexus loss occurred" },

            { 0x2A01, "Mode parameters changed" },
            { 0x2A02, "Log parameters changed" },
            { 0x2A03, "Reservations pre-empted" },
            { 0x2A04, "Reservations released" },
            { 0x2A05, "Registrations pre-empted" },
            { 0x2A06, "Asymmetric access state changed" },
            { 0x2A07, "Asymmetric access state transition failed" },
            { 0x2A08, "Priority changed" },
            { 0x2A0D, "Data encryption capabilities changed" },
            { 0x2A10, "Timestamp changed" },
            { 0x2A11, "Data encryption parameters changed by another initiator" },
            { 0x2A12, "Data encryption parameters changed by a vendor-specific event" },
            { 0x2A13, "Data Encryption Key Instance Counter has changed" },
            { 0x2A14, "SA creation capabilities data has changed" },
            { 0x2A15, "Medium removal prevention pre-empted" },
            { 0x2A80, "Security configuration changed" },

            { 0x2C00, "Command sequence invalid" },
            { 0x2C07, "Previous busy status" },
            { 0x2C08, "Previous task set full status" },
            { 0x2C09, "Previous reservation conflict status" },
            { 0x2C0B, "Not reserved" },

            { 0x2F00, "Commands cleared by another initiator" },

            { 0x3000, "Incompatible medium installed" },
            { 0x3001, "Cannot read media, unknown format" },
            { 0x3002, "Cannot read media: incompatible format" },
            { 0x3003, "Cleaning cartridge installed" },
            { 0x3004, "Cannot write medium" },
            { 0x3005, "Cannot write medium, incompatible format" },
            { 0x3006, "Cannot format, incompatible medium" },
            { 0x3007, "Cleaning failure" },
            { 0x300C, "WORM medium—overwrite attempted" },
            { 0x300D, "WORM medium—integrity check failed" },

            { 0x3100, "Medium format corrupted" },

            { 0x3700, "Rounded parameter" },

            { 0x3A00, "Medium not present" },
            { 0x3A04, "Medium not present, Media Auxiliary Memory accessible" },

            { 0x3B00, "Sequential positioning error" },
            { 0x3B0C, "Position past BOM" },
            { 0x3B1C, "Too many logical objects on partition to support operation." },

            { 0x3E00, "Logical unit has not self-configured yet" },

            { 0x3F01, "Microcode has been changed" },
            { 0x3F03, "Inquiry data has changed" },
            { 0x3F05, "Device identifier changed" },
            { 0x3F0E, "Reported LUNs data has changed" },
            { 0x3F0F, "Echo buffer overwritten" },

            { 0x4300, "Message error" },

            { 0x4400, "Internal target failure" },

            { 0x4500, "Selection/reselection failure" },

            { 0x4700, "SCSI parity error" },

            { 0x4800, "Initiator Detected Error message received" },

            { 0x4900, "Invalid message" },

            { 0x4B00, "Data phase error" },
            { 0x4B02, "Too much write data" },
            { 0x4B03, "ACK/NAK timeout" },
            { 0x4B04, "NAK received" },
            { 0x4B05, "Data offset error" },
            { 0x4B06, "Initiator response timeout" },

            { 0x4D00, "Tagged overlapped command" },

            { 0x4E00, "Overlapped commands" },

            { 0x5000, "Write append error" },

            { 0x5200, "Cartridge fault" },

            { 0x5300, "Media load or eject failed" },
            { 0x5301, "Unload tape failure" },
            { 0x5302, "Medium removal prevented" },
            { 0x5303, "Insufficient resources" },
            { 0x5304, "Medium thread or unthread failure" },

            { 0x5504, "Insufficient registration resources" },
            { 0x5506, "Media Auxiliary Memory full" },

            { 0x5B01, "Threshold condition met" },

            { 0x5D00, "Failure prediction threshold exceeded" },
            { 0x5DFF, "Failure prediction threshold exceeded (false)" },

            { 0x5E01, "Idle condition activated by timer" },

            { 0x7400, "Security error" },
            { 0x7401, "Unable to decrypt data" },
            { 0x7402, "Unencrypted data encountered while decrypting" },
            { 0x7403, "Incorrect data encryption key" },
            { 0x7404, "Cryptographic integrity validation failed" },
            { 0x7405, "Key-associated data descriptors changed." },
            { 0x7408, "Digital signature validation failure" },
            { 0x7409, "Encryption mode mismatch on read" },
            { 0x740A, "Encrypted block not RAW read-enabled" },
            { 0x740B, "Incorrect encryption parameters" },
            { 0x7421, "Data encryption configuration prevented" },
            { 0x7440, "Authentication failed" },
            { 0x7461, "External data encryption Key Manager access error" },
            { 0x7462, "External data encryption Key Manager error" },
            { 0x7463, "External data encryption management—key not found" },
            { 0x7464, "External data encryption management—request not authorized" },
            { 0x746E, "External data encryption control time-out" },
            { 0x746F, "External data encryption control unknown error" },
            { 0x7471, "Logical Unit access not authorized" },
            { 0x7480, "KAD changed" },
            { 0x7482, "Crypto KAD in CM failure" },

            { 0x8282, "Drive requires cleaning" },
            { 0x8283, "Bad microcode detected" },
        }.ToFrozenDictionary();

    public static bool TryGet(byte asc, byte ascq, out string text)
        => Map.TryGetValue(MakeKey(asc, ascq), out text!);

    public static string GetOrUnknown(byte asc, byte ascq)
        => Map.TryGetValue(MakeKey(asc, ascq), out var text)
            ? text
            : $"UNKNOWN ASC/ASCQ 0x{asc:X2}/0x{ascq:X2}";

    public static ushort MakeKey(byte asc, byte ascq)
        => (ushort)((asc << 8) | ascq);
}