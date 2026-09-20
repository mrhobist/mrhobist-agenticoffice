using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Agents;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>Bilgi dosyasi olustur/duzenle/yukle/sil ve hazir ajan md'sini ice aktarma (docs/DOMAIN.md → Ekip yonetimi).</summary>
public sealed class KnowledgeTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static readonly CancellationToken Ct = CancellationToken.None;

    private AgentService Service() => new(new MarkdownAgentStore(_fx.Paths), new JsonWorkflowStore(_fx.Paths), new JsonSceneLayoutStore(_fx.Paths), new NoScene());

    private sealed class NoScene : ISceneEventPublisher
    {
        public void Publish(string type, string json) { }
    }

    [Fact]
    public async Task Bilgi_dosyasi_olusur_yuklenir_kullanimdayken_silinmez()
    {
        var svc = Service();
        var before = (await svc.ListKnowledgeAsync(Ct)).Count;

        var created = await svc.UpsertKnowledgeAsync("guvenlik-kurallari", new KnowledgeModel("Güvenlik Kuralları", "- Anahtar koda gömülmez."), Ct);
        Assert.Equal(("guvenlik-kurallari", "Güvenlik Kuralları"), (created.Key, created.Title));
        Assert.True(File.Exists(Path.Combine(_fx.Paths.KnowledgeDir, "guvenlik-kurallari.md")));

        // md yukleme: frontmatter yoksa ilk "# Baslik" satiri baslik olur.
        var imported = await svc.ImportKnowledgeAsync(new ImportMarkdownRequest("api-sozlesme", "# API Sözleşmesi\n\nEnum adiyla tasinir.\n"), Ct);
        Assert.Equal("API Sözleşmesi", imported.Title);
        Assert.Equal(before + 2, (await svc.ListKnowledgeAsync(Ct)).Count);

        var empty = await Assert.ThrowsAsync<DomainException>(() => svc.UpsertKnowledgeAsync("bos", new KnowledgeModel("Boş", "  "), Ct));
        Assert.Equal(ErrorCodes.KnowledgeBodyEmpty, empty.ErrorCode);

        // Developer 'kodlama-standartlari' kullanir: silinemez; kullanilmayan silinir.
        var inUse = await Assert.ThrowsAsync<DomainException>(() => svc.DeleteKnowledgeAsync("kodlama-standartlari", Ct));
        Assert.Equal(ErrorCodes.KnowledgeInUse, inUse.ErrorCode);
        await svc.DeleteKnowledgeAsync("api-sozlesme", Ct);
        Assert.DoesNotContain(await svc.ListKnowledgeAsync(Ct), k => k.Key == "api-sozlesme");
    }

    [Fact]
    public async Task Hazir_ajan_mdsi_ice_aktarilir_ve_sahneye_yerlesir()
    {
        var svc = Service();
        const string md = """
            ---
            name: Veri Mühendisi
            summary: Veri boru hatlarını kurar.
            office_roles: [dev, res]
            model: claude-sonnet-5
            effort: medium
            includes: [kodlama-standartlari]
            can_ask: manager
            ---

            Sen bir yazılım üretim ofisinin VERİ MÜHENDİSİ'sin.
            """;
        var detail = await svc.ImportAsync(new ImportMarkdownRequest("veri-muhendisi", md), Ct);
        Assert.Equal(("Veri Mühendisi", "claude-sonnet-5", "manager"), (detail.Name, detail.Model, detail.CanAsk));
        Assert.Equal(["dev", "res"], detail.OfficeRoles);
        Assert.Contains("VERİ MÜHENDİSİ", detail.Prompt, StringComparison.Ordinal);

        var scene = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_fx.Paths.ConfigFile("scene.json")));
        Assert.Contains(scene.RootElement.GetProperty("agents").EnumerateArray(), a => a.GetProperty("key").GetString() == "veri-muhendisi");

        var dup = await Assert.ThrowsAsync<DomainException>(() => svc.ImportAsync(new ImportMarkdownRequest("veri-muhendisi", md), Ct));
        Assert.Equal(ErrorCodes.AgentExists, dup.ErrorCode);
        var badInclude = await Assert.ThrowsAsync<DomainException>(() => svc.ImportAsync(new ImportMarkdownRequest("yanlis", "---\nname: X\nincludes: [yok-boyle]\n---\ngovde"), Ct));
        Assert.Equal(ErrorCodes.AgentUnknownInclude, badInclude.ErrorCode);
    }
}
