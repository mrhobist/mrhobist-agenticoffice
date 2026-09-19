using MrHobist.AITeam.Application.Agents;
using MrHobist.AITeam.Application.Common;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

public sealed class AgentStoreTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Gercek_config_yuklenir_ve_alti_rol_vardir()
    {
        var team = await new MarkdownAgentStore(_fx.Paths).LoadTeamAsync(CancellationToken.None);
        Assert.All(Team.KnownRoles, r => Assert.Contains(r, team.Agents.Keys));
        Assert.Equal("Analist", team.Agents["analyst"].Name);
        Assert.Equal(["pm", "arch", "res"], team.Agents["analyst"].OfficeRoles);
        Assert.Equal("manager", team.Agents["developer"].CanAsk);
        Assert.Contains("mimari-kurallar", team.Agents["developer"].Includes);
        Assert.NotEmpty(team.Knowledge);
    }

    [Fact]
    public async Task Yaz_oku_kayipsiz()
    {
        var store = new MarkdownAgentStore(_fx.Paths);
        var team = await store.LoadTeamAsync(CancellationToken.None);
        var dev = team.Agents["developer"] with
        {
            Summary = "Iki nokta: var, virgul, \"tirnak\" ve # kare",
            Provider = Provider.Nvidia,
            Model = "z-ai/glm-5.3",
            Prompt = "Sen DEVELOPER'sin.\n\nSatir 2: kod yaz.",
        };
        await store.SaveAgentAsync(dev, CancellationToken.None);

        var again = (await store.LoadTeamAsync(CancellationToken.None)).Agents["developer"];
        Assert.Equal(dev.Summary, again.Summary);
        Assert.Equal(Provider.Nvidia, again.Provider);
        Assert.Equal("z-ai/glm-5.3", again.Model);
        Assert.Equal(dev.Prompt, again.Prompt);
        Assert.Equal(dev.Includes, again.Includes);
    }

    [Fact]
    public async Task Claude_saglayici_adi_sessizce_cevrilmez()
    {
        var path = Path.Combine(_fx.Paths.AgentsDir, "developer.md");
        var text = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, text.Replace("---\nname:", "---\nprovider: claude\nname:", StringComparison.Ordinal));
        var ex = await Assert.ThrowsAsync<DomainException>(() => new MarkdownAgentStore(_fx.Paths).LoadTeamAsync(CancellationToken.None));
        Assert.Equal(ErrorCodes.AgentInvalidProvider, ex.ErrorCode);
    }

    [Fact]
    public async Task Servis_eksik_alt_md_ile_kaydetmez()
    {
        var service = new AgentService(new MarkdownAgentStore(_fx.Paths));
        var before = await File.ReadAllTextAsync(Path.Combine(_fx.Paths.AgentsDir, "tester.md"));
        var req = new UpdateAgentRequest("Testçi", "", ["qa"], null, null, ["yok-boyle"], null, "Sen testçisin.");
        var ex = await Assert.ThrowsAsync<DomainException>(() => service.UpdateAsync("tester", req, CancellationToken.None));
        Assert.Equal(ErrorCodes.AgentUnknownInclude, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(_fx.Paths.AgentsDir, "tester.md")));
    }

    [Fact]
    public async Task Servis_bilinmeyen_ajan_404()
    {
        var service = new AgentService(new MarkdownAgentStore(_fx.Paths));
        var ex = await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync("ceo", CancellationToken.None));
        Assert.Equal(ErrorCodes.AgentNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task Bilesik_prompt_alt_mdleri_icerir()
    {
        var service = new AgentService(new MarkdownAgentStore(_fx.Paths));
        var detail = await service.GetAsync("developer", CancellationToken.None);
        Assert.StartsWith(detail.Prompt, detail.ComposedPrompt, StringComparison.Ordinal);
        Assert.Contains("# Mimari Kurallar", detail.ComposedPrompt, StringComparison.Ordinal);
        Assert.Contains("# Kodlama Standartları", detail.ComposedPrompt, StringComparison.Ordinal);
    }
}
