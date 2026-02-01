using Koko.Core.Scsi.Codes.Cartridges;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Humanizer;
using Koko.Core.Helpers;
using Serilog;

namespace Koko.Core.Scsi.Parsers;

public sealed class CMParser
{
    private const uint GuardWrapIdentifier = 0xFFFFFFFE;
    private const uint UnusedWrapIdentifier = 0xFFFFFFFF;
    private readonly ReadOnlyMemory<byte> _cm;

    private CMParser(ReadOnlyMemory<byte> cm)
    {
        _cm = cm;
    }

    // Raw cartridge type as read from CM page 1 (offset 22). Needed to rebuild immutable TapeCartridgeProfile.
    private ushort _cartridgeType;

    #region PUBLIC_PARSE_RESULTS

    public List<Page> PageData { get; } = new();
    public TapeCartridgeProfile? TapeCartridgeProfile { get; private set; }
    public MediaProfile? MediaMfgData { get; private set; }
    public List<Usage> UsageData { get; } = new();
    public TapeStatus? StatusData { get; private set; }
    public Initialisation? InitialisationData { get; private set; }
    public List<Eod> PartitionEod { get; } = new(); // partitions 0..3 if present
    public CartridgeContent? CartridgeContentData { get; private set; }
    public TapeDirectory TapeDirectoryData { get; } = new();
    public SuspendWrite SuspendWriteData { get; } = new();
    public ApplicationSpecific? ApplicationSpecificData { get; private set; }

    #endregion

    private int _nWraps; // a_NWraps
    private int _setsPerWrap; // a_SetsPerWrap
    private int _tapeDirEntryLen = 16; // a_TapeDirLength (16/28/32 etc)
    private int _hdrLen; // a_HdrLength

    private readonly Dictionary<int, Page> _pageById = new();

    public static CMParser CreateFromSpan(ReadOnlySpan<byte> buffer)
    {
        const int minCMBytes = 0x1000;

        if (buffer.Length < minCMBytes)
            throw new ArgumentException(
                $"Invalid CM buffer size: 0x{buffer.Length:X} (must be >= 0x{minCMBytes:X}).",
                nameof(buffer));

        // checksum: byte 4 is XOR of first four bytes
        var expected = (byte)(buffer[0] ^ buffer[1] ^ buffer[2] ^ buffer[3]);
        var actual = buffer[4];
        if (expected != actual)
            throw new FormatException(
                $"Invalid CM checksum byte: expected 0x{expected:X2}, actual 0x{actual:X2}.");

        // CM size byte at offset 5: in 1KiB units
        var cmSizeKib = buffer[5];
        if (cmSizeKib is not (4 or 8 or 16 or 32))
            throw new FormatException(
                $"Invalid CM size byte (offset 5): 0x{cmSizeKib:X2} (expected 0x04/0x08/0x10/0x20).");

        var cmSizeBytes = cmSizeKib * 1024;
        var minCMSizeBytes = cmSizeBytes;
        if (cmSizeKib >= 8)
            minCMSizeBytes -= 32;

        if (buffer.Length < minCMSizeBytes)
            throw new ArgumentException(
                $"CM buffer is truncated: size byte indicates {cmSizeBytes} bytes (minimum acceptable {minCMSizeBytes} bytes), but provided {buffer.Length} bytes.",
                nameof(buffer));

        // Only keep the exact CM region; callers may have provided extra bytes.
        var owned = GC.AllocateUninitializedArray<byte>(minCMSizeBytes);
        buffer[..minCMSizeBytes].CopyTo(owned);

        var parser = new CMParser(owned);

        parser.ParseCM();

        return parser;
    }

    private void ParseCM(Action<string>? warn = null)
    {
        warn ??= Log.Warning;

        PageData.Clear();
        _pageById.Clear();

        // 1) Parse page tables (protected + unprotected)
        ParsePageTables(warn);

        // 2) Parse key pages (mirrors VB flow at a high level)
        ParseCartridgeMfgPage(warn); // page id 1
        ParseMediaMfgPage(warn); // page id 2
        ParseUsagePagesIfPresent(warn); // 0x108..0x10B + 0x106
        ParseStatusPage(warn); // 0x105
        ParseInitialisationPage(warn); // 0x101
        ParseEodPages(warn); // 0x104 / 0x10E / 0x10F / 0x110
        ParseCartridgeContentIfPresent(warn); // 0x10D (LTO5+)
        ParseTapeDirectoryPage(warn); // 0x103
        ParseSuspendedWritesPage(warn); // 0x107
        ParseApplicationSpecificPage(warn); // 0x200 (MAM001/2 attribute list)
    }

    private void ParsePageTables(Action<string> warn)
    {
        var cm = _cm.Span;
        // VB:
        // a_Offset = 36 start of protected table
        // read 4-byte entries until page id == 0xFFF -> EOPT
        // first EOPT switches to unprotected table at pointer (word at offset+2)
        // second EOPT ends
        var offset = 36;
        var unprot = false;

        while (offset < 400 && offset + 4 <= cm.Length)
        {
            var tableWord0 = ReadUInt16BigEndian(cm, offset);
            var pageId = tableWord0 & 0x0FFF;

            if (pageId == 0x0FFF)
            {
                if (!unprot)
                {
                    unprot = true;
                    int unprotOffset = ReadUInt16BigEndian(cm, offset + 2);
                    offset = unprotOffset;
                    continue;
                }

                break; // end of unprotected table
            }

            // Empty/Pad
            if (pageId is 0x0FFC or 0x0FFE)
            {
                offset += 4;
                continue;
            }

            // Real page entry: version (high nibble of byte at offset), page offset at offset+2
            var version = (cm[offset] >> 4) & 0x0F;
            int pageOffset = ReadUInt16BigEndian(cm, offset + 2);

            // Read page header length word: in VB .Length = g_GetWord(a_CMBuffer, .Offset + 2)
            var pageLength = -1;
            if (pageOffset + 4 <= cm.Length)
                pageLength = ReadUInt16BigEndian(cm, pageOffset + 2);

            var p = new Page(
                Key: pageId,
                Version: version,
                Offset: pageOffset,
                Length: pageLength,
                Type: unprot ? PageType.Unprotected : PageType.Protected
            );

            // Header cross-check (VB warns if header doesn't match page-table entry)
            if (pageOffset + 2 <= cm.Length)
            {
                var headerWord0 = ReadUInt16BigEndian(cm, pageOffset);
                if (headerWord0 != tableWord0)
                    warn(
                        $"CM Page Header Error: Offset={pageOffset} expected=0x{tableWord0:X4} actual=0x{headerWord0:X4}");
            }

            PageData.Add(p);
            _pageById[pageId] = p;

            offset += 4;
        }
    }

    private bool TryGetPage(int pageId, out Page page)
        => _pageById.TryGetValue(pageId, out page);

    private bool TrySlicePage(Page page, out ReadOnlySpan<byte> span)
    {
        span = default;

        if (page.Offset < 0 || page.Length < 0)
            return false;

        var cm = _cm.Span;

        if ((uint)page.Offset > (uint)cm.Length)
            return false;
        if ((uint)page.Length > (uint)(cm.Length - page.Offset))
            return false;

        span = cm.Slice(page.Offset, page.Length);
        return true;
    }

    private void ParseCartridgeMfgPage(Action<string> warn)
    {
        if (!TryGetPage(1, out var p) || !TrySlicePage(p, out var page))
            return;

        // VB reads within the page body after header:
        // TapeVendor = getstr(a_Buffer, 4, 8)
        // CartridgeSN = getstr(a_Buffer, 12, 10)
        // CartridgeType = g_GetWord(a_Buffer, 22)
        // MfgDate = getstr(a_Buffer, 24, 8)
        // TapeLength = g_GetWord(a_Buffer, 32) (in 0.25m increments)
        // Particles = a_Buffer(42)
        // MediaCode = g_GetWord(a_Buffer, 46)

        var tapeVendor = GetAsciiTrim(page, 4, 8);
        var sn = GetAsciiTrim(page, 12, 10);
        var type = ReadUInt16BigEndian(page, 22);
        _cartridgeType = type;
        var mfgDate = GetAsciiTrim(page, 24, 8);
        var tapeLengthQuarterMetres = ReadUInt16BigEndian(page, 32);
        var mediaCode = ReadUInt16BigEndian(page, 46);

        var pageRevision = page.Length > 0 ? page[0] : (byte)0;
        var particles = page.Length > 42 ? page[42] : (byte)0;
        ParticleType particleType;
        var substrateType = SubstrateType.Unknown;
        // Particle/Substrate logic matches VB
        if (pageRevision >= 0x40)
        {
            particleType = (particles & 0x0F) != 0 ? ParticleType.BaFe : ParticleType.MP;
            substrateType = (particles & 0xF0) == 0x10 ? SubstrateType.SPALTAN : SubstrateType.PEN;
        }
        else
        {
            particleType = particles != 0 ? ParticleType.BaFe : ParticleType.MP;
        }

        // Determine Format + derived parameters
        var format = ComputeFormatAndDerived(type, warn);

        TapeCartridgeProfile = new TapeCartridgeProfile(
            cartridgeType: type,
            format: format,
            vendor: tapeVendor,
            sn: sn,
            particleType: particleType,
            substrateType: substrateType,
            manufacturingDate: mfgDate,
            tapeLengthQuarterMetres: tapeLengthQuarterMetres,
            mediaCode: mediaCode);
        return;

        string ComputeFormatAndDerived(int cartridgeType, Action<string> warn)
        {
            // Cleaning tape
            if (((cartridgeType >> 15) & 1) == 1)
            {
                _nWraps = 0;
                _setsPerWrap = 0;
                _tapeDirEntryLen = 16;
                return "Cleaning Tape";
            }

            if (cartridgeType == 1)
            {
                _nWraps = 48;
                _setsPerWrap = 5500;
                _tapeDirEntryLen = 16;
                return "LTO-1";
            }

            if (cartridgeType == 2)
            {
                _nWraps = 64;
                _setsPerWrap = 8200;
                _tapeDirEntryLen = 28;
                return "LTO-2";
            }

            var low = cartridgeType & 0xFF;
            var fmt = low switch
            {
                4 => SetDerived("LTO-3", nWraps: 44, setsPerWrap: 6000, tapeDirLen: 32),
                8 => SetDerived("LTO-4", nWraps: 56, setsPerWrap: 9500, tapeDirLen: 32),
                16 => SetDerived("LTO-5", nWraps: 80, setsPerWrap: 7800, tapeDirLen: 32),
                32 => SetDerived("LTO-6", nWraps: 136, setsPerWrap: 7805, tapeDirLen: 32),
                64 => SetDerived("LTO-7", nWraps: 112, setsPerWrap: 10950, tapeDirLen: 32),
                128 => SetDerived("LTO-8", nWraps: 208, setsPerWrap: 11660, tapeDirLen: 32),
                129 => SetDerived("LTO-9", nWraps: 280, setsPerWrap: 6770, tapeDirLen: 32),
                _ => ""
            };

            if (fmt.Length == 0)
            {
                // For HP LTO path, we treat non-LTO as unknown here.
                _nWraps = 0;
                _setsPerWrap = 0;
                _tapeDirEntryLen = 16;
                return "";
            }

            // WORM bit (VB: (CartridgeType >> 13) & 1)
            if (((cartridgeType >> 13) & 1) == 1)
                fmt += " WORM";

            // LTO-7 Type M is determined later via cartridge content page in VB;
            // we keep base "LTO-7" here, and adjust to "LTO-7 Type M" when page 0x10D indicates Type M.
            return fmt;

            string SetDerived(string label, int nWraps, int setsPerWrap, int tapeDirLen)
            {
                _nWraps = nWraps;
                _setsPerWrap = setsPerWrap;
                _tapeDirEntryLen = tapeDirLen;
                return label;
            }
        }
    }

    private void ParseMediaMfgPage(Action<string> warn)
    {
        if (!TryGetPage(2, out var p) || !TrySlicePage(p, out var page))
            return;

        var version = (byte)((page[0] >> 4) & 0x0F);

        var suboffset = 0;
        if (version >= 8)
            suboffset = 2; // VB: For 3592 CM; harmless for LTO if present

        var mediaProfileDate = GetAsciiTrim(page, 4 + suboffset, 8);
        var mediaVendor = GetAsciiTrim(page, 12 + suboffset, 8);

        // `MediaProfile` is a positional record. Publish via a single assignment.
        MediaMfgData = new MediaProfile(version, mediaProfileDate, mediaVendor);

        // Servo band ID heuristics for LTO-8 (VB tries/catches)
        // TapeCartridgeProfile is treated as immutable-ish; rebuild when we need to adjust ServoBandId.
        if (TapeCartridgeProfile is not null
            && TapeCartridgeProfile.Format?.Contains("LTO-8", StringComparison.OrdinalIgnoreCase) == true)
        {
            var servoBandId = ServoBandId.Unknown;

            if (MediaMfgData.MediaProfileDate.StartsWith("22", StringComparison.Ordinal))
                servoBandId = ServoBandId.LegacyUDIM;
            else if (MediaMfgData.MediaVendor.StartsWith(">>", StringComparison.Ordinal))
                servoBandId = ServoBandId.NonUDIM;

            if (servoBandId != ServoBandId.Unknown)
            {
                TapeCartridgeProfile = new TapeCartridgeProfile(
                    cartridgeType: _cartridgeType,
                    format: TapeCartridgeProfile.Format,
                    vendor: TapeCartridgeProfile.Vendor,
                    sn: TapeCartridgeProfile.SN,
                    particleType: TapeCartridgeProfile.ParticleType,
                    substrateType: TapeCartridgeProfile.SubstrateType,
                    manufacturingDate: TapeCartridgeProfile.ManufacturingDate,
                    tapeLengthQuarterMetres: TapeCartridgeProfile.TapeLengthQuarterMetres,
                    mediaCode: TapeCartridgeProfile.MediaCode,
                    servoBandId: servoBandId);
            }
        }
    }

    private void ParseUsagePagesIfPresent(Action<string> warn)
    {
        var cm = _cm.Span;
        UsageData.Clear();

        if (TapeCartridgeProfile is null)
            return;

        // Need cartridge format to decide offsets (LTO5+ changes)
        var isLTO5Plus = TapeCartridgeProfile.IsLaterThan(LTODensityCode.L5);

        ReadOnlySpan<int> atOffset = isLTO5Plus
            ? [32, 36, 44, 52, 56, 60, 62, 64, 66, 80]
            : [24, 28, 36, 44, 48, 52, 54, 56, 58];

        var driveSnLength = isLTO5Plus ? 16 : 10;

        // a_Length defaults 0x40, but may be overridden by page 0x108 length
        var usagePageLen = 0x40;
        if (TryGetPage(0x108, out var p108) && p108.Length >= 0)
            usagePageLen = p108.Length;

        // Mech Related vendor ID is stored at page 0x106 offset+4 len8 (after header)
        var mechVendorId = "";
        if (TryGetPage(0x106, out var p106) && p106.Offset >= 0 && p106.Length >= 0)
        {
            var off = p106.Offset + 4;
            if (off + 8 <= _cm.Length)
                mechVendorId = Encoding.ASCII.GetString(cm.Slice(off, 8)).TrimEnd();
        }

        // Read 4 usage pages (0..3), each appended with sub-page from 0x106 (12 + 64*i)
        var rawPages = new List<UsageSnapshot>(capacity: 4);

        for (var i = 0; i < 4; i++)
        {
            var pageId = 0x108 + i;

            if (!TryGetPage(pageId, out var up) || !TryGetPage(0x106, out var mp))
                continue;

            if (up.Offset < 0 || up.Length < 0 || mp.Offset < 0 || mp.Length < 0)
                continue;

            if ((uint)up.Offset > (uint)cm.Length || (uint)usagePageLen > (uint)(cm.Length - up.Offset))
                continue;

            // mech sub-page (always 64 bytes)
            var mechSubOff = mp.Offset + 12 + 64 * i;
            if ((uint)mechSubOff > (uint)cm.Length || 64u > (uint)(cm.Length - mechSubOff))
                continue;

            var usageSpan = cm.Slice(up.Offset, usagePageLen);

            var threadCount = ReadI32Be(usageSpan, atOffset[0]);

            rawPages.Add(new UsageSnapshot(
                Index: i,
                UsageOffset: up.Offset,
                UsageLength: usagePageLen,
                MechOffset: mechSubOff,
                ThreadCount: threadCount));
        }

        if (rawPages.Count < 4)
            return;

        // Reverse sort by thread count (VB: b.data1.CompareTo(a.data1))
        rawPages.Sort(static (x, y) => y.ThreadCount.CompareTo(x.ThreadCount));

        // VB reindexes after sort
        // We will interpret raw_pages[0..3] as current..previous
        // and fill UsageData[0..2] with deltas vs [i+1]
        for (var i = 0; i < 3; i++)
        {
            var curSnap = rawPages[i];
            var prevSnap = rawPages[i + 1];

            var curUsage = cm.Slice(curSnap.UsageOffset, curSnap.UsageLength);
            var prevUsage = cm.Slice(prevSnap.UsageOffset, prevSnap.UsageLength);

            var curMech = cm.Slice(curSnap.MechOffset, 64);
            var prevMech = cm.Slice(prevSnap.MechOffset, 64);

            // `Usage` is a positional record (init-only / immutable intent). Build from locals and publish once.
            int pageId = ReadUInt16BigEndian(curUsage, 0);
            var driveSn = ParseDriveSn(curUsage, 12, driveSnLength);

            var threadCount = ReadI32Be(curUsage, atOffset[0]);
            var setsWritten = ReadUInt64BigEndian(curUsage, atOffset[1]) - ReadUInt64BigEndian(prevUsage, atOffset[1]);
            var setsRead = ReadUInt64BigEndian(curUsage, atOffset[2]) - ReadUInt64BigEndian(prevUsage, atOffset[2]);
            var totalSets = ReadUInt64BigEndian(curUsage, atOffset[1]) + ReadUInt64BigEndian(curUsage, atOffset[2]);
            var writeRetries = ReadI32Be(curUsage, atOffset[3]) - ReadI32Be(prevUsage, atOffset[3]);
            var readRetries = ReadI32Be(curUsage, atOffset[4]) - ReadI32Be(prevUsage, atOffset[4]);
            var unRecovWrites = ReadUInt16BigEndian(curUsage, atOffset[5]) -
                                ReadUInt16BigEndian(prevUsage, atOffset[5]);
            var unRecovReads = ReadUInt16BigEndian(curUsage, atOffset[6]) - ReadUInt16BigEndian(prevUsage, atOffset[6]);
            var suspendedWrites =
                ReadUInt16BigEndian(curUsage, atOffset[7]) - ReadUInt16BigEndian(prevUsage, atOffset[7]);
            var fatalSusWrites =
                ReadUInt16BigEndian(curUsage, atOffset[8]) - ReadUInt16BigEndian(prevUsage, atOffset[8]);

            var lifeSetsWritten = ReadUInt64BigEndian(curUsage, atOffset[1]);
            var lifeSetsRead = ReadUInt64BigEndian(curUsage, atOffset[2]);
            var lifeWriteRetries = ReadI32Be(curUsage, atOffset[3]);
            var lifeReadRetries = ReadI32Be(curUsage, atOffset[4]);
            int lifeUnRecoverWrites = ReadUInt16BigEndian(curUsage, atOffset[5]);
            int lifeUnRecoverReads = ReadUInt16BigEndian(curUsage, atOffset[6]);
            int lifeSuspendedWrites = ReadUInt16BigEndian(curUsage, atOffset[7]);
            int lifeFatalSuspendWrites = ReadUInt16BigEndian(curUsage, atOffset[8]);
            var lifeTapeMetresPulled = 0;

            // LTO5+ extended fields: only if cur[76] > 0 per VB
            var suspendedAppendWrites = 0;
            var lp3Passes = 0;
            var midpointPasses = 0;
            var maxTapeTemp = 0;
            var lifeSuspendAppendWrites = 0;
            var lifeLp3Passes = 0;
            var lifeMidpointPasses = 0;

            if (isLTO5Plus && curUsage.Length > 76 && curUsage[76] > 0)
            {
                suspendedAppendWrites = ReadUInt16BigEndian(curUsage, 28) - ReadUInt16BigEndian(prevUsage, 28);
                lp3Passes = ReadI32Be(curUsage, 68) - ReadI32Be(prevUsage, 68);
                midpointPasses = ReadI32Be(curUsage, 72) - ReadI32Be(prevUsage, 72);
                maxTapeTemp = curUsage[76];
                lifeSuspendAppendWrites = ReadUInt16BigEndian(curUsage, 28);
                lifeLp3Passes = ReadI32Be(curUsage, 68);
                lifeMidpointPasses = ReadI32Be(curUsage, 72);
            }

            // HP mech-related block is appended after usage_page_len
            var ccqWriteFails = 0;
            var c2RecoverErrors = 0;
            var directionChanges = 0;
            var tapePullingTime = 0;
            var tapeMetresPulled = 0;
            var repositions = 0;
            var totalLoadUnloads = 0;
            var streamFails = 0;
            double maxDriveTemp = 0;
            double minDriveTemp = 0;

            if (!string.IsNullOrEmpty(mechVendorId) && mechVendorId.Contains("HP", StringComparison.OrdinalIgnoreCase))
            {
                var ccqWriteFailsRaw = ReadUInt64BigEndian(curMech, 0) - ReadUInt64BigEndian(prevMech, 0);
                ccqWriteFails = ccqWriteFailsRaw <= 0
                    ? 0
                    : (ccqWriteFailsRaw > int.MaxValue ? int.MaxValue : (int)ccqWriteFailsRaw);

                c2RecoverErrors = ReadI32Be(curMech, 8) - ReadI32Be(prevMech, 8);
                directionChanges = ReadI32Be(curMech, 24) - ReadI32Be(prevMech, 24);
                tapePullingTime = ReadI32Be(curMech, 28) - ReadI32Be(prevMech, 28);

                // 保留你原逻辑：tapeMetresPulled 取当前值（不是 delta）
                tapeMetresPulled = ReadI32Be(curMech, 32);

                repositions = ReadI32Be(curMech, 36) - ReadI32Be(prevMech, 36);

                // 保留你原逻辑：totalLoadUnloads 取当前值（不是 delta）
                totalLoadUnloads = ReadI32Be(curMech, 40);

                streamFails = ReadI32Be(curMech, 44) - ReadI32Be(prevMech, 44);

                var maxDriveTempRaw = ReadUInt16BigEndian(curMech, 48);
                var minDriveTempRaw = ReadUInt16BigEndian(curMech, 50);
                if (maxDriveTempRaw > 0) maxDriveTemp = maxDriveTempRaw / 256.0;
                if (minDriveTempRaw > 0) minDriveTemp = minDriveTempRaw / 256.0;

                // Clamp negatives to 0 (VB does that)
                if (c2RecoverErrors < 0) c2RecoverErrors = 0;
                if (directionChanges < 0) directionChanges = 0;
                if (tapePullingTime < 0) tapePullingTime = 0;
                if (tapeMetresPulled < 0) tapeMetresPulled = 0;
                if (repositions < 0) repositions = 0;
                if (streamFails < 0) streamFails = 0;

                // LifeTapeMetresPulled only when at_offset has 10 entries (LTO5+)
                if (atOffset.Length >= 10)
                    lifeTapeMetresPulled = ReadI32Be(curUsage, atOffset[9]);
            }

            // Clamp core counters to >= 0
            if (threadCount < 0) threadCount = 0;
            if (setsWritten < 0) setsWritten = 0;
            if (setsRead < 0) setsRead = 0;
            if (writeRetries < 0) writeRetries = 0;
            if (readRetries < 0) readRetries = 0;
            if (unRecovWrites < 0) unRecovWrites = 0;
            if (unRecovReads < 0) unRecovReads = 0;
            if (suspendedWrites < 0) suspendedWrites = 0;
            if (fatalSusWrites < 0) fatalSusWrites = 0;

            if (suspendedAppendWrites < 0) suspendedAppendWrites = 0;
            if (lp3Passes < 0) lp3Passes = 0;
            if (midpointPasses < 0) midpointPasses = 0;
            if (lifeLp3Passes < 0) lifeLp3Passes = 0;
            if (lifeMidpointPasses < 0) lifeMidpointPasses = 0;

            if (lifeTapeMetresPulled < 0) lifeTapeMetresPulled = 0;

            UsageData.Add(new Usage(
                Index: i,
                PageID: pageId,
                DriveSN: driveSn,
                ThreadCount: threadCount,
                SetsWritten: setsWritten,
                SetsRead: setsRead,
                TotalSets: totalSets,
                WriteRetries: writeRetries,
                ReadRetries: readRetries,
                UnRecovWrites: unRecovWrites,
                UnRecovReads: unRecovReads,
                SuspendedWrites: suspendedWrites,
                FatalSusWrites: fatalSusWrites,
                SuspendedAppendWrites: suspendedAppendWrites,
                LP3Passes: lp3Passes,
                MidpointPasses: midpointPasses,
                MaxTapeTemp: maxTapeTemp,
                CCQWriteFails: ccqWriteFails,
                C2RecovErrors: c2RecoverErrors,
                DirectionChanges: directionChanges,
                TapePullingTime: tapePullingTime,
                TapeMetresPulled: tapeMetresPulled,
                Repositions: repositions,
                TotalLoadUnloads: totalLoadUnloads,
                StreamFails: streamFails,
                MaxDriveTemp: maxDriveTemp,
                MinDriveTemp: minDriveTemp,
                LifeSetsWritten: lifeSetsWritten,
                LifeSetsRead: lifeSetsRead,
                LifeWriteRetries: lifeWriteRetries,
                LifeReadRetries: lifeReadRetries,
                LifeUnRecoverWrites: lifeUnRecoverWrites,
                LifeUnRecoverReads: lifeUnRecoverReads,
                LifeSuspendedWrites: lifeSuspendedWrites,
                LifeFatalSuspendWrites: lifeFatalSuspendWrites,
                LifeTapeMetresPulled: lifeTapeMetresPulled,
                LifeSuspendAppendWrites: lifeSuspendAppendWrites,
                LifeLP3Passes: lifeLp3Passes,
                LifeMidpointPasses: lifeMidpointPasses));
        }

        return;

        static string ParseDriveSn(ReadOnlySpan<byte> data, int offset, int len)
        {
            // VB: if word at 12 != 0 then read string; strip, if >10 keep last 10
            if (offset + 2 <= data.Length)
            {
                var w = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
                if (w == 0) return "";
            }

            if (offset + len > data.Length) return "";

            var sn = Encoding.ASCII.GetString(data.Slice(offset, len)).TrimEnd();
            if (sn.Length > 10)
                sn = sn[^10..];
            return sn;
        }
    }

    private void ParseStatusPage(Action<string> warn)
    {
        if (!TryGetPage(0x105, out var p) || !TrySlicePage(p, out var page))
            return;

        if (TapeCartridgeProfile is null)
            return;

        var threadCount = ReadI32Be(page, 12);

        // Encryption detection for LTO4+ (VB checks 3 consecutive WORDs all 0xFFFF)
        var encryptedData = false;
        if (TapeCartridgeProfile.IsLaterThan(LTODensityCode.L4))
        {
            var w22 = ReadUInt16BigEndian(page, 22);
            var w24 = ReadUInt16BigEndian(page, 24);
            var w26 = ReadUInt16BigEndian(page, 26);
            encryptedData = !(w22 == 0xFFFF && w24 == 0xFFFF && w26 == 0xFFFF);
        }

        // Cleaning tape last location (bytes 26-27)
        var lastLocation = 0;
        if (!string.IsNullOrEmpty(TapeCartridgeProfile.Format)
            && TapeCartridgeProfile.Format.Contains("Clean", StringComparison.OrdinalIgnoreCase))
        {
            lastLocation = ReadUInt16BigEndian(page, 26);
        }

        // `TapeStatus` is a readonly record struct (immutable). Publish via a single assignment.
        StatusData = TapeStatus.Create(threadCount, encryptedData, lastLocation);
    }

    private void ParseInitialisationPage(Action<string> warn)
    {
        if (!TryGetPage(0x101, out var p) || !TrySlicePage(p, out var page))
            return;

        if (TapeCartridgeProfile is null)
            return;

        var isLTO5Plus = TapeCartridgeProfile.IsLaterThan(LTODensityCode.L5);
        var atOffset = isLTO5Plus ? new[] { 28, 32, 40, 48 } : new[] { 22, 24, 32, 40 };

        var lp1 = ReadI32Be(page, atOffset[1]);
        var lp3 = ReadI32Be(page, atOffset[2]);
        var lp5 = ReadI32Be(page, atOffset[3]);

        // `Initialisation` is a readonly record struct (immutable). Publish via a single assignment.
        InitialisationData = new Initialisation(lp1, lp3, lp5);
    }

    private void ParseEodPages(Action<string> warn)
    {
        PartitionEod.Clear();

        ReadOnlySpan<int> pageIds = [0x104, 0x10E, 0x10F, 0x110];
        for (var part = 0; part < 4; part++)
        {
            var pid = pageIds[part];
            if (!TryGetPage(pid, out var p) || !TrySlicePage(p, out var page))
                continue;

            // `Eod` is a readonly record struct (immutable). Construct via primary constructor.
            var e = new Eod(
                Partition: part,
                Dataset: ReadI32Be(page, 24),
                WrapNumber: ReadI32Be(page, 28),
                Validity: ReadUInt16BigEndian(page, 32),
                PhysicalPosition: ReadI32Be(page, 36));
            PartitionEod.Add(e);
        }
    }

    private void ParseCartridgeContentIfPresent(Action<string> warn)
    {
        if (TapeCartridgeProfile is null || !TapeCartridgeProfile.IsLaterThan(LTODensityCode.L5))
            return;

        if (!TryGetPage(0x10D, out var p) || !TrySlicePage(p, out var page))
            return;

        // `CartridgeContent` is a readonly record struct (immutable).
        // Parse into locals and publish once at the end.
        var driveId = GetAsciiTrim(page, 12, 16);
        int cartridgeContentCode = ReadUInt16BigEndian(page, 28);

        // VB: PartitionedCartridge = a_Buffer(28) >> 3 And 1
        var partitionedCartridge = page.Length > 28 && ((page[28] >> 3) & 1) == 1;

        var typeMCartridge = false;
        if (TapeCartridgeProfile.IsLaterThan(LTODensityCode.L7) && page.Length > 28)
            typeMCartridge = (page[28] & 1) == 1;

        // Firmware ID offset varies for LTO-5
        var fwOff = TapeCartridgeProfile.Id.LtoDensity != null &&
                    TapeCartridgeProfile.Id.LtoDensity.Value.Equals(LTODensityCode.L5)
            ? 48
            : 52;
        var driveFirmwareId = GetAsciiTrim(page, fwOff, 4);

        CartridgeContentData = new CartridgeContent(
            DriveId: driveId,
            CartridgeContentCode: cartridgeContentCode,
            PartitionedCartridge: partitionedCartridge,
            TypeMCartridge: typeMCartridge,
            DriveFirmwareId: driveFirmwareId);

        // VB: if LTO-7 and TypeM => "LTO-7 Type M" and wraps=168
        // TapeCartridgeProfile is treated as immutable; do NOT mutate Format in-place.
        if (TapeCartridgeProfile.Id.LtoDensity != null &&
            TapeCartridgeProfile.Id.LtoDensity.Value.Equals(LTODensityCode.L7) && typeMCartridge)
        {
            _nWraps = 168;

            // Rebuild profile so CartridgeId resolution can take Type M into account (CartridgeTypeResolver uses `format`).
            // Preserve WORM suffix if present.
            var curFmt = TapeCartridgeProfile.Format;
            if (curFmt != null && !curFmt.Contains("Type M", StringComparison.OrdinalIgnoreCase))
            {
                var isWorm = curFmt.Contains("WORM", StringComparison.OrdinalIgnoreCase);
                var newFmt = isWorm ? "LTO-7 Type M WORM" : "LTO-7 Type M";

                TapeCartridgeProfile = new TapeCartridgeProfile(
                    cartridgeType: _cartridgeType,
                    format: newFmt,
                    vendor: TapeCartridgeProfile.Vendor,
                    sn: TapeCartridgeProfile.SN,
                    particleType: TapeCartridgeProfile.ParticleType,
                    substrateType: TapeCartridgeProfile.SubstrateType,
                    manufacturingDate: TapeCartridgeProfile.ManufacturingDate,
                    tapeLengthQuarterMetres: TapeCartridgeProfile.TapeLengthQuarterMetres,
                    mediaCode: TapeCartridgeProfile.MediaCode,
                    servoBandId: TapeCartridgeProfile.ServoBandId);
            }
        }
    }

    private void ParseTapeDirectoryPage(Action<string> warn)
    {
        if (!TryGetPage(0x103, out var p) || !TrySlicePage(p, out var page))
            return;

        // VB requires EOD partition 0 validity to proceed
        var eod0 = PartitionEod.FirstOrDefault(e => e.Partition == 0);
        if (eod0 == default || eod0.Validity == 0)
            return;

        TapeDirectoryData.WrapEntryInfo.Clear();
        TapeDirectoryData.CapacityLoss.Clear();
        TapeDirectoryData.DatasetsOnWrapData.Clear();

        TapeDirectoryData.Version = (byte)((page[0] >> 4) & 0x0F);

        // Determine header length by format generation
        // VB has multiple branches; for HP LTO we follow the LTO rules.
        if (TapeCartridgeProfile != null && TapeCartridgeProfile.IsLaterThan(LTODensityCode.L6))
        {
            _hdrLen = 48;
            TapeDirectoryData.FidTapeWritePassPartition0 = ReadI32Be(page, 4);
            TapeDirectoryData.FidTapeWritePassPartition1 = ReadI32Be(page, 8);
            TapeDirectoryData.FidTapeWritePassPartition2 = ReadI32Be(page, 12);
            TapeDirectoryData.FidTapeWritePassPartition3 = ReadI32Be(page, 16);
        }
        else
        {
            _hdrLen = 16;
            if (TapeCartridgeProfile != null && TapeCartridgeProfile.IsLaterThan(LTODensityCode.L4))
            {
                TapeDirectoryData.FidTapeWritePassPartition0 = ReadI32Be(page, 4);
                TapeDirectoryData.FidTapeWritePassPartition1 = ReadI32Be(page, 8);
            }
        }

        // Publish wrap entries (counts etc.)
        PublishTapeDirectoryPage(page);

        // CapacityLoss computation (VB logic)
        uint lastId = 0;
        var eods = PartitionEod.ToArray();

        var wrapsToRead = _nWraps;
        if (_tapeDirEntryLen > 0 && _hdrLen >= 0 && page.Length > _hdrLen)
        {
            var maxWrapsByLen = (page.Length - _hdrLen) / _tapeDirEntryLen;
            if (wrapsToRead > maxWrapsByLen)
                wrapsToRead = maxWrapsByLen;
        }
        else
        {
            wrapsToRead = 0;
        }

        for (var wi = 0; wi < wrapsToRead; wi++)
        {
            var entryOff = _hdrLen + _tapeDirEntryLen * wi;
            if (entryOff + 8 > page.Length)
                break;

            var setId = ReadUInt32BigEndian(page, entryOff + 4);

            switch (setId)
            {
                case UnusedWrapIdentifier:
                    TapeDirectoryData.CapacityLoss.Add(-1);
                    continue;
                case GuardWrapIdentifier:
                    TapeDirectoryData.CapacityLoss.Add(-3);
                    continue;
                case 0:
                    TapeDirectoryData.CapacityLoss.Add(0);
                    continue;
            }

            var isEodWrap = false;
            if (eods.Any(e => e.Validity != 0 && e.WrapNumber == wi))
            {
                TapeDirectoryData.CapacityLoss.Add(-2);
                isEodWrap = true;
            }

            if (!isEodWrap)
            {
                if (_setsPerWrap <= 0)
                {
                    TapeDirectoryData.CapacityLoss.Add(0);
                }
                else
                {
                    var loss = Math.Max(0, 100 * (1.0 - (setId - lastId) / (double)_setsPerWrap));
                    TapeDirectoryData.CapacityLoss.Add(loss);
                }

                lastId = setId;
            }
        }

        // DatasetsOnWrapData computation (VB second loop)
        lastId = 0;
        for (var wi = 0; wi < wrapsToRead; wi++)
        {
            var entryOff = _hdrLen + _tapeDirEntryLen * wi;
            if (entryOff + 8 > page.Length)
                break;

            var a = ReadUInt32BigEndian(page, entryOff + 4);
            int data;
            if (a == UnusedWrapIdentifier || a == GuardWrapIdentifier)
            {
                a = 0;
                data = 0;
            }
            else
            {
                data = (int)(a - lastId);
            }

            TapeDirectoryData.GetDatasetsOnWrap(wi, createNew: true)!.Data = data;
            lastId = a;
        }
    }

    private void PublishTapeDirectoryPage(ReadOnlySpan<byte> page)
    {
        // This is a direct translation of the VB’s “PublishTapeDirectoryPage” for LTO-1/2 and LTO-3+.
        // It fills TapeDirectoryData.WrapEntryInfo[wrapIndex].
        int wrapsInDrive;
        int hdr;

        if (TapeCartridgeProfile is { Id.LtoDensity: not null } &&
            TapeCartridgeProfile.Id.LtoDensity.Value.Equals(LTODensityCode.L1))
        {
            wrapsInDrive = 48;
            hdr = 16;
            for (var wi = 0; wi < wrapsInDrive; wi++)
            {
                var evenDs = ReadUInt32BigEndian(page, hdr);
                hdr += 4;
                var evenRc = ReadUInt32BigEndian(page, hdr);
                hdr += 4;
                var evenFm = ReadUInt32BigEndian(page, hdr);
                hdr += 4;
                var evenCrc = ReadUInt32BigEndian(page, hdr);
                hdr += 4;

                var oddDs = ReadUInt32BigEndian(page, hdr);
                hdr += 4;
                var oddRc = ReadUInt32BigEndian(page, hdr);
                hdr += 4;
                var oddFm = ReadUInt32BigEndian(page, hdr);
                hdr += 4;
                var oddCrc = ReadUInt32BigEndian(page, hdr);
                hdr += 4;

                var e = TapeDirectoryData.GetWrapEntry(wi, createNew: true)!;
                e.Content =
                    $"{evenDs,-12}{evenRc,-12}{evenFm,-12}{evenCrc,-12}{oddDs,-12}{oddRc,-12}{oddFm,-12}{oddCrc,-12}";
                e.RawData =
                [
                    (int) evenDs, (int) evenRc, (int) evenFm, (int) evenCrc, (int) oddDs, (int) oddRc, (int) oddFm,
                    (int) oddCrc
                ];
                e.RecCount = (int)(evenRc + oddRc);
                e.FileMarkCount = (int)(evenFm + oddFm);
            }

            return;
        }

        if (TapeCartridgeProfile is { Id.LtoDensity: not null } &&
            TapeCartridgeProfile.Id.LtoDensity.Value.Equals(LTODensityCode.L2))
        {
            wrapsInDrive = 64;
            hdr = 16;
            for (var wi = 0; wi < wrapsInDrive; wi++)
            {
                var wp = ReadUInt32BigEndian(page, hdr);
                hdr += 4;
                var ds = ReadUInt32BigEndian(page, hdr);
                hdr += 4;
                var howRc = ReadUInt32BigEndian(page, hdr);
                hdr += 4;
                var eowRc = ReadUInt32BigEndian(page, hdr);
                hdr += 4;
                var howFm = ReadUInt32BigEndian(page, hdr);
                hdr += 4;
                var eowFm = ReadUInt32BigEndian(page, hdr);
                hdr += 4;
                var crc = ReadUInt32BigEndian(page, hdr);
                hdr += 4;

                var e = TapeDirectoryData.GetWrapEntry(wi, createNew: true)!;
                e.Content = $"{wp,-12}{ds,-12}{howRc,-12}{eowRc,-12}{howFm,-12}{eowFm,-12}{crc,-12}";
                e.RawData = [(int)ds, (int)howRc, (int)eowRc, (int)howFm, (int)eowFm, (int)crc];
                e.RecCount = (int)(howRc + eowRc);
                e.FileMarkCount = (int)(howFm + eowFm);
            }

            return;
        }

        // LTO-3+ (and most later) use: WP, DS, HOW RC, EOW RC, HOW FM, EOW FM, FM MAP, CRC
        wrapsInDrive = _nWraps;
        hdr = _hdrLen;

        for (var wi = 0; wi < wrapsInDrive; wi++)
        {
            var wp = ReadUInt32BigEndian(page, hdr);
            hdr += 4;
            var ds = ReadUInt32BigEndian(page, hdr);
            hdr += 4;
            var howRc = ReadUInt32BigEndian(page, hdr);
            hdr += 4;
            var eowRc = ReadUInt32BigEndian(page, hdr);
            hdr += 4;
            var howFm = ReadUInt32BigEndian(page, hdr);
            hdr += 4;
            var eowFm = ReadUInt32BigEndian(page, hdr);
            hdr += 4;
            var fmMap = ReadUInt32BigEndian(page, hdr);
            hdr += 4;
            var crc = ReadUInt32BigEndian(page, hdr);
            hdr += 4;

            var e = TapeDirectoryData.GetWrapEntry(wi, createNew: true)!;
            e.Content = $"{wp,-12}{ds,-12}{howRc,-12}{eowRc,-12}{howFm,-12}{eowFm,-12}{fmMap,-12}{crc,-12}";
            e.RawData = [(int)ds, (int)howRc, (int)eowRc, (int)howFm, (int)eowFm, (int)fmMap, (int)crc];
            e.RecCount = (int)(howRc + eowRc);
            e.FileMarkCount = (int)(howFm + eowFm);
        }
    }

    private void ParseSuspendedWritesPage(Action<string> warn)
    {
        if (!TryGetPage(0x107, out var p) || !TrySlicePage(p, out var page))
            return;

        int slots;
        if (TapeCartridgeProfile != null && !TapeCartridgeProfile.IsLaterThan(LTODensityCode.L5))
            slots = 14;
        else if (TapeCartridgeProfile is { Id.LtoDensity: not null } &&
                 TapeCartridgeProfile.Id.LtoDensity.Value.Equals(LTODensityCode.L5))
            slots = 22;
        else
            slots = 38; // LTO-6/7/8/9 (VB uses 38)

        var offset = 0;
        for (var i = 0; i < slots; i++)
        {
            // VB:
            // DataSetID(i) = dword(offset+8)
            // WTapePass(i) = dword(offset+12)
            // offset += 8
            var dsid = ReadUInt32BigEndian(page, offset + 8);
            var wtp = ReadUInt32BigEndian(page, offset + 12);
            SuspendWriteData.GetDataSetId(i, createNew: true)!.Value = (int)dsid;
            SuspendWriteData.GetWTapePass(i, createNew: true)!.Value = (int)wtp;
            offset += 8;
            if (offset + 16 > page.Length) break;
        }
    }

    private void ParseApplicationSpecificPage(Action<string> warn)
    {
        if (!TryGetPage(0x200, out var p) || !TrySlicePage(p, out var page))
            return;

        var sig = GetAsciiTrim(page, 4, 6);
        if (!sig.Equals("MAM001", StringComparison.Ordinal) && !sig.Equals("MAM002", StringComparison.Ordinal))
            return;

        // `ApplicationSpecific` is a readonly record struct (immutable).
        // Parse into locals and publish once at the end.
        var barcode = "";
        var appVendor = "";
        var appName = "";
        var appVersion = "";

        var idx = 10;
        while (idx + 4 <= page.Length)
        {
            var attrId = ReadUInt16BigEndian(page, idx);
            var attrLen = ReadUInt16BigEndian(page, idx + 2) & 0x0FFF;

            if (attrId == 0x0FFF || attrLen == 0)
                break;

            var valOff = idx + 4;
            if (valOff + attrLen > page.Length)
                break;

            switch (attrId)
            {
                case 0x0806:
                    {
                        var bc = GetAsciiTrim(page, valOff, attrLen);
                        if (!string.IsNullOrEmpty(bc))
                            barcode = bc;
                        break;
                    }
                case 0x0800:
                    appVendor = GetAsciiTrim(page, valOff, attrLen);
                    break;
                case 0x0801:
                    appName = GetAsciiTrim(page, valOff, attrLen);
                    break;
                case 0x0802:
                    appVersion = GetAsciiTrim(page, valOff, attrLen);
                    break;
            }

            idx += 4 + attrLen;
        }

        ApplicationSpecificData = new ApplicationSpecific(
            Barcode: barcode,
            ApplicationVendor: appVendor,
            ApplicationName: appName,
            ApplicationVersion: appVersion);
    }

    public string GetModernReport()
    {
        const int totalWidth = 80;
        const int innerWidth = totalWidth - 2;
        const int labelWidth = 24;
        const int valueWidth = innerWidth - labelWidth - 5; // " " + label + " │ " + value + " "
        const int leftSegmentWidth = labelWidth + 2;
        const int rightSegmentWidth = valueWidth + 2;

        var output = new StringBuilder();

        // ========== BANNER ==========
        var headerParts = new List<string>();
        var formatText = NormalizeValue(BuildFormatText());
        if (formatText != "—") headerParts.Add(formatText);
        var sn = NormalizeValue(TapeCartridgeProfile?.SN);
        if (sn != "—") headerParts.Add($"SN {sn}");
        var vendor = NormalizeValue(TapeCartridgeProfile?.Vendor);
        if (vendor != "—") headerParts.Add(vendor);
        var mfgDate = NormalizeValue(FormatDateYmd(TapeCartridgeProfile?.ManufacturingDate));
        if (mfgDate != "—") headerParts.Add(mfgDate);

        var header = string.Join(" • ", headerParts);
        if (string.IsNullOrWhiteSpace(header)) header = "CM REPORT";

        output.AppendLine($"┌{new string('─', innerWidth)}┐");
        if (header.Length > innerWidth - 2)
        {
            var lines = SplitToFit(header, innerWidth - 2);
            foreach (var line in lines)
                output.AppendLine($"│ {line.PadRight(innerWidth - 2)} │");
        }
        else
        {
            output.AppendLine($"│ {header.PadRight(innerWidth - 2)} │");
        }
        AppendSectionHeader("APPLICATION");
        var barcode = NormalizeValue(ApplicationSpecificData?.Barcode);
        if (barcode != "—")
            AppendRow("Barcode", barcode, highlight: true);
        var appInfo = NormalizeValue(BuildApplicationInfo());
        if (appInfo != "—")
            AppendRow("Application", appInfo);

        AppendSectionHeader("USAGE");

        if (TapeCartridgeProfile is not null && TapeCartridgeProfile.Id.Family == CartridgeFamily.Cleaning)
        {
            // Cleaning cartridge
            var cleansPerformed = StatusData?.ThreadCount.ToString(CultureInfo.InvariantCulture) ?? "—";
            var cleansRemain = NormalizeValue(ComputeCleansRemaining());
            var usedLength = NormalizeValue(BuildCleaningUsedLength());

            AppendRow("Cleans performed", cleansPerformed);
            AppendRow("Cleans remain", cleansRemain);
            AppendRow("Used length", usedLength);
        }
        else
        {
            // Data cartridge
            var loadCount = StatusData?.ThreadCount.ToString(CultureInfo.InvariantCulture) ?? "—";
            var encrypted = FormatBool(StatusData?.EncryptedData);
            AppendRow("Load count", loadCount);
            AppendRow("Encrypted", encrypted);

            // I/O Statistics
            var writeTotal = NormalizeValue(BuildTotalIoText(isWrite: true));
            var readTotal = NormalizeValue(BuildTotalIoText(isWrite: false));
            AppendRow("Total write", writeTotal);
            AppendRow("Total read", readTotal);

            // FVE
            var fveText = NormalizeValue(BuildFveText());
            AppendRow("Full volume eq.", fveText, highlight: true);

            // Error metrics
            AppendRow("Write retries", FormatWithStatus(GetUsage()?.LifeWriteRetries));
            AppendRow("Read retries", FormatWithStatus(GetUsage()?.LifeReadRetries));
            AppendRow("Unrecovered writes", FormatWithStatus(GetUsage()?.LifeUnRecoverWrites));
            AppendRow("Unrecovered reads", FormatWithStatus(GetUsage()?.LifeUnRecoverReads));
            AppendRow("Suspended writes", FormatWithStatus(GetUsage()?.LifeSuspendedWrites));
            AppendRow("Suspended append writes", FormatWithStatus(GetUsage()?.LifeSuspendAppendWrites));
            AppendRow("Fatal suspended writes", FormatWithStatus(GetUsage()?.LifeFatalSuspendWrites));
        }
        AppendSectionHeader("MEDIUM IDENTITY");
        AppendRow("Format", formatText);
        AppendRow("Serial number", sn);
        AppendRow("Tape vendor", vendor);
        AppendRow("Tape mfg date", mfgDate);

        var mediaVendor = NormalizeValue(MediaMfgData?.MediaVendor);
        var mediaDate = NormalizeValue(FormatDateYmd(MediaMfgData?.MediaProfileDate));
        AppendRow("Media vendor", mediaVendor);
        AppendRow("Media mfg date", mediaDate);

        var particleType = TapeCartridgeProfile?.ParticleType.ToString();
        if (TapeCartridgeProfile is not null && TapeCartridgeProfile.Id.Family == CartridgeFamily.Cleaning)
            particleType = "Universal Clean Cartridge";
        if (string.IsNullOrWhiteSpace(particleType) || particleType == ParticleType.Unknown.ToString())
            particleType = null;

        var substrateType = TapeCartridgeProfile?.SubstrateType;
        var substrateText = substrateType == null || substrateType == SubstrateType.Unknown
            ? null
            : substrateType.ToString();

        var servoBand = TapeCartridgeProfile?.ServoBandId;
        var servoText = servoBand == null || servoBand == ServoBandId.Unknown
            ? null
            : servoBand.ToString();

        var mediaCodeText = TapeCartridgeProfile == null ? null : $"0x{TapeCartridgeProfile.MediaCode:X4}";

        if (!string.IsNullOrEmpty(particleType))
            AppendRow("Particle type", particleType);
        if (!string.IsNullOrEmpty(substrateText))
            AppendRow("Substrate", substrateText);
        if (!string.IsNullOrEmpty(servoText))
            AppendRow("Servo band", servoText);
        if (!string.IsNullOrEmpty(mediaCodeText))
            AppendRow("Media code", mediaCodeText);
        var hasDriveInfo = CartridgeContentData != null || !string.IsNullOrWhiteSpace(GetUsage()?.DriveSN);
        if (hasDriveInfo)
        {
            AppendSectionHeader("DRIVE");
            var driveSN = NormalizeValue(GetUsage()?.DriveSN);
            var driveID = NormalizeValue(CartridgeContentData?.DriveId);
            var driveFW = NormalizeValue(CartridgeContentData?.DriveFirmwareId);
            var contentCode = FormatHex(CartridgeContentData?.CartridgeContentCode, 4);
            var partitioned = FormatBool(CartridgeContentData?.PartitionedCartridge);
            var typeM = FormatBool(CartridgeContentData?.TypeMCartridge);

            if (driveSN != "—")
                AppendRow("Drive SN", driveSN);
            if (driveID != "—")
                AppendRow("Drive ID", driveID);
            if (driveFW != "—")
                AppendRow("Drive firmware", driveFW);
            if (contentCode != null)
                AppendRow("Content code", contentCode);
            if (partitioned != "—")
                AppendRow("Partitioned", partitioned);
            if (typeM != "—")
                AppendRow("Type M", typeM);
        }

        AppendSectionHeader("DATA ON TAPE");

        if (TapeCartridgeProfile is null || TapeCartridgeProfile.Id.Family == CartridgeFamily.Cleaning)
        {
            AppendRow("Data on tape", "Not applicable");
        }
        else
        {
            var kbPerDataset = GetKbPerDataset();
            var wrapsText = _nWraps > 0 ? _nWraps.ToString(CultureInfo.InvariantCulture) : "—";
            var setsPerWrapText = _setsPerWrap > 0 ? _setsPerWrap.ToString(CultureInfo.InvariantCulture) : "—";
            var kbText = kbPerDataset.Bytes > 0 ? kbPerDataset.ToString(CultureInfo.InvariantCulture) : "—";
            var tapeDirVer = TapeDirectoryData.Version > 0
                ? TapeDirectoryData.Version.ToString(CultureInfo.InvariantCulture)
                : "—";

            AppendRow("Wraps", wrapsText);
            AppendRow("Sets/wrap", setsPerWrapText);
            AppendRow("KB/dataset", kbText);
            AppendRow("Tape dir ver", tapeDirVer);

            if (TapeDirectoryData.CapacityLoss.Count == 0)
            {
                AppendRow("Partitions", "Not available");
            }
            else
            {
                try
                {
                    var dataWrapList = new List<int>();
                    var dataWrapNum = 0;
                    foreach (var loss in TapeDirectoryData.CapacityLoss)
                    {
                        if (loss is -3)
                        {
                            if (dataWrapNum <= 0) continue;
                            dataWrapList.Add(dataWrapNum);
                            dataWrapNum = 0;
                        }
                        else
                        {
                            dataWrapNum += 1;
                        }
                    }

                    if (dataWrapNum > 0)
                        dataWrapList.Add(dataWrapNum);

                    long nLossDatasets = 0;
                    var dataSizes = new List<long>();
                    long currSize = 0;
                    var guardWrap = false;

                    var wrapsToRead = _nWraps;
                    if (wrapsToRead > TapeDirectoryData.CapacityLoss.Count)
                        wrapsToRead = TapeDirectoryData.CapacityLoss.Count;

                    for (var wi = 0; wi < wrapsToRead; wi++)
                    {
                        var loss = GetCapacityLoss(wi);
                        var datasetEntry = TapeDirectoryData.GetDatasetsOnWrap(wi, createNew: false);
                        if (datasetEntry == null)
                            break;

                        if (loss >= 0 && _setsPerWrap > 0)
                            nLossDatasets += Math.Max(0, _setsPerWrap - datasetEntry.Data);

                        switch (loss)
                        {
                            case >= 0:
                                currSize += datasetEntry.Data;
                                break;
                            case -2:
                                currSize += datasetEntry.Data;
                                break;
                            case -3:
                                if (guardWrap)
                                {
                                    dataSizes.Add(currSize);
                                    currSize = 0;
                                    guardWrap = false;
                                }
                                else
                                {
                                    guardWrap = true;
                                }
                                break;
                        }
                    }

                    dataSizes.Add(currSize);

                    var estLoss = FormatSizeBytes((nLossDatasets * GetKbPerDataset().Kilobytes * 1000L).Bytes());
                    AppendRow("Total partitions", dataWrapList.Count.ToString(CultureInfo.InvariantCulture));

                    var sizePerWrap = GetMbPerWrap();
                    for (var i = 0; i < dataWrapList.Count; i++)
                    {
                        var wraps = dataWrapList[i];
                        var totalSize = (sizePerWrap.Megabytes * wraps).Megabytes();
                        var writtenSize = "";
                        if (dataSizes.Count == dataWrapList.Count && sizePerWrap.Bytes > 0)
                        {
                            var bytes = (dataSizes[i] * GetKbPerDataset().Kilobytes).Kilobytes();
                            writtenSize = $"{FormatSizeBytes(bytes)} / ";
                        }

                        var sizeText = $"{writtenSize}{FormatSizeBytes(totalSize)} · {wraps} wraps";
                        AppendRow($"Partition {i} size", sizeText);
                    }

                    AppendRow("Est. capacity loss", estLoss, highlight: true);
                }
                catch
                {
                    AppendRow("Partitions", "Not available");
                }
            }
        }
        AppendSectionHeader("CM DATA");
        AppendRow("Length", _cm.Length.ToString(CultureInfo.InvariantCulture));
        AppendRow("Pages", PageData.Count.ToString(CultureInfo.InvariantCulture));

        AppendBottomBorder();
        return output.ToString();

        void AppendSectionHeader(string title)
        {
            var left = BuildSectionSegment(title, leftSegmentWidth);
            var right = new string('─', rightSegmentWidth);
            output.AppendLine($"├{left}┼{right}┤");
        }

        void AppendRow(string label, string? value, bool highlight = false)
        {
            var labelText = NormalizeLabel(label);
            var valueText = NormalizeValue(value);
            var valueCell = highlight
                ? BuildHighlightedValue(valueText, valueWidth)
                : FitRight(valueText, valueWidth);
            output.AppendLine($"│ {FitLeft(labelText, labelWidth)} │ {valueCell} │");
        }

        void AppendBottomBorder()
            => output.AppendLine($"└{new string('─', leftSegmentWidth)}┴{new string('─', rightSegmentWidth)}┘");

        static string NormalizeValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "—";
            if (string.Equals(value, "Not available", StringComparison.OrdinalIgnoreCase)) return "—";
            return value.Trim();
        }

        static string? FormatDateYmd(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var trimmed = value.Trim();
            if (trimmed.Length != 8) return trimmed;
            for (var i = 0; i < trimmed.Length; i++)
            {
                if (!char.IsDigit(trimmed[i]))
                    return trimmed;
            }

            return $"{trimmed[..4]}-{trimmed[4..6]}-{trimmed[6..8]}";
        }

        static string NormalizeLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return string.Empty;
            var text = label.Trim();
            if (text.EndsWith(":", StringComparison.Ordinal))
                text = text[..^1].TrimEnd();
            return text;
        }

        static string BuildSectionSegment(string title, int width)
        {
            if (width <= 0) return string.Empty;
            var label = string.IsNullOrWhiteSpace(title) ? string.Empty : title.Trim();
            if (string.IsNullOrEmpty(label)) return new string('─', width);
            var prefix = $"─ {label} ";
            if (prefix.Length >= width) return prefix[..width];
            return prefix + new string('─', width - prefix.Length);
        }

        static string FitLeft(string text, int width)
        {
            if (width <= 0) return string.Empty;
            if (text.Length <= width) return text.PadRight(width);
            if (width == 1) return "…";
            return text[..(width - 1)] + "…";
        }

        static string FitRight(string text, int width)
        {
            if (width <= 0) return string.Empty;
            if (text.Length <= width) return text.PadLeft(width);
            if (width == 1) return "…";
            var trimmed = text[..(width - 1)] + "…";
            return trimmed.PadLeft(width);
        }

        static string BuildHighlightedValue(string value, int width)
        {
            const string marker = "★ ";
            if (width <= 0) return string.Empty;
            if (width <= marker.Length)
                return FitLeft(marker.TrimEnd(), width);

            var available = width - marker.Length;
            return marker + FitRight(value, available);
        }

        static string FormatBool(bool? value)
            => value.HasValue ? (value.Value ? "Yes" : "No") : "—";

        static string? FormatHex(int? value, int digits)
            => value.HasValue ? $"0x{value.Value.ToString($"X{digits}", CultureInfo.InvariantCulture)}" : null;

        static string FormatWithStatus(long? value)
        {
            if (!value.HasValue) return "—";
            var numStr = value.Value.ToString(CultureInfo.InvariantCulture);
            return value.Value == 0 ? $"{numStr} ✓" : numStr;
        }

        static List<string> SplitToFit(string text, int maxWidth)
        {
            var result = new List<string>();
            var words = text.Split(' ');
            var currentLine = "";

            foreach (var word in words)
            {
                var testLine = string.IsNullOrEmpty(currentLine) ? word : $"{currentLine} {word}";
                if (testLine.Length <= maxWidth)
                {
                    currentLine = testLine;
                }
                else
                {
                    if (!string.IsNullOrEmpty(currentLine))
                        result.Add(currentLine);
                    currentLine = word;
                }
            }

            if (!string.IsNullOrEmpty(currentLine))
                result.Add(currentLine);

            return result;
        }
    }

    public string GetLegacyReport()
    {
        var output = new StringBuilder();

        AppendHeader(output, "APPLICATION INFO");
        AppendRowSafe(output, "Barcode:", () => ApplicationSpecificData?.Barcode);
        AppendRowSafe(output, "Application:", BuildApplicationInfo);

        AppendHeader(output, "MEDIUM USAGE");

        if (TapeCartridgeProfile is not null && TapeCartridgeProfile.Id.Family == CartridgeFamily.Cleaning)
        {
            AppendRowSafe(output, "Cleans performed:",
                () => StatusData?.ThreadCount.ToString(CultureInfo.InvariantCulture));
            AppendRowSafe(output, "Cleans remain:", ComputeCleansRemaining);
            AppendRowSafe(output, "Used length:", BuildCleaningUsedLength);
        }
        else
        {
            AppendRowSafe(output, "Load count:", () => StatusData?.ThreadCount.ToString(CultureInfo.InvariantCulture));
            AppendRowSafe(output, "Total write:", () => BuildTotalIoText(isWrite: true));
            AppendRowSafe(output, "Total read:", () => BuildTotalIoText(isWrite: false));
            AppendRowSafe(output, "Full volume equivalents:", BuildFveText);
            AppendRowSafe(output, "Write retries:",
                () => GetUsage()?.LifeWriteRetries.ToString(CultureInfo.InvariantCulture));
            AppendRowSafe(output, "Read retries:",
                () => GetUsage()?.LifeReadRetries.ToString(CultureInfo.InvariantCulture));
            AppendRowSafe(output, "Unrecovered writes:",
                () => GetUsage()?.LifeUnRecoverWrites.ToString(CultureInfo.InvariantCulture));
            AppendRowSafe(output, "Unrecovered reads:",
                () => GetUsage()?.LifeUnRecoverReads.ToString(CultureInfo.InvariantCulture));
            AppendRowSafe(output, "Suspended writes:",
                () => GetUsage()?.LifeSuspendedWrites.ToString(CultureInfo.InvariantCulture));
            AppendRowSafe(output, "Suspended append writes:",
                () => GetUsage()?.LifeSuspendAppendWrites.ToString(CultureInfo.InvariantCulture));
            AppendRowSafe(output, "Fatal suspended writes:",
                () => GetUsage()?.LifeFatalSuspendWrites.ToString(CultureInfo.InvariantCulture));
        }

        AppendHeader(output, "MEDIUM IDENTITY");
        AppendRowSafe(output, "Format:", BuildFormatText);
        AppendRowSafe(output, "Serial number:", () => TapeCartridgeProfile?.SN);
        AppendRowSafe(output, "Tape Vendor:", () => TapeCartridgeProfile?.Vendor);
        AppendRowSafe(output, "Tape mfg date:", () => TapeCartridgeProfile?.ManufacturingDate);
        AppendRowSafe(output, "Media Vendor:", () => MediaMfgData?.MediaVendor);
        AppendRowSafe(output, "Media mfg date:", () => MediaMfgData?.MediaProfileDate);

        try
        {
            var cmData = _cm.Span;
            var particleType = TapeCartridgeProfile?.ParticleType.ToString() ?? "";
            if (TapeCartridgeProfile is not null && TapeCartridgeProfile.Id.Family == CartridgeFamily.Cleaning)
                particleType = "Universal Clean Cartridge";
            if (string.IsNullOrWhiteSpace(particleType))
                particleType = "Not available";

            output.AppendLine(FormatRow("Particle type:", particleType));

            AppendHeader(output, "DATA ON TAPE");

            var wares = new StringBuilder();
            long nLossDatasets = 0;
            var dataSize = new List<long>();

            try
            {
                var skipWrapAnalysis = TapeCartridgeProfile is not null
                                       && TapeCartridgeProfile.Id.Family == CartridgeFamily.Cleaning;
                if (!skipWrapAnalysis)
                {
                    wares.AppendLine(BuildHeader("WRAP ANALYSIS"));
                    wares.AppendLine("| Wrap | Start Block |  End Block  | Filemark |      Set      | Capacity  |");
                    wares.AppendLine("|------+-------------+-------------+----------+---------------+-----------|");

                    var startBlock = 0;
                    long currSize = 0;
                    var guardWrap = false;

                    var wrapsToRead = _nWraps;
                    for (var wn = 0; wn < wrapsToRead; wn++)
                    {
                        var capacityLoss = GetCapacityLoss(wn);
                        var wrapEntry = TapeDirectoryData.GetWrapEntry(wn, createNew: false);
                        var datasetEntry = TapeDirectoryData.GetDatasetsOnWrap(wn, createNew: false);

                        if (wrapEntry is null || datasetEntry is null)
                            throw new InvalidOperationException("Wrap data missing");

                        var startBlockStr = startBlock.ToString(CultureInfo.InvariantCulture);
                        if (capacityLoss is -1 or -3)
                            startBlockStr = "";

                        var endBlock = startBlock + wrapEntry.RecCount + wrapEntry.FileMarkCount - 1;
                        if (capacityLoss is -2)
                            endBlock += 1;

                        wares.Append($"| {wn.ToString(CultureInfo.InvariantCulture).PadLeft(3)}  |");
                        wares.Append($" {startBlockStr.PadLeft(10)}  |");
                        if (startBlockStr.Length > 0)
                            wares.Append($"  {endBlock.ToString(CultureInfo.InvariantCulture).PadLeft(10)} |");
                        else
                            wares.Append($"  {"".PadLeft(10)} |");
                        wares.Append(
                            $"  {wrapEntry.FileMarkCount.ToString(CultureInfo.InvariantCulture).PadLeft(5)}   |");
                        wares.Append(
                            $" {datasetEntry.Data.ToString(CultureInfo.InvariantCulture).PadLeft(5)} / {_setsPerWrap.ToString(CultureInfo.InvariantCulture).PadRight(5)} |");

                        startBlock += wrapEntry.RecCount + wrapEntry.FileMarkCount;

                        switch (capacityLoss)
                        {
                            case >= 0 when _setsPerWrap > 0:
                                {
                                    nLossDatasets += Math.Max(0, _setsPerWrap - datasetEntry.Data);
                                    currSize += datasetEntry.Data;
                                    var pct = datasetEntry.Data / (double)_setsPerWrap * 100.0;
                                    wares.Append($" {pct.ToString("f2", CultureInfo.InvariantCulture).PadLeft(7)}%  |");
                                    break;
                                }
                            case >= 0:
                                wares.Append("       0%  |");
                                break;
                            case -1:
                                startBlock = 0;
                                wares.Append("           |");
                                break;
                            case -2:
                                currSize += datasetEntry.Data;
                                wares.Append("  >>EOD<<  |");
                                break;
                            case -3:
                                {
                                    startBlock = 0;
                                    if (guardWrap)
                                    {
                                        dataSize.Add(currSize);
                                        currSize = 0;
                                        guardWrap = false;
                                    }
                                    else
                                    {
                                        guardWrap = true;
                                    }

                                    wares.Append("  *GUARD*  |");
                                    break;
                                }
                        }

                        wares.AppendLine();
                    }

                    dataSize.Add(currSize);
                }
            }
            catch
            {
                wares.Append("| CM data parsing failed.".PadRight(74)).Append('|').AppendLine();
            }

            try
            {
                var dataWrapList = new List<int>();
                var dataWrapNum = 0;
                foreach (var loss in TapeDirectoryData.CapacityLoss)
                {
                    if (loss is -3)
                    {
                        if (dataWrapNum <= 0) continue;
                        dataWrapList.Add(dataWrapNum);
                        dataWrapNum = 0;
                    }
                    else
                    {
                        dataWrapNum += 1;
                    }
                }

                if (dataWrapNum > 0)
                    dataWrapList.Add(dataWrapNum);

                output.AppendLine(FormatRow("Total partitions:",
                    dataWrapList.Count.ToString(CultureInfo.InvariantCulture)));

                for (var i = 0; i < dataWrapList.Count; i++)
                {
                    var wraps = dataWrapList[i];
                    var sizePerWrap = GetMbPerWrap();
                    var len = (sizePerWrap.Megabytes * wraps).Megabytes();

                    var writtenSize = "";
                    if (dataSize.Count == dataWrapList.Count && sizePerWrap.Bytes > 0)
                    {
                        var bytes = (dataSize[i] * GetKbPerDataset().Kilobytes).Kilobytes();
                        writtenSize = $"{FormatSizeBytes(bytes)} / ";
                    }

                    var sizeText = (writtenSize + FormatSizeBytes(len)).PadRight(24);
                    var wrapText = $"[{wraps.ToString(CultureInfo.InvariantCulture),3} wraps]";
                    output.AppendLine(FormatRow($"Partition {i} size:", sizeText + wrapText));
                }
            }
            catch
            {
                output.AppendLine("Partition page not available");
            }

            output.AppendLine(FormatRow("Estimated capacity loss:",
                FormatSizeBytes((nLossDatasets * GetKbPerDataset().Kilobytes * 1000L).Bytes())));
            output.Append(wares.ToString());

            AppendHeader(output, "CM RAW DATA");
            output.AppendLine(FormatRow("Length:", cmData.Length.ToString(CultureInfo.InvariantCulture)));
            output.Append(HexDump.Format(cmData));
            output.AppendLine(BuildHeader(string.Empty));
            output.AppendLine();
        }
        catch (Exception ex)
        {
            output.Append("| CM data parsing failed.".PadRight(74)).Append('|').AppendLine();
            output.AppendLine(ex.ToString());
        }

        return output.ToString();
    }

    private static void AppendHeader(StringBuilder sb, string title)
        => sb.AppendLine(BuildHeader(title));

    private static string BuildHeader(string title)
    {
        const int innerWidth = 73;
        var content = string.IsNullOrEmpty(title) ? string.Empty : $" {title} ";
        if (content.Length > innerWidth)
            content = content[..innerWidth];
        var leftPad = (innerWidth - content.Length) / 2;
        var rightPad = innerWidth - content.Length - leftPad;
        return "+" + new string('=', leftPad) + content + new string('=', rightPad) + "+";
    }

    private static string FormatRow(string label, string value)
    {
        var prefix = $"| {label} ".PadRight(28);
        return (prefix + value).PadRight(74) + "|";
    }

    private static void AppendRowSafe(StringBuilder sb, string label, Func<string?> getValue)
    {
        try
        {
            var value = getValue();
            if (string.IsNullOrWhiteSpace(value))
                value = "Not available";
            sb.AppendLine(FormatRow(label, value));
        }
        catch
        {
            sb.AppendLine(FormatRow(label, "Not available"));
        }
    }

    private Usage? GetUsage()
        => UsageData.Count > 0 ? UsageData[0] : null;

    private string? BuildApplicationInfo()
    {
        if (ApplicationSpecificData is null)
            return null;

        var vendor = ApplicationSpecificData.Value.ApplicationVendor;
        var name = ApplicationSpecificData.Value.ApplicationName;
        var version = ApplicationSpecificData.Value.ApplicationVersion;
        var appInfo = $"{vendor} {name} {version}".Trim();
        return string.IsNullOrWhiteSpace(appInfo) ? null : appInfo;
    }

    private string? BuildFormatText()
    {
        if (TapeCartridgeProfile is null)
            return null;

        var format = TapeCartridgeProfile.Format ?? "";
        var mediaCode = TapeCartridgeProfile.MediaCode;
        var densityCode = TapeCartridgeProfile.Id.LtoDensity?.Code ?? 0;
        var suffix = $"(MC 0x{mediaCode:X4} DC 0x{densityCode:X2})";
        var value = string.IsNullOrWhiteSpace(format) ? suffix : $"{format} {suffix}";
        return value.Trim();
    }

    private string BuildTotalIoText(bool isWrite)
    {
        var usage = GetUsage();
        if (usage is null)
            return "Not available";

        var kbPerDataset = GetKbPerDataset();
        var sets = isWrite ? usage.LifeSetsWritten : usage.LifeSetsRead;

        if (kbPerDataset.Bytes > 0)
        {
            var size = (kbPerDataset.Kilobytes * sets).Kilobytes();
            return FormatSizeBytes(size);
        }

        return $"{sets.ToString(CultureInfo.InvariantCulture)} Sets";
    }

    private string BuildFveText()
    {
        var usage = GetUsage();
        if (usage is null)
            return "Not available";

        if (_setsPerWrap <= 0 || _nWraps <= 0)
            return "Unknown";

        var denom = _setsPerWrap * (double)_nWraps;
        var fve = (usage.LifeSetsRead + usage.LifeSetsWritten) / denom;

        if (TapeCartridgeProfile is null || TapeCartridgeProfile.TapeLifeInVols <= 0)
            return $"{fve.ToString("f2", CultureInfo.InvariantCulture)} FVE";

        var pct = fve / TapeCartridgeProfile.TapeLifeInVols * 100.0;
        return
            $"{fve.ToString("f2", CultureInfo.InvariantCulture)} FVE ({pct.ToString("f2", CultureInfo.InvariantCulture)}%)";
    }

    private string? ComputeCleansRemaining()
    {
        if (TapeCartridgeProfile is null || StatusData is null)
            return null;

        if (TapeCartridgeProfile.TapeLengthQuarterMetres <= 0)
            return null;

        var cleanLength = 5.5;
        var tapeLen = TapeCartridgeProfile.TapeLengthQuarterMetres / 4.0;
        var lastLoc = StatusData.Value.LastLocation / 4.0;
        var remaining = (tapeLen - 11 - lastLoc) / cleanLength;
        if (remaining < 0)
            remaining = 0;
        return remaining.ToString("f2", CultureInfo.InvariantCulture);
    }

    private string? BuildCleaningUsedLength()
    {
        if (TapeCartridgeProfile is null || StatusData is null)
            return null;

        var tapeLen = TapeCartridgeProfile.TapeLengthQuarterMetres / 4.0;
        var lastLoc = StatusData.Value.LastLocation / 4.0;
        var usable = tapeLen - 11;
        if (usable < 0)
            usable = 0;
        return
            $"{lastLoc.ToString("f2", CultureInfo.InvariantCulture)} m / {usable.ToString("f2", CultureInfo.InvariantCulture)} m";
    }

    private ByteSize GetKbPerDataset()
    {
        return (TapeCartridgeProfile?.KbPerDataset * 1000L)?.Bytes() ?? 0L.Bytes();
    }

    private ByteSize GetMbPerWrap()
    {
        var kbPerDataset = GetKbPerDataset();
        if (kbPerDataset.Bytes <= 0 || _setsPerWrap <= 0)
            return ByteSize.FromBytes(0);

        var kbPerWrap = kbPerDataset.Kilobytes * _setsPerWrap;
        return ByteSize.FromKilobytes(kbPerWrap);
    }

    private double GetCapacityLoss(int index)
    {
        if (index < 0 || index >= TapeDirectoryData.CapacityLoss.Count)
            return 0;
        return TapeDirectoryData.CapacityLoss[index];
    }

    private static string FormatSizeBytes(ByteSize size)
    {
        return size.ToString("#.##");
    }

    private static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> s, int offset)
        => BinaryPrimitives.ReadUInt16BigEndian(s.Slice(offset, 2));

    /// <summary>
    /// Compatibility usage.
    /// </summary>
    /// <param name="s"></param>
    /// <param name="offset"></param>
    /// <returns></returns>
    private static int ReadI32Be(ReadOnlySpan<byte> s, int offset)
        => unchecked((int)BinaryPrimitives.ReadUInt32BigEndian(s.Slice(offset, 4)));

    private static long ReadUInt64BigEndian(ReadOnlySpan<byte> s, int offset)
        => unchecked((long)BinaryPrimitives.ReadUInt64BigEndian(s.Slice(offset, 8)));

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> s, int offset)
        => BinaryPrimitives.ReadUInt32BigEndian(s.Slice(offset, 4));

    private static string GetAsciiTrim(ReadOnlySpan<byte> s, int offset, int length)
    {
        if (offset < 0 || length <= 0) return "";
        return offset + length > s.Length ? "" : Encoding.ASCII.GetString(s.Slice(offset, length)).TrimEnd('\0').TrimEnd();
    }

    private readonly record struct UsageSnapshot(
        int Index,
        int UsageOffset,
        int UsageLength,
        int MechOffset,
        int ThreadCount);
}
