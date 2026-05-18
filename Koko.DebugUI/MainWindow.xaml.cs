using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

using Koko.Core;
using Koko.Core.Helpers;
using Koko.Core.Ltfs;
using Koko.Core.Scsi;
using Koko.Core.Scsi.Commands;
using Koko.Core.Scsi.Parsers;

using Microsoft.Win32;

namespace Koko.DebugUI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StatusText.Text = "Ready";
    }

    private void LoadSchemaFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load LTFS schema",
            Filter = "LTFS schema (*.schema;*.xml)|*.schema;*.xml|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            SetBusy($"Loading {dialog.FileName}");
            var sw = new Stopwatch();
            sw.Start();
            var index = LtfsSchemaReader.ReadFile(dialog.FileName);
            sw.Stop();
            MessageBox.Show($"parse time:{sw.Elapsed}");
            DisplayIndex(index, dialog.FileName);
            AppendLog($"Loaded schema file: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            ShowError("Failed to load schema file.", ex);
        }
        finally
        {
            SetReady();
        }
    }

    private void RefreshDrives_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy("Refreshing SCSI tape drives");

            var drives = SetupAPI.ListDevices("SCSI").Where(x =>
                x.ClassName is not null && x.ClassName.Equals("TapeDrive", StringComparison.InvariantCultureIgnoreCase));

            var firstDrive = drives.FirstOrDefault();
            if (firstDrive?.PhysicalDeviceObjectName is null)
            {
                Manual.Clear();
                AppendLog("No SCSI tape drive found.");
                return;
            }

            Manual.Text = ToGlobalRootPath(firstDrive.PhysicalDeviceObjectName);
            AppendLog($"Found SCSI tape drive: {Manual.Text}");
        }
        catch (Exception ex)
        {
            ShowError("Failed to refresh SCSI tape drives.", ex);
        }
        finally
        {
            SetReady();
        }
    }

    private async void ReadIndexPartitionSchema_Click(object sender, RoutedEventArgs e)
    {
        var devicePath = Manual.Text?.Trim();
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            MessageBox.Show(this, "Enter a SCSI tape drive path first.", "Koko Debug UI", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(this, devicePath, "Opening SCSI Path", MessageBoxButton.OK, MessageBoxImage.Information);

        try
        {
            SetBusy($"Discovering LTFS schema from {devicePath}");
            CommandDetailsGrid.ItemsSource = null;
            var result = await Task.Run(() => ReadIndexPartitionSchema(devicePath));
            CommandDetailsGrid.ItemsSource = result.CommandTraces;
            AppendCommandDataLog(result.CommandTraces);
            DisplayIndex(result.Index, $"{devicePath} ({result.Source})");
            AppendLog($"Discovered LTFS schema from {devicePath}. Source={result.Source}, append={result.AppendPoint.Partition}{result.AppendPoint.Block}, dirty={result.DirtyAppendDetected}, blocksize={result.Label?.BlockSize ?? 0}.");
            foreach (var warning in result.Warnings)
                AppendLog($"WARN: {warning}");
        }
        catch (Exception ex)
        {
            if (ex is DebugReadException debugReadException)
            {
                CommandDetailsGrid.ItemsSource = debugReadException.CommandTraces;
                AppendCommandDataLog(debugReadException.CommandTraces);
                ShowError("Failed to read index partition schema.", debugReadException.InnerException ?? debugReadException);
            }
            else
            {
                ShowError("Failed to read index partition schema.", ex);
            }
        }
        finally
        {
            SetReady();
        }
    }

    private void ShowTurPacketHexDump_Click(object sender, RoutedEventArgs e)
    {
        ShowTestUnitReadyPacket();
    }

    private async void ExportLtfsTarZst_Click(object sender, RoutedEventArgs e)
    {
        var devicePath = Manual.Text?.Trim();
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            MessageBox.Show(this, "Enter a SCSI tape drive path first.", "Koko Debug UI", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export LTFS tar.zst",
            Filter = "Koko LTFS archive (*.tar.zst)|*.tar.zst|All files (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".tar.zst",
            FileName = $"LTFSIndex_DebugExport_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}Z.tar.zst",
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            SetBusy($"Exporting LTFS tar.zst from {devicePath}");
            CommandDetailsGrid.ItemsSource = null;
            var result = await Task.Run(() => ExportLtfsTarZst(devicePath, dialog.FileName));
            CommandDetailsGrid.ItemsSource = result.CommandTraces;
            AppendCommandDataLog(result.CommandTraces);
            DisplayIndex(result.Index, $"{devicePath} ({result.Source})");
            AppendLog($"Exported {result.ArchivePath}.");
            AppendLog($"Validated archive entries={result.Validation.EntryCount}, schema={result.Validation.SchemaCount}, label={result.Validation.LabelCount}, mam={result.Validation.MamCount}, cm={result.Validation.CmCount}.");
            foreach (var warning in result.Warnings)
                AppendLog($"WARN: {warning}");
        }
        catch (Exception ex)
        {
            if (ex is DebugReadException debugReadException)
            {
                CommandDetailsGrid.ItemsSource = debugReadException.CommandTraces;
                AppendCommandDataLog(debugReadException.CommandTraces);
                ShowError("Failed to export LTFS tar.zst.", debugReadException.InnerException ?? debugReadException);
            }
            else
            {
                ShowError("Failed to export LTFS tar.zst.", ex);
            }
        }
        finally
        {
            SetReady();
        }
    }

    private static async Task<DebugReadResult> ReadIndexPartitionSchema(string devicePath)
    {
        var manager = DriveSessionManager.Instance.Value;
        using var lease = manager.Lease(devicePath, LtoTapeDrive.OpenDriveByPath);
        if (lease.Drive is not LtoTapeDrive dev)
            throw new InvalidOperationException("Device is not an LTO tape drive.");

        var traceDrive = new TraceScsiDrive(dev);
        var device = new ScsiLtfsWriterDevice(traceDrive);

        try
        {
            await device.TestUnitReadyAsync();
            await device.ReserveAsync();
            var removalPrevented = false;
            try
            {
                await device.PreventRemovalAsync(true);
                removalPrevented = true;
                var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
                    new LtfsVolumeDiscoveryOptions(
                        IndexPreference: LtfsDiscoveryIndexPreference.IndexPartition,
                        IndexPartitionOnly: true));
                return new DebugReadResult(result, traceDrive.Traces);
            }
            finally
            {
                if (removalPrevented)
                    await device.PreventRemovalAsync(false);
                await device.ReleaseAsync();
            }
        }
        catch (Exception ex)
        {
            throw new DebugReadException(ex, traceDrive.Traces);
        }
    }

    private static async Task<DebugExportResult> ExportLtfsTarZst(string devicePath, string archivePath)
    {
        var manager = DriveSessionManager.Instance.Value;
        using var lease = manager.Lease(devicePath, LtoTapeDrive.OpenDriveByPath);
        if (lease.Drive is not LtoTapeDrive dev)
            throw new InvalidOperationException("Device is not an LTO tape drive.");

        var traceDrive = new TraceScsiDrive(dev);
        var device = new ScsiLtfsWriterDevice(traceDrive);

        try
        {
            await device.TestUnitReadyAsync();
            await device.ReserveAsync();
            var removalPrevented = false;
            try
            {
                await device.PreventRemovalAsync(true);
                removalPrevented = true;
                var discovery = await new LtfsVolumeDiscoveryService(device).DiscoverAsync(
                    new LtfsVolumeDiscoveryOptions(
                        IndexPreference: LtfsDiscoveryIndexPreference.IndexPartition,
                        IndexPartitionOnly: true));

                var artifacts = await new LtfsAutosaveExporter().ExportAsync(
                    new LtfsAutosaveRequest(
                        OperationId: "debug-ui-export",
                        Reason: "manual-debug-export",
                        Index: discovery.Index,
                        Label: discovery.Label,
                        Options: new LtfsAutosaveOptions(
                            Enabled: true,
                            OutputArchivePath: archivePath,
                            RetainLastPerVolume: 0),
                        MetadataDevice: device),
                    CancellationToken.None);

                var exportedPath = artifacts.Single();
                var validation = ValidateArchive(exportedPath);
                return new DebugExportResult(discovery, traceDrive.Traces, exportedPath, validation);
            }
            finally
            {
                if (removalPrevented)
                    await device.PreventRemovalAsync(false);
                await device.ReleaseAsync();
            }
        }
        catch (Exception ex)
        {
            throw new DebugReadException(ex, traceDrive.Traces);
        }
    }

    private static ArchiveValidationResult ValidateArchive(string archivePath)
    {
        var entryCount = 0;
        var schemaCount = 0;
        var labelCount = 0;
        var mamCount = 0;
        var cmCount = 0;

        using var archiveStream = File.OpenRead(archivePath);
        using var zstandardStream = new ZstandardStream(archiveStream, CompressionMode.Decompress, leaveOpen: false);
        using var tarReader = new TarReader(zstandardStream, leaveOpen: false);
        while (tarReader.GetNextEntry() is { } entry)
        {
            if (entry.DataStream is null)
                continue;

            entryCount++;
            using var data = new MemoryStream();
            entry.DataStream.CopyTo(data);
            data.Position = 0;

            if (entry.Name.EndsWith(".schema", StringComparison.OrdinalIgnoreCase))
            {
                LtfsSchemaReader.Read(data);
                schemaCount++;
            }
            else if (entry.Name.EndsWith(".label", StringComparison.OrdinalIgnoreCase))
            {
                LtfsLabelReader.Read(data);
                labelCount++;
            }
            else if (entry.Name.EndsWith(".mam.json", StringComparison.OrdinalIgnoreCase))
            {
                using var _ = JsonDocument.Parse(data);
                mamCount++;
            }
            else if (entry.Name.EndsWith(".cm.bin", StringComparison.OrdinalIgnoreCase))
            {
                CMParser.CreateFromSpan(data.ToArray());
                cmCount++;
            }
        }

        if (entryCount == 0)
            throw new InvalidDataException("The exported archive does not contain any readable tar entries.");
        if (schemaCount == 0)
            throw new InvalidDataException("The exported archive does not contain an LTFS schema entry.");

        return new ArchiveValidationResult(entryCount, schemaCount, labelCount, mamCount, cmCount);
    }

    private void ShowTestUnitReadyPacket()
    {
        var packet = IOControl.CreateNoDataPacketBytesForDebug(
            [0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            DataDirection.In,
            timeoutSeconds: 10);

        MessageBox.Show(
            this,
            $"TEST UNIT READY packet before DeviceIoControl:{Environment.NewLine}{Environment.NewLine}{HexDump.Format(packet)}",
            "TUR Packet HexDump",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void DisplayIndex(LtfsIndex index, string source)
    {
        SchemaTreeView.Items.Clear();
        SchemaTreeView.Items.Add(CreateIndexNode(index, source));
        IndexSummaryText.Text = $"Generation {index.GenerationNumber}, UUID {index.VolumeUuid}, location {index.Location.Partition}{index.Location.StartBlock}, previous {index.PreviousGenerationLocation.Partition}{index.PreviousGenerationLocation.StartBlock}";
    }

    private static TreeViewItem CreateIndexNode(LtfsIndex index, string source)
    {
        var root = Node($"LTFS Index - {Path.GetFileName(source)}");
        root.Items.Add(Node($"Version: {index.Version}"));
        root.Items.Add(Node($"Creator: {index.Creator}"));
        root.Items.Add(Node($"Volume UUID: {index.VolumeUuid}"));
        root.Items.Add(Node($"Generation: {index.GenerationNumber}"));
        root.Items.Add(Node($"Update Time: {index.UpdateTime}"));
        root.Items.Add(Node($"Location: {index.Location.Partition}{index.Location.StartBlock}"));
        root.Items.Add(Node($"Previous: {index.PreviousGenerationLocation.Partition}{index.PreviousGenerationLocation.StartBlock}"));
        root.Items.Add(Node($"Highest File UID: {index.HighestFileUid}"));

        var files = Node($"Root Files ({index.RootFiles.Count})");
        foreach (var file in index.RootFiles)
            files.Items.Add(CreateFileNode(file));
        root.Items.Add(files);

        var directories = Node($"Root Directories ({index.RootDirectories.Count})");
        foreach (var directory in index.RootDirectories)
            directories.Items.Add(CreateDirectoryNode(directory));
        root.Items.Add(directories);

        root.IsExpanded = true;
        directories.IsExpanded = true;
        return root;
    }

    private static TreeViewItem CreateDirectoryNode(LtfsDirectory directory)
    {
        var node = Node($"Directory: {directory.Name} (uid {directory.FileUid})");
        node.Items.Add(Node($"Readonly: {directory.ReadOnly}"));
        node.Items.Add(Node($"Created: {directory.CreationTime}"));
        node.Items.Add(Node($"Modified: {directory.ModifyTime}"));

        var files = Node($"Files ({directory.Files.Count})");
        foreach (var file in directory.Files)
            files.Items.Add(CreateFileNode(file));
        node.Items.Add(files);

        var directories = Node($"Directories ({directory.Directories.Count})");
        foreach (var child in directory.Directories)
            directories.Items.Add(CreateDirectoryNode(child));
        node.Items.Add(directories);

        return node;
    }

    private static TreeViewItem CreateFileNode(LtfsFile file)
    {
        var node = Node($"File: {file.Name} ({file.Length} bytes, uid {file.FileUid})");
        node.Items.Add(Node($"Readonly: {file.ReadOnly}"));
        node.Items.Add(Node($"Open For Write: {file.OpenForWrite}"));
        node.Items.Add(Node($"Created: {file.CreationTime}"));
        node.Items.Add(Node($"Modified: {file.ModifyTime}"));

        var extents = Node($"Extents ({file.Extents.Count})");
        foreach (var extent in file.Extents)
            extents.Items.Add(Node($"{extent.Partition}{extent.StartBlock} +{extent.ByteOffset}, file {extent.FileOffset}, {extent.ByteCount} bytes"));
        node.Items.Add(extents);

        var xattrs = Node($"Extended Attributes ({file.ExtendedAttributes.Count})");
        foreach (var xattr in file.ExtendedAttributes)
            xattrs.Items.Add(Node($"{xattr.Key}: {xattr.Value}"));
        node.Items.Add(xattrs);

        return node;
    }

    private static TreeViewItem Node(string text) => new() { Header = text };

    private void SetBusy(string text)
    {
        StatusText.Text = text;
        IsEnabled = false;
    }

    private void SetReady()
    {
        IsEnabled = true;
        StatusText.Text = "Ready";
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }

    private void AppendCommandDataLog(IReadOnlyList<ScsiCommandTraceRow> commandTraces)
    {
        foreach (var row in commandTraces.Where(x => !string.IsNullOrWhiteSpace(x.DataPreview)))
            AppendLog($"{row.Time} {row.Command} {row.Direction} Len={row.DataLength} Status={row.ScsiStatus} Bytes={row.BytesReturned} {row.DataPreview}");
    }

    private void ShowError(string message, Exception exception)
    {
        AppendLog($"ERROR: {message} {exception}");
        MessageBox.Show(this, $"{message}{Environment.NewLine}{exception.Message}", "Koko Debug UI", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string ToGlobalRootPath(string physicalDeviceObjectName)
    {
        if (physicalDeviceObjectName.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
            return physicalDeviceObjectName;

        if (physicalDeviceObjectName.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
            return $@"\\.\globalroot{physicalDeviceObjectName}";

        return physicalDeviceObjectName;
    }

    private sealed record DebugReadResult(
        LtfsVolumeDiscoveryResult IndexResult,
        IReadOnlyList<ScsiCommandTraceRow> CommandTraces)
    {
        public LtfsIndex Index => IndexResult.Index;
        public LtfsIndexDiscoverySource Source => IndexResult.Source;
        public LtfsTapePosition AppendPoint => IndexResult.AppendPoint;
        public bool DirtyAppendDetected => IndexResult.DirtyAppendDetected;
        public LtfsLabel? Label => IndexResult.Label;
        public IReadOnlyList<string> Warnings => IndexResult.Warnings;
    }

    private sealed record DebugExportResult(
        LtfsVolumeDiscoveryResult IndexResult,
        IReadOnlyList<ScsiCommandTraceRow> CommandTraces,
        string ArchivePath,
        ArchiveValidationResult Validation)
    {
        public LtfsIndex Index => IndexResult.Index;
        public LtfsIndexDiscoverySource Source => IndexResult.Source;
        public IReadOnlyList<string> Warnings => IndexResult.Warnings;
    }

    private sealed record ArchiveValidationResult(
        int EntryCount,
        int SchemaCount,
        int LabelCount,
        int MamCount,
        int CmCount);

    private sealed class DebugReadException(Exception innerException, IReadOnlyList<ScsiCommandTraceRow> commandTraces)
        : Exception(innerException.Message, innerException)
    {
        public IReadOnlyList<ScsiCommandTraceRow> CommandTraces { get; } = commandTraces;
    }

    private sealed record ScsiCommandTraceRow(
        string Time,
        string Command,
        string Cdb,
        string Direction,
        int DataLength,
        bool Success,
        string ScsiStatus,
        uint BytesReturned,
        string TransportError,
        string Sense,
        string DataPreview);

    private sealed class TraceScsiDrive(IScsiDrive inner) : IScsiDrive
    {
        private readonly List<ScsiCommandTraceRow> traces = [];
        private readonly object gate = new();

        public IReadOnlyList<ScsiCommandTraceRow> Traces
        {
            get
            {
                lock (gate)
                    return traces.ToArray();
            }
        }

        public int BlockSizeLimit
        {
            get => inner.BlockSizeLimit;
            set => inner.BlockSizeLimit = value;
        }

        public ScsiTransportError? LastTransportError => inner.LastTransportError;

        public bool ScsiRead(
            ReadOnlySpan<byte> commandBlock,
            Span<byte> returnBuffer,
            uint timeoutSeconds,
            out byte scsiStatus,
            out uint bytesReturned,
            Span<byte> senseBuffer)
        {
            var cdb = commandBlock.ToArray();
            var ok = inner.ScsiRead(commandBlock, returnBuffer, timeoutSeconds, out scsiStatus, out bytesReturned, senseBuffer);
            AddTrace(cdb, DataDirection.In, returnBuffer.Length, ok, scsiStatus, bytesReturned, senseBuffer, inner.LastTransportError, returnBuffer);
            return ok;
        }

        public bool ScsiWrite(
            ReadOnlySpan<byte> commandBlock,
            Span<byte> dataBuffer,
            uint timeoutSeconds,
            out byte scsiStatus,
            out uint bytesReturned,
            Span<byte> senseBuffer)
        {
            var cdb = commandBlock.ToArray();
            var ok = inner.ScsiWrite(commandBlock, dataBuffer, timeoutSeconds, out scsiStatus, out bytesReturned, senseBuffer);
            AddTrace(cdb, DataDirection.Out, dataBuffer.Length, ok, scsiStatus, bytesReturned, senseBuffer, inner.LastTransportError, dataBuffer);
            return ok;
        }

        public bool ScsiCommand(
            ReadOnlySpan<byte> commandBlock,
            DataDirection dataDirection,
            uint timeout,
            out byte scsiStatus,
            out uint bytesReturned,
            Span<byte> senseBuffer)
        {
            var cdb = commandBlock.ToArray();
            var ok = inner.ScsiCommand(commandBlock, dataDirection, timeout, out scsiStatus, out bytesReturned, senseBuffer);
            AddTrace(cdb, dataDirection, 0, ok, scsiStatus, bytesReturned, senseBuffer, inner.LastTransportError, []);
            return ok;
        }

        private void AddTrace(
            byte[] cdb,
            DataDirection direction,
            int dataLength,
            bool success,
            byte scsiStatus,
            uint bytesReturned,
            ReadOnlySpan<byte> senseBuffer,
            ScsiTransportError? transportError,
            ReadOnlySpan<byte> dataBuffer)
        {
            var row = new ScsiCommandTraceRow(
                DateTime.Now.ToString("HH:mm:ss.fff"),
                CommandName(cdb),
                FormatBytes(cdb),
                direction.ToString(),
                dataLength,
                success,
                $"0x{scsiStatus:X2}",
                bytesReturned,
                transportError is null ? "" : $"{transportError.ErrorCode}: {transportError.Message}",
                FormatSense(senseBuffer),
                FormatDataPreview(dataBuffer, bytesReturned, senseBuffer));

            lock (gate)
                traces.Add(row);
        }

        private static string CommandName(byte[] cdb)
        {
            if (cdb.Length == 0)
                return "Unknown";

            return cdb[0] switch
            {
                0x00 => "TEST UNIT READY",
                0x01 => "REWIND",
                0x03 => "REQUEST SENSE",
                0x04 => "FORMAT MEDIUM",
                0x05 => "READ BLOCK LIMITS",
                0x08 => "READ 6",
                0x0A => "WRITE 6",
                0x10 => "WRITE FILEMARKS",
                0x11 => "SPACE 6",
                0x12 => "INQUIRY",
                0x13 => "VERIFY",
                0x15 => "MODE SELECT 6",
                0x16 => "RESERVE UNIT 6",
                0x17 => "RELEASE UNIT 6",
                0x1A => "MODE SENSE 6",
                0x1B => "LOAD/UNLOAD",
                0x1E => "PREVENT/ALLOW MEDIUM REMOVAL",
                0x2B => "LOCATE 10",
                0x34 => "READ POSITION",
                0x4D => "LOG SENSE",
                0x55 => "MODE SELECT 10",
                0x56 => "RESERVE UNIT 10",
                0x57 => "RELEASE UNIT 10",
                0x5A => "MODE SENSE 10",
                0x8C => "READ ATTRIBUTE",
                0x8D => "WRITE ATTRIBUTE",
                0x91 => "SPACE 16",
                0x92 => "LOCATE 16",
                0xA3 => "MAINTENANCE IN",
                0xA4 => "MAINTENANCE OUT",
                0xA2 => "SECURITY PROTOCOL IN",
                0xB5 => "SECURITY PROTOCOL OUT",
                _ => $"Opcode 0x{cdb[0]:X2}",
            };
        }

        private static string FormatSense(ReadOnlySpan<byte> sense)
        {
            if (sense.IsEmpty || sense.IndexOfAnyExcept((byte)0) < 0)
                return "";

            return FormatBytes(sense);
        }

        private static string FormatDataPreview(ReadOnlySpan<byte> data, uint bytesReturned, ReadOnlySpan<byte> sense)
        {
            if (data.IsEmpty)
                return "";

            var actualLength = GetActualReadLength(data.Length, bytesReturned, sense);
            if (actualLength <= 0)
                return "";

            var preview = data[..Math.Min(actualLength, 256)];
            var ascii = new string(preview.ToArray().Select(x => x is >= 0x20 and <= 0x7E ? (char)x : '.').ToArray());
            var hex = FormatBytes(preview);
            var suffix = actualLength > preview.Length ? $" ... +{actualLength - preview.Length} bytes" : "";
            return $"Actual={actualLength}; ASCII=\"{ascii}\"; HEX={hex}{suffix}";
        }

        private static int GetActualReadLength(int allocationLength, uint bytesReturned, ReadOnlySpan<byte> sense)
        {
            if (sense.Length >= 7 && (sense[2] & 0x20) != 0)
            {
                var residual = (sense[3] << 24) | (sense[4] << 16) | (sense[5] << 8) | sense[6];
                if (residual >= 0 && residual <= allocationLength)
                    return allocationLength - residual;
            }

            _ = bytesReturned;
            return allocationLength;
        }

        private static string FormatBytes(ReadOnlySpan<byte> bytes)
        {
            return string.Join(" ", bytes.ToArray().Select(x => x.ToString("X2")));
        }
    }
}
