using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Common;
using MrHobist.AITeam.Application.Projects;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Projects;
using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>Projeler (docs/DOMAIN.md → Projeler): dosya gidis-donusu, kart ozeti, silme kurali, is → proje zorunlulugu.</summary>
public sealed class ProjectTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Proje_olusur_listelenir_guncellenir_silinir()
    {
        var runs = _fx.Runs;
        var svc = new ProjectService(_fx.Projects, new JsonWorkflowStore(_fx.Paths), runs, new WorkspaceLocator(_fx.Paths), new FakeLauncher());

        var card = await svc.CreateAsync(new CreateProjectRequest("hello-world", "Hello World Console", ".NET konsol", null, null), Ct);
        Assert.Equal(("default", "projects/hello-world", Project.LocalOwner, 0), (card.Workflow, card.TargetDir, card.OwnerId, card.Runs));
        // Renk paletten (ilk bos), sira sona; ikinci proje farkli renk alir; reorder sirayi yazar, ray/kanban bu sirayi okur.
        Assert.Equal((Project.Palette[0], 0), (card.Color, card.Order));
        var second = await svc.CreateAsync(new CreateProjectRequest("ikinci", "İkinci", null, null, null), Ct);
        Assert.Equal((Project.Palette[1], 1), (second.Color, second.Order));
        var reordered = await svc.ReorderAsync(new ReorderRequest(["ikinci", "hello-world"]), Ct);
        Assert.Equal(["ikinci", "hello-world"], reordered.Select(p => p.Key));
        Assert.Equal([0, 1], reordered.Select(p => p.Order));
        var recolored = await svc.UpdateAsync("ikinci", new ProjectModel("İkinci", null, null, null, "#123456"), Ct);
        Assert.Equal(("#123456", 0), (recolored.Color, recolored.Order));
        var bad = await Assert.ThrowsAsync<DomainException>(() => svc.UpdateAsync("ikinci", new ProjectModel("İkinci", null, null, null, "kirmizi"), Ct));
        Assert.Equal(ErrorCodes.ProjectInvalidColor, bad.ErrorCode);
        await svc.DeleteAsync("ikinci", false, Ct);
        Assert.Equal("hello-world", (await _fx.Projects.LoadAsync("hello-world", Ct)).Key); // silinen komsu digerini goturmedi

        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.CreateAsync(new CreateProjectRequest("hello-world", "x", null, null, null), Ct));
        Assert.Equal(ErrorCodes.ProjectExists, ex.ErrorCode);
        ex = await Assert.ThrowsAsync<DomainException>(() => svc.CreateAsync(new CreateProjectRequest("kotu", "x", null, "yok", null), Ct));
        Assert.Equal(ErrorCodes.WorkflowNotFound, ex.ErrorCode);
        ex = await Assert.ThrowsAsync<DomainException>(() => svc.CreateAsync(new CreateProjectRequest("kacak", "x", null, null, "../disari"), Ct));
        Assert.Equal(ErrorCodes.ProjectTargetDirInvalid, ex.ErrorCode);

        var updated = await svc.UpdateAsync("hello-world", new ProjectModel("Hello World", "aciklama", "tasarimli", "apps/hello"), Ct);
        Assert.Equal(("Hello World", "tasarimli", "apps/hello"), (updated.Title, updated.Workflow, updated.TargetDir));
        Assert.Single(await svc.ListAsync(Ct));

        // Icinde calisma varsa silinmez; kart sayilari calismalari toplar.
        await runs.CreateAsync(new Run("20260919-000000-p1", "is", "brief", Sensitivity.Anthropic, DateTimeOffset.UtcNow, RunStatus.AwaitingApproval, TotalCostUsd: 0.5m, Project: "hello-world"), Ct);
        var withRun = await svc.GetAsync("hello-world", Ct);
        Assert.Equal((1, 1, 0.5m), (withRun.Runs, withRun.AwaitingApproval, withRun.TotalCostUsd));
        ex = await Assert.ThrowsAsync<DomainException>(() => svc.DeleteAsync("hello-world", false, Ct));
        Assert.Equal(ErrorCodes.ProjectInUse, ex.ErrorCode);

        var empty = await svc.CreateAsync(new CreateProjectRequest("bos", "Bos", null, null, null), Ct);
        var gone = await svc.DeleteAsync(empty.Key, false, Ct);
        Assert.Equal((0, false), (gone.RunsDeleted, gone.FilesDeleted));
        Assert.Single(await svc.ListAsync(Ct));
    }

    /// <summary>
    /// Proje butcesi (2026-09-22 kullanici karari): varsayilan SINIRSIZ; $ ve token ayri ayri verilir;
    /// kart projenin butun calismalarinin token toplamini gosterir. Sifir/negatif tavan reddedilir --
    /// 0 "sinirsiz" degil "hicbir is kosmasin" demek olurdu ve proje sessizce kilitlenirdi.
    /// </summary>
    [Fact]
    public async Task Proje_butcesi_varsayilan_sinirsiz_token_toplami_kartta_gorunur()
    {
        var runs = _fx.Runs;
        var svc = new ProjectService(_fx.Projects, new JsonWorkflowStore(_fx.Paths), runs, new WorkspaceLocator(_fx.Paths), new FakeLauncher());

        // Varsayilan: butce alanlari bos -> sinirsiz.
        var sinirsiz = await svc.CreateAsync(new CreateProjectRequest("sinirsiz", "Sinirsiz", null, null, null), Ct);
        Assert.Equal((null, (long?)null), (sinirsiz.MaxCostUsd, sinirsiz.MaxTokens));
        Assert.Equal((0L, 0L), (sinirsiz.TotalInputTokens, sinirsiz.TotalOutputTokens));

        // Iki olcu bagimsiz verilebilir.
        var butceli = await svc.CreateAsync(new CreateProjectRequest("butceli", "Butceli", null, null, null, null, 12.5m, 1_000_000), Ct);
        Assert.Equal((12.5m, (long?)1_000_000), (butceli.MaxCostUsd, butceli.MaxTokens));

        // Yalniz token tavani da gecerli: abonelikte asil tukenen kaynak budur.
        var yalnizToken = await svc.UpdateAsync("sinirsiz", new ProjectModel("Sinirsiz", null, null, null, null, null, 500), Ct);
        Assert.Equal((null, (long?)500), (yalnizToken.MaxCostUsd, yalnizToken.MaxTokens));

        // Sifir ve negatif reddedilir (ikisi de ayri kapidan).
        var sifir = await Assert.ThrowsAsync<DomainException>(() => svc.UpdateAsync("butceli", new ProjectModel("Butceli", null, null, null, null, 0m), Ct));
        Assert.Equal(ErrorCodes.ProjectBudgetInvalid, sifir.ErrorCode);
        var negatif = await Assert.ThrowsAsync<DomainException>(() => svc.UpdateAsync("butceli", new ProjectModel("Butceli", null, null, null, null, null, -1), Ct));
        Assert.Equal(ErrorCodes.ProjectBudgetInvalid, negatif.ErrorCode);

        // Kart token'i calismalardan toplar; baska projenin isi karismaz.
        await runs.CreateAsync(new Run("20260922-000001-t1", "a", "b", Sensitivity.Anthropic, DateTimeOffset.UtcNow, RunStatus.Completed, Project: "butceli", InputTokens: 1200, OutputTokens: 300), Ct);
        await runs.CreateAsync(new Run("20260922-000002-t2", "b", "b", Sensitivity.Anthropic, DateTimeOffset.UtcNow, RunStatus.Completed, Project: "butceli", InputTokens: 800, OutputTokens: 200), Ct);
        await runs.CreateAsync(new Run("20260922-000003-t3", "c", "b", Sensitivity.Anthropic, DateTimeOffset.UtcNow, RunStatus.Completed, Project: "sinirsiz", InputTokens: 9999, OutputTokens: 9999), Ct);

        var kart = await svc.GetAsync("butceli", Ct);
        Assert.Equal((2000L, 500L), (kart.TotalInputTokens, kart.TotalOutputTokens));
        Assert.Equal((12.5m, (long?)1_000_000), (kart.MaxCostUsd, kart.MaxTokens));
    }

    [Fact]
    public async Task Calisma_listesi_projeye_gore_suzulur()
    {
        var runs = _fx.Runs;
        await runs.CreateAsync(new Run("20260919-000001-a", "a", "b", Sensitivity.Anthropic, DateTimeOffset.UtcNow, RunStatus.Running, Project: "p1"), Ct);
        await runs.CreateAsync(new Run("20260919-000002-b", "b", "b", Sensitivity.Anthropic, DateTimeOffset.UtcNow, RunStatus.Running, Project: "p2"), Ct);
        await runs.CreateAsync(new Run("20260919-000003-c", "c", "b", Sensitivity.Anthropic, DateTimeOffset.UtcNow, RunStatus.Running, Project: "p1"), Ct);

        Assert.Equal(["20260919-000003-c", "20260919-000001-a"], (await runs.ListAsync(10, Ct, "p1")).Select(r => r.Id));
        Assert.Equal(3, (await runs.ListAsync(10, Ct)).Count);
        Assert.Single(await runs.ListAsync(1, Ct, "p1"));
    }

    /// <summary>Baslatici yok: testler surec acmaz.</summary>
    [Fact]
    public async Task Silme_suren_isi_engeller_bitmis_gecmisi_ve_istenirse_dosyalari_siler()
    {
        var runs = _fx.Runs;
        var locator = new WorkspaceLocator(_fx.Paths);
        var svc = new ProjectService(_fx.Projects, new JsonWorkflowStore(_fx.Paths), runs, locator, new FakeLauncher());
        var card = await svc.CreateAsync(new CreateProjectRequest("silinecek", "Silinecek", null, null, "apps/silinecek"), Ct);
        var root = locator.RootOf(new Project(card.Key, card.Title, "", card.Workflow, card.TargetDir, Project.LocalOwner, card.CreatedAt, card.Color));
        await File.WriteAllTextAsync(Path.Combine(root, "run.cmd"), "@echo off", Ct);

        await runs.CreateAsync(new Run("20260920-000000-a1", "bitmis", "brief", Sensitivity.Anthropic, DateTimeOffset.UtcNow, RunStatus.Completed, Project: "silinecek"), Ct);
        await runs.CreateAsync(new Run("20260920-000000-a2", "suren", "brief", Sensitivity.Anthropic, DateTimeOffset.UtcNow, RunStatus.AwaitingInput, Project: "silinecek"), Ct);
        await runs.CreateAsync(new Run("20260920-000000-b1", "baska", "brief", Sensitivity.Anthropic, DateTimeOffset.UtcNow, RunStatus.Completed, Project: "baska"), Ct);

        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.DeleteAsync("silinecek", true, Ct));
        Assert.Equal(ErrorCodes.ProjectInUse, ex.ErrorCode);
        Assert.True(Directory.Exists(root)); // reddedilen silme hicbir seye dokunmaz

        // Suren is bitti → silinir: bu projenin gecmisi gider, baska projenin kalir, dosyalar istendigi icin gider.
        await runs.UpdateAsync((await runs.GetAsync("20260920-000000-a2", Ct))! with { Status = RunStatus.Cancelled }, Ct);
        var result = await svc.DeleteAsync("silinecek", true, Ct);
        Assert.Equal((2, true, "apps/silinecek"), (result.RunsDeleted, result.FilesDeleted, result.TargetDir));
        Assert.False(Directory.Exists(root));
        Assert.Null(await runs.GetAsync("20260920-000000-a1", Ct));
        Assert.NotNull(await runs.GetAsync("20260920-000000-b1", Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => _fx.Projects.LoadAsync("silinecek", Ct));

        // Dosyalar istenmezse hedef dizin yerinde kalir.
        var keep = await svc.CreateAsync(new CreateProjectRequest("kalan", "Kalan", null, null, "apps/kalan"), Ct);
        var keepRoot = locator.RootOf(new Project(keep.Key, keep.Title, "", keep.Workflow, keep.TargetDir, Project.LocalOwner, keep.CreatedAt, keep.Color));
        var kept = await svc.DeleteAsync("kalan", false, Ct);
        Assert.False(kept.FilesDeleted);
        Assert.True(Directory.Exists(keepRoot));
    }

    [Fact]
    public async Task Klasor_secici_depo_icini_listeler_gizlileri_atlar_disari_cikmaz()
    {
        var locator = new WorkspaceLocator(_fx.Paths);
        var svc = new ProjectService(_fx.Projects, new JsonWorkflowStore(_fx.Paths), _fx.Runs, locator, new FakeLauncher());
        var repo = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_fx.Paths.ConfigRoot))!;
        Directory.CreateDirectory(Path.Combine(repo, "apps", "alpha", "src"));
        Directory.CreateDirectory(Path.Combine(repo, "apps", "alpha", "bin"));
        Directory.CreateDirectory(Path.Combine(repo, "apps", "alpha", ".git"));
        Directory.CreateDirectory(Path.Combine(repo, "apps", "beta"));

        var rootLevel = await svc.ListDirectoriesAsync(null, Ct);
        Assert.Null(rootLevel.Parent);
        Assert.Contains(rootLevel.Dirs, d => d.Path == "apps");
        Assert.DoesNotContain(rootLevel.Dirs, d => d.Name == "runs"); // calisma gecmisi hedef dizin olamaz

        var apps = await svc.ListDirectoriesAsync("apps/", Ct);
        Assert.Equal(("apps", ""), (apps.Path, apps.Parent));
        Assert.Equal(["alpha", "beta"], apps.Dirs.Select(d => d.Name));

        var alpha = await svc.ListDirectoriesAsync("apps\\alpha", Ct);
        Assert.Equal("apps", alpha.Parent);
        Assert.Equal(["apps/alpha/src"], alpha.Dirs.Select(d => d.Path)); // bin ve .git gizli

        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.ListDirectoriesAsync("../disari", Ct));
        Assert.Equal(ErrorCodes.ProjectTargetDirInvalid, ex.ErrorCode);
    }

    private sealed class FakeLauncher : IProjectLauncher
    {
        public bool CanLaunch(string projectRoot) => File.Exists(Path.Combine(projectRoot, "run.cmd"));

        public int Launch(string projectRoot) => CanLaunch(projectRoot) ? 4242 : throw new DomainException(ErrorCodes.ProjectLaunchMissing, "run.cmd yok");
    }
}
