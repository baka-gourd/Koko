namespace Koko.Core.Scsi.Codes.Cartridges;

public readonly record struct Eod(
    int Partition,
    int Dataset,
    int WrapNumber,
    int Validity,
    int PhysicalPosition
);