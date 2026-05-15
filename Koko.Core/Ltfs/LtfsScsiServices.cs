using Koko.Core.Events;
using Koko.Core.Scsi;

namespace Koko.Core.Ltfs;

public sealed class LtfsScsiServiceSession : IDisposable
{
    private LtfsScsiServiceSession(LtoTapeDrive drive, IKokoEventBus? eventBus)
    {
        Drive = drive ?? throw new ArgumentNullException(nameof(drive));
        EventBus = eventBus ?? NullKokoEventBus.Instance;
        FormatDevice = Drive;
        WriterDevice = Drive;
        FormatService = new LtfsFormatService(FormatDevice, EventBus);
        WriterService = new LtfsWriterService(WriterDevice, EventBus);
    }

    public LtoTapeDrive Drive { get; }
    public IKokoEventBus EventBus { get; }
    public ILtfsFormatDevice FormatDevice { get; }
    public ILtfsWriterDevice WriterDevice { get; }
    public LtfsFormatService FormatService { get; }
    public LtfsWriterService WriterService { get; }

    public static LtfsScsiServiceSession OpenByPhysicalDeviceObjectName(string physicalDeviceObjectName, IKokoEventBus? eventBus = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalDeviceObjectName);
        return OpenByPath($"\\\\.\\globalroot{physicalDeviceObjectName}", eventBus);
    }

    public static LtfsScsiServiceSession OpenByPath(string path, IKokoEventBus? eventBus = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new LtfsScsiServiceSession(LtoTapeDrive.OpenDriveByPath(path), eventBus);
    }

    public void Dispose()
    {
        Drive.Dispose();
    }
}
