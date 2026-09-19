using MrHobist.AITeam.Application.Workflows;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Workflows;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

public sealed class WorkflowStoreTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Gercek_workflow_yuklenir()
    {
        var wf = await new JsonWorkflowStore(_fx.Paths).LoadAsync(CancellationToken.None);
        Assert.Equal(3, wf.MaxReviewRounds);
        Assert.Equal(StageKind.Analyze, wf.Stages[0].Kind);
        Assert.Contains(wf.Stages, s => s.Kind == StageKind.Handoff);
        Assert.Equal("analyst", wf.AnalyzeStage.Role);
    }

    [Fact]
    public async Task Yaz_oku_yorum_korunur()
    {
        var store = new JsonWorkflowStore(_fx.Paths);
        var wf = await store.LoadAsync(CancellationToken.None);
        await store.SaveAsync(wf with { MaxReviewRounds = 5 }, CancellationToken.None);
        var text = await File.ReadAllTextAsync(_fx.Paths.WorkflowFile);
        Assert.Contains("\"_comment\"", text, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"analyze\"", text, StringComparison.Ordinal);
        Assert.Equal(5, (await store.LoadAsync(CancellationToken.None)).MaxReviewRounds);
    }

    [Fact]
    public async Task Servis_bozuk_akisi_yazmaz()
    {
        var service = new WorkflowService(new JsonWorkflowStore(_fx.Paths));
        var model = await service.GetAsync(CancellationToken.None);
        var broken = model with { Stages = model.Stages.Where(s => s.Kind != StageKind.Implement).ToList() };
        var before = await File.ReadAllTextAsync(_fx.Paths.WorkflowFile);
        var ex = await Assert.ThrowsAsync<DomainException>(() => service.UpdateAsync(broken, CancellationToken.None));
        Assert.Equal(ErrorCodes.WorkflowNoImplement, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(_fx.Paths.WorkflowFile));
    }

    [Fact]
    public async Task Bozuk_json_acik_hata()
    {
        await File.WriteAllTextAsync(_fx.Paths.WorkflowFile, "{ bozuk");
        var ex = await Assert.ThrowsAsync<DomainException>(() => new JsonWorkflowStore(_fx.Paths).LoadAsync(CancellationToken.None));
        Assert.Equal(ErrorCodes.ConfigFileInvalid, ex.ErrorCode);
    }
}
