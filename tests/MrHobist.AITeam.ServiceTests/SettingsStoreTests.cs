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
    public async Task Gecersiz_esik_kaydedilmez()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => _fx.Settings.SaveAsync(new AppSettings(new Dictionary<Provider, int> { [Provider.Anthropic] = 0 }), Ct));
        Assert.Equal(ErrorCodes.SettingsInvalid, ex.ErrorCode);
    }
}
