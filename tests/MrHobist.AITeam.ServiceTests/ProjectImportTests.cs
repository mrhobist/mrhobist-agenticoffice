using System.Diagnostics;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Common;
using MrHobist.AITeam.Application.Projects;
using MrHobist.AITeam.Application.Runs;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Projects;
using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Domain.Workflows;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>
/// Projeyi iceri alma (docs/DOMAIN.md → Projeyi iceri alma, 2026-09-26): depo disi klasoru inceleme, dil seridi, oneri,
/// ayni klasore iki proje yasagi, git taramasi ve analiz isteminde "Mevcut kod" bolumu.
/// </summary>
public sealed class ProjectImportTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly StorageFixture _fx = new();

    /// <summary>Depo DISI klasor: fixture kokunun yaninda, suruculu tam yol (Project.IsExternalDir). Ust klasor de testin (incelenir).</summary>
    private readonly string _base = Path.Combine(Path.GetTempPath(), "aiteam-import-" + Guid.NewGuid().ToString("N"));

    private string Outside => Path.Combine(_base, "anket-app");

    public void Dispose()
    {
        _fx.Dispose();
        if (Directory.Exists(_base))
        {
            foreach (var f in Directory.EnumerateFiles(_base, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal); // git nesneleri salt okunur
            }

            Directory.Delete(_base, recursive: true);
        }
    }

    private ProjectService Service() => new(_fx.Projects, new JsonWorkflowStore(_fx.Paths), _fx.Runs, new WorkspaceLocator(_fx.Paths), new RunCmdLauncher(), inspector: new WorkspaceInspector());

    private string Write(string rel, string content)
    {
        var path = Path.Combine(Outside, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Slash(string p) => p.Replace('\\', '/');

    [Fact]
    public async Task Depo_disi_klasor_incelenir_ve_iceri_alinir_ayni_klasor_ikinci_kez_alinamaz()
    {
        Write("README.md", "# Nabız Anketi\n\nHaftalık ekip anketi.\n");
        Write("src/app.ts", new string('x', 3000));
        Write("src/style.css", new string('x', 1000));
        Write("node_modules/lib/index.js", new string('x', 100_000)); // git yok: yurume budar
        Write("run.cmd", "@echo off");
        var svc = Service();

        var seen = await svc.InspectAsync(Outside, Ct);
        Assert.Equal(["TypeScript", "CSS", "Batchfile"], seen.Languages.Select(l => l.Name)); // run.cmd de kod: GitHub da sayar
        Assert.Equal([74.8, 24.9, 0.2], seen.Languages.Select(l => l.Percent));
        Assert.Equal(("Nabız Anketi", "Haftalık ekip anketi.", "anket-app"), (seen.SuggestedTitle, seen.SuggestedDescription, seen.SuggestedKey));
        Assert.True(seen.Launchable);
        Assert.False(seen.IsGit);
        Assert.Null(seen.UsedBy);

        var card = await svc.ImportAsync(new ImportProjectRequest(Outside, Key: "anket"), Ct);
        Assert.Equal(("anket", "Nabız Anketi", Slash(Outside)), (card.Key, card.Title, card.TargetDir));
        Assert.True(card.Launchable);

        // Ayni klasor, ic klasoru ve (inceleme) dis klasoru: baska projenin.
        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.ImportAsync(new ImportProjectRequest(Outside), Ct));
        Assert.Equal(ErrorCodes.ProjectDirInUse, ex.ErrorCode);
        ex = await Assert.ThrowsAsync<DomainException>(() => svc.ImportAsync(new ImportProjectRequest(Path.Combine(Outside, "src")), Ct));
        Assert.Equal(ErrorCodes.ProjectDirInUse, ex.ErrorCode);
        Assert.Equal("anket", (await svc.InspectAsync(_base, Ct)).UsedBy);
        ex = await Assert.ThrowsAsync<DomainException>(() => svc.CreateAsync(new CreateProjectRequest("ikiz", "İkiz", null, null, Slash(Outside) + "/"), Ct));
        Assert.Equal(ErrorCodes.ProjectDirInUse, ex.ErrorCode);

        // Kendi incelemesi kendini "baskasinin" saymaz.
        var own = await svc.InspectProjectAsync("anket", Ct);
        Assert.Null(own.UsedBy);
        Assert.Equal(3, own.Languages.Count);
    }

    [Fact]
    public async Task Iceri_almada_bos_alanlar_oneriden_gelir_anahtar_alinmissa_sayi_eklenir()
    {
        Write("package.json", """{ "name": "anket-app", "description": "Nabız anketi" }""");
        var svc = Service();
        await svc.CreateAsync(new CreateProjectRequest("anket-app", "Onceden var", null, null, null), Ct);

        var card = await svc.ImportAsync(new ImportProjectRequest(Outside), Ct);
        Assert.Equal(("anket-app-2", "Anket app", "Nabız anketi"), (card.Key, card.Title, card.Description));
    }

    [Fact]
    public async Task Olmayan_klasor_404_depodan_kacan_yol_400()
    {
        var svc = Service();
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => svc.InspectAsync(Path.Combine(Outside, "yok"), Ct));
        Assert.Equal(ErrorCodes.ProjectDirNotFound, missing.ErrorCode);
        var bad = await Assert.ThrowsAsync<DomainException>(() => svc.ImportAsync(new ImportProjectRequest("../disari"), Ct));
        Assert.Equal(ErrorCodes.ProjectTargetDirInvalid, bad.ErrorCode);
        bad = await Assert.ThrowsAsync<DomainException>(() => svc.ImportAsync(new ImportProjectRequest("C:/"), Ct)); // surucu koku secilemez
        Assert.Equal(ErrorCodes.ProjectTargetDirInvalid, bad.ErrorCode);
        bad = await Assert.ThrowsAsync<DomainException>(() => svc.InspectAsync("C:", Ct)); // surucuye goreli: calisma dizinine cozulmesin
        Assert.Equal(ErrorCodes.ProjectTargetDirInvalid, bad.ErrorCode);
    }

    [Fact]
    public async Task Klasor_secici_depo_disini_gezer_surucu_kokunun_ustu_yoktur()
    {
        Directory.CreateDirectory(Path.Combine(Outside, "alpha"));
        Directory.CreateDirectory(Path.Combine(Outside, "node_modules"));
        Directory.CreateDirectory(Path.Combine(Outside, ".git"));
        var svc = Service();

        var level = await svc.ListDirectoriesAsync(Outside + "\\", Ct);
        Assert.Equal(Slash(Outside), level.Path);
        Assert.Equal(Slash(_base), level.Parent);
        Assert.Equal([Slash(Path.Combine(Outside, "alpha"))], level.Dirs.Select(d => d.Path)); // gizli ve paket klasoru yok
        Assert.NotEmpty(level.Drives!);

        var drive = Slash(Path.GetPathRoot(Outside)!); // "C:/"
        var root = await svc.ListDirectoriesAsync(drive[..2], Ct);
        Assert.Equal((drive, (string?)null), (root.Path, root.Parent));
        var up = await svc.ListDirectoriesAsync(drive + "Users", Ct);
        Assert.Equal(drive, up.Parent);
    }

    [Fact]
    public async Task Git_deposunda_gitignore_uyulur_dal_ve_kimliksiz_uzak_adres_okunur()
    {
        if (!GitAvailable())
        {
            return; // git yoksa tarayici zaten klasor yurur; o yol ustteki testte
        }

        Write(".gitignore", "generated/\n");
        Write("Program.cs", new string('x', 500));
        Write("generated/Big.cs", new string('x', 50_000));
        Git("init", "--quiet", "-b", "gelistirme");
        Git("remote", "add", "origin", "https://ad:gizli-belirtec@example.com/ekip/anket.git");

        var scan = await new WorkspaceInspector().ScanAsync(Outside, Ct);
        Assert.True(scan.IsGit);
        Assert.Equal("gelistirme", scan.GitBranch);
        Assert.Equal("https://example.com/ekip/anket.git", scan.GitRemote);
        // generated/Big.cs (izlenmeyen + yok sayilan) sayilmaz; Program.cs (izlenmeyen ama yok sayilmayan: devam eden is) sayilir.
        Assert.Equal([("C#", 500L)], scan.Code.Languages.Select(l => (l.Name, l.Bytes)));
        Assert.Equal("git@github.com:ekip/anket.git", WorkspaceInspector.StripCredentials("git@github.com:ekip/anket.git"));
    }

    [Fact]
    public void Analiz_istemi_dolu_dizinde_mevcut_kodu_ve_git_kuralini_yazar_bos_dizinde_eski_cumleyi()
    {
        var run = new Run("20260926-000000-im", "is", "Giris sayfasina sifre sifirlama ekle", Sensitivity.Anthropic, DateTimeOffset.UtcNow, RunStatus.Running, Project: "anket");
        var wf = new Workflow("default", "Varsayilan", 3, null, [new("analiz", "Analiz", StageKind.Analyze, "dev", "pm", ""), new("gelistirme", "Gelistirme", StageKind.Implement, "dev", "dev", "")]);
        var scan = new WorkspaceScan(true, Codebase.Measure([new("src/Api/Program.cs", 700), new("ui/app.vue", 300), new("Anket.slnx", 50)]), false, true, "main", null, "Anket", "");

        var full = Prompts.AnalystBrief(run, wf, "C:/Work/Anket", existing: scan);
        Assert.Contains("# Mevcut kod", full, StringComparison.Ordinal);
        Assert.Contains("diller: C# %70 · Vue %30", full, StringComparison.Ordinal);
        Assert.Contains("`Anket.slnx`", full, StringComparison.Ordinal);
        Assert.Contains("`main` dalı", full, StringComparison.Ordinal);
        Assert.Contains("commit atma", full, StringComparison.Ordinal);
        Assert.DoesNotContain("Dizinde zaten kod olabilir", full, StringComparison.Ordinal);

        var empty = Prompts.AnalystBrief(run, wf, "C:/Work/Bos", existing: new WorkspaceScan(true, Codebase.Empty, false, false, null, null, "Bos", ""));
        Assert.DoesNotContain("# Mevcut kod", empty, StringComparison.Ordinal);
        Assert.Contains("Dizinde zaten kod olabilir", empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ofisin_kendi_deposu_ic_klasoru_ve_ust_klasoru_iceri_alinamaz()
    {
        var svc = Service();
        var repo = Slash(_fx.Root);
        foreach (var dir in new[] { repo, repo + "/config", Slash(Path.GetDirectoryName(_fx.Root)!) })
        {
            var ex = await Assert.ThrowsAsync<DomainException>(() => svc.InspectAsync(dir, Ct));
            Assert.Equal(ErrorCodes.ProjectDirReserved, ex.ErrorCode);
            ex = await Assert.ThrowsAsync<DomainException>(() => svc.ImportAsync(new ImportProjectRequest(dir, Key: "ofis"), Ct));
            Assert.Equal(ErrorCodes.ProjectDirReserved, ex.ErrorCode);
        }

        // Yeni proje de ayni kurala bagli (kontrol locator'da): depo ici hedef goreli yazilir.
        var created = await Assert.ThrowsAsync<DomainException>(() => svc.CreateAsync(new CreateProjectRequest("ofis", "Ofis", null, null, repo + "/projects/x"), Ct));
        Assert.Equal(ErrorCodes.ProjectDirReserved, created.ErrorCode);
    }

    [Fact]
    public async Task Okuma_uclari_hedef_dizini_olusturmaz()
    {
        var svc = Service();
        var card = await svc.CreateAsync(new CreateProjectRequest("taze", "Taze", null, null, Slash(Outside)), Ct);
        Assert.False(card.Launchable);
        await svc.ListAsync(Ct);
        var own = await svc.InspectProjectAsync("taze", Ct); // is baslamadi: dizin yok, 0 dosya
        Assert.Equal((0, 0), (own.Files, own.Languages.Count));
        Assert.False(Directory.Exists(Outside));
    }

    private static bool GitAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            p!.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Outside, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.WaitForExit(20_000);
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {p.StandardError.ReadToEnd()}");
    }

    private sealed class RunCmdLauncher : IProjectLauncher
    {
        public bool CanLaunch(string projectRoot) => File.Exists(Path.Combine(projectRoot, "run.cmd"));

        public int Launch(string projectRoot) => 4242;
    }
}
