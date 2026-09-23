using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>
/// Golge git anlik goruntusu (ornek: opencode snapshot). Onemli olan iki sey: geri donus GERCEKTEN calisir,
/// ve projenin KENDI git deposuna hic dokunulmaz -- ajanin yazdigi proje kullanicinin deposu olabilir.
/// </summary>
public sealed class WorkspaceSnapshotTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aiteam-snap-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _work;
    private readonly GitWorkspaceSnapshot _snap;

    public WorkspaceSnapshotTests()
    {
        _work = Path.Combine(_root, "proje");
        Directory.CreateDirectory(_work);
        _snap = new GitWorkspaceSnapshot(new StoragePaths(Path.Combine(_root, "config"), Path.Combine(_root, "data")));
    }

    public void Dispose()
    {
        try
        {
            // Git nesne dosyalari SALT OKUNUR yazilir; once bayragi kaldirmadan Directory.Delete duser.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Gecici klasor kilitliyse test sonucunu etkilemez.
        }
    }

    [Fact]
    public async Task Goruntu_alinir_ve_sonraki_degisiklikler_geri_alinir()
    {
        await File.WriteAllTextAsync(Path.Combine(_work, "Program.cs"), "ilk hali", Ct);
        var point = await _snap.TrackAsync(_work, Ct);
        Assert.NotNull(point); // git bu depoda zaten sart

        await File.WriteAllTextAsync(Path.Combine(_work, "Program.cs"), "developer bozdu", Ct);
        await File.WriteAllTextAsync(Path.Combine(_work, "Yeni.cs"), "turda eklendi", Ct);

        Assert.True(await _snap.RestoreAsync(_work, point!, Ct));

        Assert.Equal("ilk hali", await File.ReadAllTextAsync(Path.Combine(_work, "Program.cs"), Ct));
        Assert.False(File.Exists(Path.Combine(_work, "Yeni.cs")), "turda eklenen dosya silinmeliydi");
    }

    /// <summary>Golge depo proje dizininin DISINDA: projenin kendi .git'i olusmaz, varsa dokunulmaz.</summary>
    [Fact]
    public async Task Projenin_kendi_git_deposuna_dokunulmaz()
    {
        await File.WriteAllTextAsync(Path.Combine(_work, "a.txt"), "x", Ct);
        var point = await _snap.TrackAsync(_work, Ct);
        Assert.NotNull(point); // git bu depoda zaten sart

        Assert.False(Directory.Exists(Path.Combine(_work, ".git")), "proje dizininde .git olusturulmus");
        Assert.True(Directory.Exists(Path.Combine(_root, "data", "snapshots")), "golge depo data/snapshots altinda olmali");
    }

    /// <summary>Gecersiz girdi calismayi durdurmaz: goruntu bir kolayliktir (bkz. IWorkspaceSnapshot).</summary>
    [Fact]
    public async Task Olmayan_dizin_ve_bilinmeyen_tanitici_sessizce_basarisiz_olur()
    {
        Assert.Null(await _snap.TrackAsync(Path.Combine(_root, "yok"), Ct));
        Assert.False(await _snap.RestoreAsync(_work, "0123456789abcdef0123456789abcdef01234567", Ct));
    }
}
