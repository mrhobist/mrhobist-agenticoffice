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
