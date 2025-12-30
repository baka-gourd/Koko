namespace Koko.Core.Scsi.Codes.Cartridges;

public readonly record struct CartridgeContent(
    string DriveId,
    int CartridgeContentCode,
    bool PartitionedCartridge,
    bool TypeMCartridge,
    string DriveFirmwareId
);