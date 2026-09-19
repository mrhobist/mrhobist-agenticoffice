using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>
/// Gercek <c>config/</c> agacini gecici dizine kopyalar; testler orada yazar, depo kirlenmez.
/// Yol, test dll'inden yukari cikilarak bulunur.
/// </summary>
public sealed class StorageFixture : IDisposable
{
    public StorageFixture()
    {
        var repoConfig = StoragePaths.Discover(AppContext.BaseDirectory).ConfigRoot;
        Root = Path.Combine(Path.GetTempPath(), "aiteam-tests", Guid.NewGuid().ToString("N"));
        var config = Path.Combine(Root, "config");
        CopyTree(repoConfig, config);
        Isolate(config);
        Paths = new StoragePaths(config, Path.Combine(Root, "runs"));
    }

    public string Root { get; }

    public StoragePaths Paths { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Gecici dizin; kilitliyse isletim sistemi temizler.
        }
    }

    /// <summary>
    /// Kullanicinin canli duzenlemeleri testi etkilemesin: projeler kopyalanmaz (testler kendini ekler) ve analistin
    /// md'sinden model/efor satirlari atilir (testler VARSAYILANLARI dogrular; kullanici UI'dan eforu degistirebilir).
    /// </summary>
    private static void Isolate(string config)
    {
        var projects = Path.Combine(config, "projects");
        if (Directory.Exists(projects))
        {
            foreach (var f in Directory.EnumerateFiles(projects))
            {
                File.Delete(f);
            }
        }

        var analyst = Path.Combine(config, "agents", "analyst.md");
        if (File.Exists(analyst))
        {
            var lines = File.ReadAllLines(analyst)
                .Where(l => !l.StartsWith("effort:", StringComparison.Ordinal) && !l.StartsWith("model:", StringComparison.Ordinal));
            File.WriteAllLines(analyst, lines);
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
