namespace Koko.Web.Storage;

public sealed class KokoStorageOptions
{
    public const string SectionName = "Koko";

    public string DataDirectory { get; set; } = "data";

    public string TapeDataDirectory { get; set; } = "tape";

    public string TemporaryDataDirectory { get; set; } = "temp";
}

public sealed class KokoStoragePaths
{
    public KokoStoragePaths(IHostEnvironment environment, Microsoft.Extensions.Options.IOptions<KokoStorageOptions> options)
    {
        var value = options.Value;
        DataDirectory = Resolve(environment.ContentRootPath, value.DataDirectory);
        TapeDataDirectory = Resolve(environment.ContentRootPath, value.TapeDataDirectory);
        TemporaryDataDirectory = Resolve(environment.ContentRootPath, value.TemporaryDataDirectory);
        TapeMetaDatabasePath = Path.Combine(DataDirectory, "TapeMeta.db");
    }

    public string DataDirectory { get; }

    public string TapeDataDirectory { get; }

    public string TemporaryDataDirectory { get; }

    public string TapeMetaDatabasePath { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(TapeDataDirectory);
        Directory.CreateDirectory(TemporaryDataDirectory);
    }

    private static string Resolve(string contentRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Koko storage directories must not be blank.");

        var resolved = Path.IsPathRooted(path)
            ? path
            : Path.Combine(contentRoot, path);

        return Path.GetFullPath(resolved);
    }
}
