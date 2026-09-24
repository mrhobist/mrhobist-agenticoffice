using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Settings;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>Ayarlar deposu (app_settings, tek satir): varsayilan, kaydet/oku gidis-donusu, damgaya bagli onbellek, dogrulama.</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Ayarlar_kaydedilir_okunur_ve_onbellek_damgayla_calisir()
    {
        var store = _fx.Settings;

        // Satir yok -> varsayilan.
        Assert.Equal(AppSettings.DefaultGuardPercent, (await store.LoadAsync(Ct)).GuardFor(Provider.Anthropic));

        await store.SaveAsync(new AppSettings(new Dictionary<Provider, int> { [Provider.Anthropic] = 80, [Provider.Openai] = 50 }), Ct);
        var first = await store.LoadAsync(Ct);
        Assert.Equal((80, 50), (first.GuardFor(Provider.Anthropic), first.GuardFor(Provider.Openai)));

        // updated_at damgasi degismedi -> onbellek ayni ornegi verir (metin donusumu tick hassasiyetini korur).
        Assert.Same(first, await store.LoadAsync(Ct));

        // Ikinci kayit upsert: satir sayisi 1 kalir, yeni deger okunur.
        await store.SaveAsync(new AppSettings(new Dictionary<Provider, int> { [Provider.Anthropic] = 70 }), Ct);
        var second = await store.LoadAsync(Ct);
        Assert.Equal(70, second.GuardFor(Provider.Anthropic));
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Onbellek_omru_varsayilan_bos_kaydedilir_ve_gecersizi_reddedilir()
    {
        var store = _fx.Settings;
        Assert.Null((await store.LoadAsync(Ct)).CacheTtl); // varsayilan: CLI'nin kendi omru

        await store.SaveAsync(new AppSettings(AppSettings.Default.LimitGuards, CacheTtls.FiveMinutes), Ct);
        Assert.Equal("5m", (await store.LoadAsync(Ct)).CacheTtl);

        var ex = await Assert.ThrowsAsync<DomainException>(() => store.SaveAsync(new AppSettings(AppSettings.Default.LimitGuards, "2h"), Ct));
        Assert.Equal(ErrorCodes.SettingsInvalid, ex.ErrorCode);
        Assert.Equal("5m", (await store.LoadAsync(Ct)).CacheTtl); // gecersiz kayit oncekini bozmaz
    }

    [Fact]
    public async Task Gecersiz_esik_kaydedilmez()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => _fx.Settings.SaveAsync(new AppSettings(new Dictionary<Provider, int> { [Provider.Anthropic] = 0 }), Ct));
        Assert.Equal(ErrorCodes.SettingsInvalid, ex.ErrorCode);
    }
}
