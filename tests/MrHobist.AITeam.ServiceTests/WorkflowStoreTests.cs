using MrHobist.AITeam.Application.Workflows;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Workflows;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

public sealed class WorkflowStoreTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private WorkflowService Service() => new(new JsonWorkflowStore(_fx.Paths), new MarkdownAgentStore(_fx.Paths));

    [Fact]
    public async Task Gercek_akislar_yuklenir()
    {
        var store = new JsonWorkflowStore(_fx.Paths);
        Assert.Equal(["default", "tasarimli"], await store.ListKeysAsync(CancellationToken.None));

        var wf = await store.LoadAsync("default", CancellationToken.None);
        Assert.True(wf.IsDefault);
        Assert.Equal("organizer", wf.HandoffRole);
        Assert.Equal(StageKind.Analyze, wf.Stages[0].Kind);
        Assert.Equal("manager", wf.Stages[^1].Role);
        Assert.DoesNotContain(wf.Stages, s => s.Role == "designer");
        Assert.Contains("organizer", wf.Roles);

        var tasarimli = await store.LoadAsync("tasarimli", CancellationToken.None);
        Assert.Contains(tasarimli.Stages, s => s.Kind == StageKind.Design && s.Role == "designer");
    }

    [Fact]
    public async Task Her_akis_gercek_ekiple_uyumlu()
    {
        var team = await new MarkdownAgentStore(_fx.Paths).LoadTeamAsync(CancellationToken.None);
        var store = new JsonWorkflowStore(_fx.Paths);
        foreach (var key in await store.ListKeysAsync(CancellationToken.None))
        {
            (await store.LoadAsync(key, CancellationToken.None)).ValidateAgainst(team);
        }
    }

    [Fact]
    public async Task Yaz_oku_yorum_korunur()
    {
        var store = new JsonWorkflowStore(_fx.Paths);
        var wf = await store.LoadAsync("default", CancellationToken.None);
        await new JsonWorkflowStore(_fx.Paths).SaveAsync(wf with { MaxReviewRounds = 5 }, CancellationToken.None);
        var text = await File.ReadAllTextAsync(_fx.Paths.WorkflowFile("default"));
        Assert.Contains("\"_comment\"", text, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"analyze\"", text, StringComparison.Ordinal);
        Assert.Contains("\"handoffRole\": \"organizer\"", text, StringComparison.Ordinal);
        Assert.Equal(5, (await store.LoadAsync("default", CancellationToken.None)).MaxReviewRounds);
    }

    [Fact]
    public async Task Servis_bozuk_akisi_yazmaz()
    {
        var service = Service();
        var model = await service.GetAsync("default", CancellationToken.None);
        var broken = new WorkflowModel(model.Title, model.MaxReviewRounds, model.HandoffRole, model.Stages.Where(s => s.Kind != StageKind.Implement).ToList());
        var before = await File.ReadAllTextAsync(_fx.Paths.WorkflowFile("default"));
        var ex = await Assert.ThrowsAsync<DomainException>(() => service.UpsertAsync("default", broken, CancellationToken.None));
        Assert.Equal(ErrorCodes.WorkflowNoImplement, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(_fx.Paths.WorkflowFile("default")));
    }

    [Fact]
    public async Task Servis_ekipte_olmayan_rolu_yazmaz()
    {
        var service = Service();
        var model = await service.GetAsync("default", CancellationToken.None);
        var stages = model.Stages.Select(s => s.Kind == StageKind.Implement ? s with { Role = "devops" } : s).ToList();
        var ex = await Assert.ThrowsAsync<DomainException>(
            () => service.UpsertAsync("default", new WorkflowModel(model.Title, 3, model.HandoffRole, stages), CancellationToken.None));
        Assert.Equal(ErrorCodes.WorkflowUnknownRole, ex.ErrorCode);
    }

    [Fact]
    public async Task Yeni_akis_olusur_listelenir_silinir()
    {
        var service = Service();
        var model = await service.GetAsync("default", CancellationToken.None);
        var lean = new WorkflowModel("Hizli", 1, null, model.Stages.Where(s => s.Role != "manager").ToList());

        var created = await service.UpsertAsync("hizli", lean, CancellationToken.None);
        Assert.Equal("hizli", created.Key);
        Assert.Null(created.HandoffRole);
        Assert.True(File.Exists(_fx.Paths.WorkflowFile("hizli")));

        var list = await service.ListAsync(CancellationToken.None);
        Assert.Equal("default", list[0].Key);
        Assert.Contains(list, i => i.Key == "hizli" && !i.IsDefault && i.StageCount == 3 && !i.Roles.Contains("organizer"));

        await service.DeleteAsync("hizli", CancellationToken.None);
        Assert.False(File.Exists(_fx.Paths.WorkflowFile("hizli")));
        var nf = await Assert.ThrowsAsync<DomainException>(() => service.GetAsync("hizli", CancellationToken.None));
        Assert.Equal(ErrorCodes.WorkflowNotFound, nf.ErrorCode);
    }

    [Fact]
    public async Task Default_silinemez()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => Service().DeleteAsync("default", CancellationToken.None));
        Assert.Equal(ErrorCodes.WorkflowDefaultProtected, ex.ErrorCode);
        Assert.True(File.Exists(_fx.Paths.WorkflowFile("default")));
    }

    [Fact]
    public async Task Bozuk_json_acik_hata()
    {
        await File.WriteAllTextAsync(_fx.Paths.WorkflowFile("default"), "{ bozuk");
        var ex = await Assert.ThrowsAsync<DomainException>(() => new JsonWorkflowStore(_fx.Paths).LoadAsync("default", CancellationToken.None));
        Assert.Equal(ErrorCodes.ConfigFileInvalid, ex.ErrorCode);
    }

    [Fact]
    public async Task Bilinmeyen_kind_acik_hata()
    {
        var path = _fx.Paths.WorkflowFile("default");
        var text = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, text.Replace("\"kind\": \"implement\"", "\"kind\": \"deploy\"", StringComparison.Ordinal));
        var ex = await Assert.ThrowsAsync<DomainException>(() => new JsonWorkflowStore(_fx.Paths).LoadAsync("default", CancellationToken.None));
        Assert.Equal(ErrorCodes.WorkflowInvalidStage, ex.ErrorCode);
    }
}
