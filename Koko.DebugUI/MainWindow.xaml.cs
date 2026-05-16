using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

using Koko.Core;
using Koko.Core.Helpers;
using Koko.Core.Ltfs;
using Koko.Core.Scsi;
using Koko.Core.Scsi.Commands;

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
            DriveComboBox.Items.Clear();

            var drives = SetupAPI.ListDevices("SCSI").Where(x =>
                x.ClassName is not null && x.ClassName.Equals("TapeDrive", StringComparison.InvariantCultureIgnoreCase));

            foreach (var drive in drives)
                DriveComboBox.Items.Add(new DebugDriveItem($"\\\\.\\globalroot{drive.PhysicalDeviceObjectName}",
                    $"\\\\.\\globalroot{drive.PhysicalDeviceObjectName}"));

            if (DriveComboBox.Items.Count > 0)
                DriveComboBox.SelectedIndex = 0;

            AppendLog($"Found SCSI tape drive(s).");
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
        if (DriveComboBox.SelectedItem is not DebugDriveItem selected)
        {
            MessageBox.Show(this, "Select a SCSI tape drive first.", "Koko Debug UI", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SetBusy($"Discovering LTFS schema from {selected.DisplayName}");
            CommandDetailsGrid.ItemsSource = null;
            var result = await Task.Run(() => ReadIndexPartitionSchema(selected.DevicePath));
            CommandDetailsGrid.ItemsSource = result.CommandTraces;
            DisplayIndex(result.Index, $"{selected.DisplayName} ({result.Source})");
            AppendLog($"Discovered LTFS schema from {selected.DevicePath}. Source={result.Source}, append={result.AppendPoint.Partition}{result.AppendPoint.Block}, dirty={result.DirtyAppendDetected}, blocksize={result.Label?.BlockSize ?? 0}.");
            foreach (var warning in result.Warnings)
                AppendLog($"WARN: {warning}");
        }
        catch (Exception ex)
        {
            if (ex is DebugReadException debugReadException)
            {
                CommandDetailsGrid.ItemsSource = debugReadException.CommandTraces;
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

    private static async Task<DebugReadResult> ReadIndexPartitionSchema(string devicePath)
    {
        var manager = DriveSessionManager.Instance.Value;
        using var lease = manager.Lease(devicePath, id => LtoTapeDrive.OpenDriveByPath(id));
        if (lease.Drive is not LtoTapeDrive lto)
            throw new InvalidOperationException("Device is not an LTO tape drive.");

        var traceDrive = new TraceScsiDrive(lto);
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
                var result = await new LtfsVolumeDiscoveryService(device).DiscoverAsync();
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

    private void ShowError(string message, Exception exception)
    {
        AppendLog($"ERROR: {message} {exception}");
        MessageBox.Show(this, $"{message}{Environment.NewLine}{exception.Message}", "Koko Debug UI", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private sealed record DebugDriveItem(string DisplayName, string DevicePath);

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
        string Sense);

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
            AddTrace(cdb, DataDirection.In, returnBuffer.Length, ok, scsiStatus, bytesReturned, senseBuffer, inner.LastTransportError);
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
            AddTrace(cdb, DataDirection.Out, dataBuffer.Length, ok, scsiStatus, bytesReturned, senseBuffer, inner.LastTransportError);
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
            AddTrace(cdb, dataDirection, 0, ok, scsiStatus, bytesReturned, senseBuffer, inner.LastTransportError);
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
            ScsiTransportError? transportError)
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
                FormatSense(senseBuffer));

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

        private static string FormatBytes(ReadOnlySpan<byte> bytes)
        {
            return string.Join(" ", bytes.ToArray().Select(x => x.ToString("X2")));
        }
    }
}
