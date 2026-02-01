using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DevWinUI;

using Koko.Core;
using Koko.Core.Scsi;

using Serilog;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Koko.Core.Helpers;
using Koko.Helpers;
using Koko.Core.Scsi.Parsers;

namespace Koko.DrivePages.LTODrive;

public sealed partial class LTOCommandPaletteViewModel : ObservableObject
{
    private const int CdbByteCount = 16;
    private const int DefaultDataFallbackBytes = 64;

    public LTOCommandPaletteViewModel(LtoCommandPaletteNavArgs args)
    {
        DevicePath = args.DevicePath;

        ScsiCommandDisplay = string.Empty;
        ScsiDataDisplay = string.Empty;
        InfoText = string.Empty;

        Timeout = 600;
        Direction = DataDirection.In;
    }

    [ObservableProperty]
    public partial string? DevicePath { get; set; }

    [ObservableProperty]
    public partial double Timeout { get; set; }

    [ObservableProperty]
    public partial DataDirection Direction { get; set; }

    private readonly byte[] _scsiCommand = new byte[CdbByteCount];

    // 用 List<byte> 保存“有效长度”的数据（等价 VB 的 dataData）
    private readonly List<byte> _scsiData = new(capacity: 16);

    // RelayCommand 的 CanExecute 源：这里保持你的设计
    private bool CanSend { get; set; } = true;

    [ObservableProperty]
    public partial string ScsiCommandDisplay { get; set; }

    [ObservableProperty]
    public partial string ScsiDataDisplay { get; set; }

    [ObservableProperty]
    public partial string InfoText { get; set; }

    partial void OnScsiCommandDisplayChanged(string value)
    {
        ParseIntoBytes16(value, _scsiCommand);
        Log.Debug("ScsiCommand={@hex}", _scsiCommand);
    }

    partial void OnScsiDataDisplayChanged(string value)
    {
        _scsiData.Clear(); // 关键：必须清空，避免累加

        if (string.IsNullOrWhiteSpace(value))
        {
            Log.Debug("ScsiData=<empty>");
            return;
        }

        var raw = value.Replace('_', '0')
            .ToUpperInvariant()
            .Where(c => (c is >= '0' and <= '9') || (c is >= 'A' and <= 'F'))
            .ToArray();

        // 必须是偶数个 hex 字符
        if ((raw.Length & 1) == 1)
        {
            // 你原来是直接把显示清空，这里保留同样行为
            ScsiDataDisplay = string.Empty;
            return;
        }

        // 每 2 个 hex 字符组成 1 字节：raw[0]=hi, raw[1]=lo
        for (var i = 0; i < raw.Length; i += 2)
        {
            var hi = HexToNibble(raw[i]);
            var lo = HexToNibble(raw[i + 1]);
            _scsiData.Add((byte)((hi << 4) | lo));
        }

        Log.Debug("ScsiData={@hex}", _scsiData.ToArray());
    }

    private static void ParseIntoBytes16(string? input, byte[] dest16)
    {
        Array.Clear(dest16, 0, CdbByteCount);

        if (string.IsNullOrWhiteSpace(input))
            return;

        var hexChars = input
            .Replace('_', '0')
            .ToUpperInvariant()
            .Where(c => (c is >= '0' and <= '9') || (c is >= 'A' and <= 'F'))
            .Take(CdbByteCount * 2)
            .ToArray();

        for (var i = 0; i < CdbByteCount; i++)
        {
            var hiIndex = i * 2;
            var loIndex = hiIndex + 1;

            var hi = hiIndex < hexChars.Length ? HexToNibble(hexChars[hiIndex]) : 0;
            var lo = loIndex < hexChars.Length ? HexToNibble(hexChars[loIndex]) : 0;

            dest16[i] = (byte)((hi << 4) | lo);
        }
    }

    private static int HexToNibble(char c)
        => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => 0
        };

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendScsi()
    {
        using (Log.PushMethod())
        {
            CanSend = false;
            if (DevicePath is null)
            {
                await MessageBox.ShowAsync("Device Error", "Device path is null");
                return;
            }

            var manager = DriveSessionManager.Instance.Value;
            using var lease = manager.Lease(DevicePath, id => LtoTapeDrive.OpenDriveByPath($@"\\.\globalroot{id}"));
            if (lease.Drive is not LtoTapeDrive lto)
            {
                await MessageBox.ShowAsync("Device Error", "Device is not a LTO Drive");
                return;
            }

            Span<byte> sense = stackalloc byte[IOControl.DefaultSenseLength];
            sense.Clear();

            var dataLen = _scsiData.Count > 0 ? _scsiData.Count : DefaultDataFallbackBytes;
            var dataArray = new byte[dataLen];

            if (_scsiData.Count > 0)
                _scsiData.CopyTo(dataArray, 0);

            Span<byte> data = dataArray.AsSpan();

            var timeoutMs = Timeout <= 0 ? 0u :
                Timeout >= uint.MaxValue ? uint.MaxValue :
                (uint)Timeout;

            bool ok;
            if (Direction == DataDirection.In)
            {
                ok = lto.ScsiRead(_scsiCommand.AsSpan(), data, timeoutMs,
                    out var scsiStatus, out uint bytesReturned, sense);
            }
            else // DataDirection.Out
            {
                ok = lto.ScsiWrite(_scsiCommand.AsSpan(), data, timeoutMs,
                    out var scsiStatus, out uint bytesReturned, sense);
            }

            if (!ok)
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                Log.Error(new Win32Exception(err), "LTO Error");
                InfoText = FormatToText(_scsiCommand, data, sense);
                return;
            }

            if (Direction == DataDirection.In)
            {
                ScsiDataDisplay = Convert.ToHexString(data);
            }

            InfoText = FormatToText(_scsiCommand, data, sense);
            CanSend = true;
        }
    }

    private static string FormatToText(ReadOnlySpan<byte> command, ReadOnlySpan<byte> data, ReadOnlySpan<byte> sense)
    {
        var sb = new StringBuilder();

        sb.AppendLine("CDB");
        sb.AppendLine(HexDump.Format(command));

        if (data.Length != 0)
        {
            sb.AppendLine("PARAM");
            sb.AppendLine(HexDump.Format(data));
        }

        sb.AppendLine("SENSE");
        sb.AppendLine(SenseParser.ParseSense(sense));

        return sb.ToString();
    }
}

public sealed record LtoCommandPaletteNavArgs(string DevicePath);
