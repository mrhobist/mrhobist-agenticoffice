using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>
/// Diger testler sabit <c>Fixtures/config/</c> ekibine bakar; bu sinif kullanicinin CANLI <c>config/</c>'unu
/// yalniz okur: ekip ne olursa olsun akislarin rolleri ve ajanlarin ek bilgileri cozulmeli, yoksa Api
/// ilk calismada patlar. Icerik (hangi ajan, kac akis) denetlenmez -- o kullanicinin karari.
/// </summary>
public sealed class LiveConfigTests
{
    private static StoragePaths LivePaths()
    {
        var discovered = StoragePaths.Discover(AppContext.BaseDirectory);
        return new StoragePaths(discovered.ConfigRoot, Path.Combine(Path.GetTempPath(), "aiteam-live-config", Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task Canli_config_akislari_ve_ek_bilgileri_cozulur()
    {
        var paths = LivePaths();
        var team = await new MarkdownAgentStore(paths).LoadTeamAsync(CancellationToken.None);
        var workflows = new JsonWorkflowStore(paths);

        var keys = await workflows.ListKeysAsync(CancellationToken.None);
        Assert.Contains("default", keys);
        foreach (var key in keys)
        {
            var wf = await workflows.LoadAsync(key, CancellationToken.None);
            Assert.All(wf.Stages, s => Assert.Contains(s.Role, team.Agents.Keys));
            if (wf.HandoffRole is not null)
                Assert.Contains(wf.HandoffRole, team.Agents.Keys);
        }

        Assert.All(team.Agents.Values, a => Assert.All(a.Includes, k => Assert.Contains(k, team.Knowledge.Keys)));
    }
}
