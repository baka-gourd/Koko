using Microsoft.EntityFrameworkCore;

namespace Koko.Web.Data;

public sealed class TapeMetaDbContext : DbContext
{
    public TapeMetaDbContext(DbContextOptions<TapeMetaDbContext> options)
        : base(options)
    {
    }

    public DbSet<TapeMetadataArchive> TapeArchives => Set<TapeMetadataArchive>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var archive = modelBuilder.Entity<TapeMetadataArchive>();
        archive.HasKey(x => x.ArchiveXxHash128);
        archive.HasIndex(x => x.ArchivePath);
        archive.HasIndex(x => new { x.Barcode, x.GenerationNumber });
        archive.Property(x => x.ArchiveXxHash128).IsRequired();
        archive.Property(x => x.ArchivePath).IsRequired();
        archive.Property(x => x.Barcode).IsRequired();
    }
}

public sealed class TapeMetadataArchive
{
    public string ArchiveXxHash128 { get; set; } = string.Empty;

    public string Barcode { get; set; } = string.Empty;

    public string ArchivePath { get; set; } = string.Empty;

    public string ArchiveName { get; set; } = string.Empty;

    public long ArchiveSizeBytes { get; set; }

    public DateTimeOffset ArchiveLastWriteTimeUtc { get; set; }

    public DateTimeOffset IndexedAtUtc { get; set; }

    public bool Missing { get; set; }

    public string Status { get; set; } = "Pending";

    public string? Error { get; set; }

    public Guid? VolumeUuid { get; set; }

    public long? GenerationNumber { get; set; }

    public string? LtfsUpdateTime { get; set; }

    public string? LocationPartition { get; set; }

    public long? LocationStartBlock { get; set; }

    public long? FileCount { get; set; }

    public long? DirectoryCount { get; set; }

    public long? LogicalBytes { get; set; }

    public long? TotalBytes { get; set; }

    public long? UsedBytes { get; set; }

    public long? AvailableBytes { get; set; }
}
