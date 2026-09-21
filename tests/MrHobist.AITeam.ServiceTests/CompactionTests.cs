using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Infrastructure.Compaction;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>
/// MAF Compaction bizim tasinan gecmise uygulanir (docs/PHASES.md → compact). Modelsiz stratejiler: kayan pencere
/// (mesaj sayisi) + kirpma (token). Yeni istem sikistirmaya girmez; bu sinif yalniz tasinan kismi sinar.
/// </summary>
public sealed class CompactionTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>10 red turu = 20 tasinan mesaj; butce 4. Eskiler duser, en yeni tur TAM kalir, roller almasik.</summary>
    [Fact]
    public async Task Butce_asilinca_en_eski_turlar_duser_en_yeni_tam_kalir()
    {
        var history = new List<RuntimeMessage>();
        for (var round = 1; round <= 10; round++)
        {
            history.Add(new RuntimeMessage("user", $"[t1 · gelistirme · tur {round}] Bu adımda verdiğin çıktı:"));
            history.Add(new RuntimeMessage("assistant", $"{{\"summary\":\"tur {round} çıktısı\",\"filesChanged\":[\"a{round}.cs\"]}}"));
        }

        var kept = await new MafHistoryCompactor().CompactAsync(history, new CompactionBudget(MaxMessages: 4, MaxTokens: 12_000), Ct);

        Assert.True(kept.Count <= 4, $"butce 4, kalan {kept.Count}");
        Assert.Contains(kept, m => m.Content.Contains("tur 10 çıktısı", StringComparison.Ordinal)); // en yeni tur korunur
        Assert.DoesNotContain(kept, m => m.Content.Contains("tur 1 çıktısı", StringComparison.Ordinal)); // en eski duser
        Assert.Equal("assistant", kept[^1].Role); // tasinan kisim asistan ciktisiyla biter; yeni istem sonra eklenir
    }

    [Fact]
    public async Task Butce_icindeyse_gecmis_oldugu_gibi_kalir()
    {
        var history = new List<RuntimeMessage>
        {
            new("user", "[t1 · gelistirme · tur 1] Bu adımda verdiğin çıktı:"),
            new("assistant", "{\"summary\":\"ilk\"}"),
        };

        var kept = await new MafHistoryCompactor().CompactAsync(history, CompactionBudget.TaskHistory, Ct);

        Assert.Same(history, kept); // dokunulmadi: ayni ornek
    }

    /// <summary>Token butcesi: tek bir dev cikti bile tavani asarsa kirpilir; icerik tamamen kaybolmaz.</summary>
    [Fact]
    public async Task Token_butcesi_asilinca_uzun_cikti_kirpilir()
    {
        var huge = new string('x', 200_000); // ~50k token
        var history = new List<RuntimeMessage>
        {
            new("user", "[t1 · gelistirme · tur 1] Bu adımda verdiğin çıktı:"),
            new("assistant", huge),
            new("user", "[t1 · test · tur 1] Bu adımda verdiğin çıktı:"),
            new("assistant", "{\"verdict\":\"reject\",\"feedback\":\"a.cs eksik\"}"),
        };

        var kept = await new MafHistoryCompactor().CompactAsync(history, new CompactionBudget(MaxMessages: 8, MaxTokens: 2_000), Ct);

        var total = kept.Sum(m => m.Content.Length);
        Assert.True(total < huge.Length, $"kirpilmadi: {total} karakter");
        Assert.Contains(kept, m => m.Content.Contains("a.cs eksik", StringComparison.Ordinal)); // en yeni geri bildirim korunur
    }
}
