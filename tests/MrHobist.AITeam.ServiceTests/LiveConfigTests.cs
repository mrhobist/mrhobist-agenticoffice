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
        // Model katalogu okunabilir olmali (bozuksa /models ve /providers patlar).
        await new JsonModelCatalog(paths).LoadAsync(CancellationToken.None);
    }

    /// <summary>
    /// Hazir MCP katalogu (config/mcp-catalog.json) yuklenir ve desteklenen HER secenek sahte degerlerle gecerli bir sunucuya
    /// donusur: sablon yanlis alana basvurursa ya da zorunlu alan eksik tanimliysa kullanici "Kur"a bastiginda degil burada patlar.
    /// </summary>
    [Fact]
    public async Task Canli_mcp_katalogunun_her_secenegi_kurulabilir()
    {
        var catalog = await new JsonMcpCatalog(LivePaths()).LoadAsync(CancellationToken.None);
        Assert.NotEmpty(catalog);
        foreach (var entry in catalog)
        {
            foreach (var option in entry.Options.Where(o => o.Supported))
            {
                var values = (option.Fields ?? []).ToDictionary(f => f.Name, f => (string?)(f.Choices is { Count: > 0 } c ? c[0] : (f.Name.Contains("URL", StringComparison.Ordinal) || f.Name.Contains("HOST", StringComparison.Ordinal) ? "https://ornek.local" : "deger")));
                var server = Domain.Mcp.McpCatalogBuilder.Build(entry, option, entry.Key, null, values);
                Assert.Equal(option.Transport, server.Transport);
                Assert.DoesNotContain(server.Args, a => a.Contains('{', StringComparison.Ordinal));
            }
        }
    }
}
