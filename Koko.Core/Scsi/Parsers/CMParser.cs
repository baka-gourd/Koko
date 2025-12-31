using Koko.Core.Scsi.Codes.Cartridges;

using System.Buffers.Binary;
using System.Text;

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

    private int _nWraps;               // a_NWraps
    private int _setsPerWrap;         // a_SetsPerWrap
    private int _tapeDirEntryLen = 16; // a_TapeDirLength (16/28/32 etc)
    private int _hdrLen;               // a_HdrLength

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
        ParseCartridgeMfgPage(warn);       // page id 1
        ParseMediaMfgPage(warn);           // page id 2
        ParseUsagePagesIfPresent(warn);    // 0x108..0x10B + 0x106
        ParseStatusPage(warn);             // 0x105
        ParseInitialisationPage(warn);     // 0x101
        ParseEodPages(warn);               // 0x104 / 0x10E / 0x10F / 0x110
        ParseCartridgeContentIfPresent(warn); // 0x10D (LTO5+)
        ParseTapeDirectoryPage(warn);      // 0x103
        ParseSuspendedWritesPage(warn);    // 0x107
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
                    warn($"CM Page Header Error: Offset={pageOffset} expected=0x{tableWord0:X4} actual=0x{headerWord0:X4}");
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
            var unRecovWrites = ReadUInt16BigEndian(curUsage, atOffset[5]) - ReadUInt16BigEndian(prevUsage, atOffset[5]);
            var unRecovReads = ReadUInt16BigEndian(curUsage, atOffset[6]) - ReadUInt16BigEndian(prevUsage, atOffset[6]);
            var suspendedWrites = ReadUInt16BigEndian(curUsage, atOffset[7]) - ReadUInt16BigEndian(prevUsage, atOffset[7]);
            var fatalSusWrites = ReadUInt16BigEndian(curUsage, atOffset[8]) - ReadUInt16BigEndian(prevUsage, atOffset[8]);

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
                ccqWriteFails = ccqWriteFailsRaw <= 0 ? 0 : (ccqWriteFailsRaw > int.MaxValue ? int.MaxValue : (int)ccqWriteFailsRaw);

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
        var fwOff = TapeCartridgeProfile.Id.LtoDensity != null && TapeCartridgeProfile.Id.LtoDensity.Value.Equals(LTODensityCode.L5) ? 48 : 52;
        var driveFirmwareId = GetAsciiTrim(page, fwOff, 4);

        CartridgeContentData = new CartridgeContent(
            DriveId: driveId,
            CartridgeContentCode: cartridgeContentCode,
            PartitionedCartridge: partitionedCartridge,
            TypeMCartridge: typeMCartridge,
            DriveFirmwareId: driveFirmwareId);

        // VB: if LTO-7 and TypeM => "LTO-7 Type M" and wraps=168
        // TapeCartridgeProfile is treated as immutable; do NOT mutate Format in-place.
        if (TapeCartridgeProfile.Id.LtoDensity != null && TapeCartridgeProfile.Id.LtoDensity.Value.Equals(LTODensityCode.L7) && typeMCartridge)
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
        var hdr = _hdrLen;

        if (TapeCartridgeProfile is { Id.LtoDensity: not null } && TapeCartridgeProfile.Id.LtoDensity.Value.Equals(LTODensityCode.L1))
        {
            wrapsInDrive = 48;
            hdr = 16;
            for (var wi = 0; wi < wrapsInDrive; wi++)
            {
                var evenDs = ReadUInt32BigEndian(page, hdr); hdr += 4;
                var evenRc = ReadUInt32BigEndian(page, hdr); hdr += 4;
                var evenFm = ReadUInt32BigEndian(page, hdr); hdr += 4;
                var evenCrc = ReadUInt32BigEndian(page, hdr); hdr += 4;

                var oddDs = ReadUInt32BigEndian(page, hdr); hdr += 4;
                var oddRc = ReadUInt32BigEndian(page, hdr); hdr += 4;
                var oddFm = ReadUInt32BigEndian(page, hdr); hdr += 4;
                var oddCrc = ReadUInt32BigEndian(page, hdr); hdr += 4;

                var e = TapeDirectoryData.GetWrapEntry(wi, createNew: true)!;
                e.Content = $"{evenDs,-12}{evenRc,-12}{evenFm,-12}{evenCrc,-12}{oddDs,-12}{oddRc,-12}{oddFm,-12}{oddCrc,-12}";
                e.RawData = [(int)evenDs, (int)evenRc, (int)evenFm, (int)evenCrc, (int)oddDs, (int)oddRc, (int)oddFm, (int)oddCrc
                ];
                e.RecCount = (int)(evenRc + oddRc);
                e.FileMarkCount = (int)(evenFm + oddFm);
            }
            return;
        }

        if (TapeCartridgeProfile is { Id.LtoDensity: not null } && TapeCartridgeProfile.Id.LtoDensity.Value.Equals(LTODensityCode.L2))
        {
            wrapsInDrive = 64;
            hdr = 16;
            for (var wi = 0; wi < wrapsInDrive; wi++)
            {
                var wp = ReadUInt32BigEndian(page, hdr); hdr += 4;
                var ds = ReadUInt32BigEndian(page, hdr); hdr += 4;
                var howRc = ReadUInt32BigEndian(page, hdr); hdr += 4;
                var eowRc = ReadUInt32BigEndian(page, hdr); hdr += 4;
                var howFm = ReadUInt32BigEndian(page, hdr); hdr += 4;
                var eowFm = ReadUInt32BigEndian(page, hdr); hdr += 4;
                var crc = ReadUInt32BigEndian(page, hdr); hdr += 4;

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
            var wp = ReadUInt32BigEndian(page, hdr); hdr += 4;
            var ds = ReadUInt32BigEndian(page, hdr); hdr += 4;
            var howRc = ReadUInt32BigEndian(page, hdr); hdr += 4;
            var eowRc = ReadUInt32BigEndian(page, hdr); hdr += 4;
            var howFm = ReadUInt32BigEndian(page, hdr); hdr += 4;
            var eowFm = ReadUInt32BigEndian(page, hdr); hdr += 4;
            var fmMap = ReadUInt32BigEndian(page, hdr); hdr += 4;
            var crc = ReadUInt32BigEndian(page, hdr); hdr += 4;

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
        else if (TapeCartridgeProfile is { Id.LtoDensity: not null } && TapeCartridgeProfile.Id.LtoDensity.Value.Equals(LTODensityCode.L5))
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
        if (offset + length > s.Length) return "";
        return Encoding.ASCII.GetString(s.Slice(offset, length)).TrimEnd('\0').TrimEnd();
    }

    private readonly record struct UsageSnapshot(
        int Index,
        int UsageOffset,
        int UsageLength,
        int MechOffset,
        int ThreadCount);
}