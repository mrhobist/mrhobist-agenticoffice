using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Infrastructure;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>
/// Her test kendi gecici calisma alanini alir: gercek <c>config/</c> agaci kopyalanir (ajan md'leri, bilgi,
/// akislar, sahne) ve yaninda BOS bir SQLite veritabani kurulur. Sema, uretimdeki yoldan uygulanir --
/// yani test "sema betikleri + EF eslemesi" ciftini de dogrular (drift testi, ARCHITECTURE.md §8.5).
/// Depolar elle degil DI'dan alinir: kayit yanlissa test de patlar.
/// </summary>
public sealed class StorageFixture : IDisposable
{
    private readonly ServiceProvider _provider;

    public StorageFixture()
    {
        var repoConfig = StoragePaths.Discover(AppContext.BaseDirectory).ConfigRoot;
        Root = Path.Combine(Path.GetTempPath(), "aiteam-tests", Guid.NewGuid().ToString("N"));
        var config = Path.Combine(Root, "config");
        CopyTree(repoConfig, config);
        Isolate(config);
        Paths = new StoragePaths(config, Path.Combine(Root, "data"));

        DependencyInjection.MigrateDatabase(Paths);
        _provider = new ServiceCollection().AddAiTeamStorage(Paths).BuildServiceProvider();
    }

    public string Root { get; }

    public StoragePaths Paths { get; }

    public IRunStore Runs => _provider.GetRequiredService<IRunStore>();

    public IProjectStore Projects => _provider.GetRequiredService<IProjectStore>();

    public ISettingsStore Settings => _provider.GetRequiredService<ISettingsStore>();

    public IServiceProvider Services => _provider;

    public T Get<T>()
        where T : notnull => _provider.GetRequiredService<T>();

    public void Dispose()
    {
        _provider.Dispose();

        // Havuzdaki acik SQLite baglantilari kapatilmazsa .db dosyasi kilitli kalir ve gecici dizin silinmez.
        SqliteConnection.ClearAllPools();

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
    /// Kullanicinin canli duzenlemeleri testi etkilemesin: analistin md'sinden model/efor satirlari atilir
    /// (testler VARSAYILANLARI dogrular; kullanici UI'dan eforu degistirebilir). Projeler kopyalanmaz --
    /// artik veritabaninda yasiyorlar ve her test bos bir veritabaniyla basliyor.
    /// </summary>
    private static void Isolate(string config)
    {
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
