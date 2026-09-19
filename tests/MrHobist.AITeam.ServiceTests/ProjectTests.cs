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
        var runs = new JsonlRunStore(_fx.Paths);
        var svc = new ProjectService(new JsonProjectStore(_fx.Paths), new JsonWorkflowStore(_fx.Paths), runs);

        var card = await svc.CreateAsync(new CreateProjectRequest("hello-world", "Hello World Console", ".NET konsol", null, null), Ct);
        Assert.Equal(("default", "projects/hello-world", Project.LocalOwner, 0), (card.Workflow, card.TargetDir, card.OwnerId, card.Runs));
        Assert.True(File.Exists(Path.Combine(_fx.Paths.ProjectsDir, "hello-world.json")));

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
        ex = await Assert.ThrowsAsync<DomainException>(() => svc.DeleteAsync("hello-world", Ct));
        Assert.Equal(ErrorCodes.ProjectInUse, ex.ErrorCode);

        var empty = await svc.CreateAsync(new CreateProjectRequest("bos", "Bos", null, null, null), Ct);
        await svc.DeleteAsync(empty.Key, Ct);
        Assert.Single(await svc.ListAsync(Ct));
    }

    [Fact]
    public async Task Calisma_listesi_projeye_gore_suzulur()
    {
        var runs = new JsonlRunStore(_fx.Paths);
        await runs.CreateAsync(new Run("20260919-000001-a", "a", "b", Sensitivity.Anthropic, DateTimeOffset.UtcNow, RunStatus.Running, Project: "p1"), Ct);
        await runs.CreateAsync(new Run("20260919-000002-b", "b", "b", Sensitivity.Anthropic, DateTimeOffset.UtcNow, RunStatus.Running, Project: "p2"), Ct);
        await runs.CreateAsync(new Run("20260919-000003-c", "c", "b", Sensitivity.Anthropic, DateTimeOffset.UtcNow, RunStatus.Running, Project: "p1"), Ct);

        Assert.Equal(["20260919-000003-c", "20260919-000001-a"], (await runs.ListAsync(10, Ct, "p1")).Select(r => r.Id));
        Assert.Equal(3, (await runs.ListAsync(10, Ct)).Count);
        Assert.Single(await runs.ListAsync(1, Ct, "p1"));
    }
}
