using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Agents;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>Model listesi .NET'in <c>config/models.json</c>'undan gelir; runtime yalniz erisilebilirlik ve ek kesif verir.</summary>
public sealed class ModelCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aiteam-models", Guid.NewGuid().ToString("N"));

    public ModelCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private JsonModelCatalog Catalog(string? json)
    {
        if (json is not null)
        {
            File.WriteAllText(Path.Combine(_root, JsonModelCatalog.FileName), json);
        }

        return new JsonModelCatalog(new StoragePaths(_root, Path.Combine(_root, "data")));
    }

    [Fact]
    public async Task Katalog_sirayla_okunur_yorum_ve_bos_satir_atlanir()
    {
        var models = await Catalog("""{ "_comment": "x", "anthropic": ["claude-opus-5-5", " ", "claude-sonnet-5"], "openai": ["gpt-5"] }""")
            .LoadAsync(CancellationToken.None);

        Assert.Equal(
            [new CatalogModel(Provider.Anthropic, "claude-opus-5-5"), new CatalogModel(Provider.Anthropic, "claude-sonnet-5"), new CatalogModel(Provider.Openai, "gpt-5")],
            models);
    }

    [Fact]
    public async Task Dosya_yoksa_bos_liste()
        => Assert.Empty(await Catalog(null).LoadAsync(CancellationToken.None));

    [Theory]
    [InlineData("""{ "claude": ["x"] }""")]
    [InlineData("""{ "anthropic": "x" }""")]
    [InlineData("""[ "x" ]""")]
    [InlineData("""{ "anthropic": [""")]
    public async Task Bozuk_katalog_config_file_invalid(string json)
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => Catalog(json).LoadAsync(CancellationToken.None));
        Assert.Equal(ErrorCodes.ConfigFileInvalid, ex.ErrorCode);
    }

    [Fact]
    public void Birlestirme_katalog_sirasini_korur_runtime_erisimini_tasir_ek_kesfi_sona_koyar()
    {
        CatalogModel[] listed = [new(Provider.Anthropic, "claude-opus-5-5"), new(Provider.Anthropic, "claude-sonnet-5")];
        RuntimeModelInfo[] discovered =
        [
            new(Provider.Anthropic, "claude-sonnet-5", true, "giriş var"),
            new(Provider.Anthropic, "claude-haiku-4-5-20251001", true, "giriş var"),
            new(Provider.Openai, "gpt-5", false, "giriş yok"),
        ];

        var merged = ModelListService.Merge(listed, discovered);

        Assert.Equal(["claude-opus-5-5", "claude-sonnet-5", "claude-haiku-4-5-20251001", "gpt-5"], merged.Select(m => m.Model));
        // Runtime listesinde olmayan yeni model, saglayicinin giris durumunu tasir.
        Assert.Equal(new RuntimeModelInfo(Provider.Anthropic, "claude-opus-5-5", true, "giriş var"), merged[0]);
        Assert.False(merged[3].Reachable);
    }

    [Fact]
    public void Runtime_saglayiciyi_hic_bilmiyorsa_erisilemez()
    {
        var merged = ModelListService.Merge([new CatalogModel(Provider.Nvidia, "nv-x")], []);
        Assert.Equal(new RuntimeModelInfo(Provider.Nvidia, "nv-x", false, ""), Assert.Single(merged));
    }
}
