namespace Koko.Core.Scsi.Codes.Cartridges;

public readonly record struct Initialisation(
    int Lp1,
    int Lp3,
    int Lp5
);