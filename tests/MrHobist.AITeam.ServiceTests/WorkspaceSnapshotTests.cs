using MrHobist.AITeam.Application.Abstractions;
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

    /// <summary>
    /// 2026-09-24 canli hata: proje Visual Studio'da acikken .vs/…/*.vsidx kilitli, git add TAMAMEN dusuyordu -- 23 Eylul'den
    /// beri hic goruntu yoktu. IDE dizini goruntuye girmez; geri donuste de silinmez (izlenmedigi icin "yok" sayilirdi).
    /// </summary>
    [Fact]
    public async Task Ide_dizininde_kilitli_dosya_goruntuyu_dusurmez_geri_donus_ona_dokunmaz()
    {
        var vs = Path.Combine(_work, ".vs", "Proje", "FileContentIndex");
        Directory.CreateDirectory(vs);
        var index = Path.Combine(vs, "kilitli.vsidx");
        await File.WriteAllTextAsync(index, "ide", Ct);
        await File.WriteAllTextAsync(Path.Combine(_work, "Program.cs"), "ilk hali", Ct);

        string? point;
        using (new FileStream(index, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            point = await _snap.TrackAsync(_work, Ct);
        }

        Assert.NotNull(point);
        await File.WriteAllTextAsync(Path.Combine(_work, "Program.cs"), "developer bozdu", Ct);
        Assert.True(await _snap.RestoreAsync(_work, point!, Ct));
        Assert.Equal("ilk hali", await File.ReadAllTextAsync(Path.Combine(_work, "Program.cs"), Ct));
        Assert.True(File.Exists(index), "IDE dizini geri donuste silinmemeli");
    }

    /// <summary>
    /// Iki goruntu arasi fark (gorev basinda yazilan kodun ozeti): durum + satir sayisi, Turkce harfli yol bozulmaz,
    /// disarida birakilan dizin (node_modules) listeye girmez, dosya metni goruntudeki haliyle okunur (diskteki sonraki hali degil).
    /// </summary>
    [Fact]
    public async Task Iki_goruntu_arasi_fark_ve_goruntudeki_metin_okunur()
    {
        await File.WriteAllTextAsync(Path.Combine(_work, "Program.cs"), "a\nb\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(_work, "Silinecek.cs"), "x\n", Ct);
        var from = await _snap.TrackAsync(_work, Ct);
        Assert.NotNull(from);

        await File.WriteAllTextAsync(Path.Combine(_work, "Program.cs"), "a\nc\nd\n", Ct);
        File.Delete(Path.Combine(_work, "Silinecek.cs"));
        Directory.CreateDirectory(Path.Combine(_work, "src", "Öğeler"));
        await File.WriteAllTextAsync(Path.Combine(_work, "src", "Öğeler", "Çizim.cs"), "public sealed class Çizim;\n", Ct);
        Directory.CreateDirectory(Path.Combine(_work, "ui", "node_modules", "paket"));
        await File.WriteAllTextAsync(Path.Combine(_work, "ui", "node_modules", "paket", "index.cs"), "paket\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(_work, "resim.png"), "ikili sayilmaz, desende yok", Ct);
        var to = await _snap.TrackAsync(_work, Ct);
        Assert.NotNull(to);

        var changes = await _snap.DiffAsync(_work, from!, to!, ["**/*.cs"], ["**/node_modules/**"], Ct);

        Assert.Equal(
            new List<WorkspaceChange> { new("Program.cs", 'M', 2, 1), new("Silinecek.cs", 'D', 0, 1), new("src/Öğeler/Çizim.cs", 'A', 1, 0) },
            changes.OrderBy(c => c.Path, StringComparer.Ordinal).ToList());
        Assert.Equal("public sealed class Çizim;\n", await _snap.ReadAsync(_work, to!, "src/Öğeler/Çizim.cs", Ct));

        await File.WriteAllTextAsync(Path.Combine(_work, "Program.cs"), "sonradan", Ct);
        Assert.Equal("a\nc\nd\n", await _snap.ReadAsync(_work, to!, "Program.cs", Ct));
        Assert.Null(await _snap.ReadAsync(_work, to!, "yok.cs", Ct));
        Assert.Empty(await _snap.DiffAsync(_work, to!, to!, [], [], Ct));
    }

    /// <summary>Gecersiz girdi calismayi durdurmaz: goruntu bir kolayliktir (bkz. IWorkspaceSnapshot).</summary>
    [Fact]
    public async Task Olmayan_dizin_ve_bilinmeyen_tanitici_sessizce_basarisiz_olur()
    {
        Assert.Null(await _snap.TrackAsync(Path.Combine(_root, "yok"), Ct));
        Assert.False(await _snap.RestoreAsync(_work, "0123456789abcdef0123456789abcdef01234567", Ct));
    }
}
