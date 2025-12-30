namespace Koko.Core.Scsi.Codes.Cartridges;

public readonly record struct ApplicationSpecific(
    string Barcode,
    string ApplicationVendor,
    string ApplicationName,
    string ApplicationVersion
);