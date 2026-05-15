using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

using Koko.Core.Ltfs;
using Koko.Core.Scsi;

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

            var drives = SetupAPI.ListTapeDeviceInterfaces()
                .Where(x => !string.IsNullOrWhiteSpace(x.DevicePath))
                .Select(x => new DebugDriveItem(
                    x.DevicePath,
                    x.DevicePath))
                .ToList();

            foreach (var drive in drives)
                DriveComboBox.Items.Add(drive);

            if (DriveComboBox.Items.Count > 0)
                DriveComboBox.SelectedIndex = 0;

            AppendLog($"Found {drives.Count} SCSI tape drive(s).");
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
            var result = await Task.Run(() => ReadIndexPartitionSchema(selected.DevicePath));
            DisplayIndex(result.Index, $"{selected.DisplayName} ({result.Source})");
            AppendLog($"Discovered LTFS schema from {selected.DevicePath}. Source={result.Source}, append={result.AppendPoint.Partition}{result.AppendPoint.Block}, dirty={result.DirtyAppendDetected}, blocksize={result.Label?.BlockSize ?? 0}.");
            foreach (var warning in result.Warnings)
                AppendLog($"WARN: {warning}");
        }
        catch (Exception ex)
        {
            ShowError("Failed to read index partition schema.", ex);
        }
        finally
        {
            SetReady();
        }
    }

    private static async Task<LtfsVolumeDiscoveryResult> ReadIndexPartitionSchema(string devicePath)
    {
        using var session = LtfsScsiServiceSession.OpenByPath(devicePath);
        var device = session.WriterDevice;

        await device.ReserveAsync();
        var removalPrevented = false;
        try
        {
            await device.PreventRemovalAsync(true);
            await device.TestUnitReadyAsync();
            removalPrevented = true;
            return await new LtfsVolumeDiscoveryService(device).DiscoverAsync();
        }
        finally
        {
            if (removalPrevented)
                await device.PreventRemovalAsync(false);
            await device.ReleaseAsync();
        }
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
}
