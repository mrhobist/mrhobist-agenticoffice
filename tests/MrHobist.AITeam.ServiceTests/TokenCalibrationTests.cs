using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Runs;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>
/// Karakter/token kalibrasyonu (docs/DOMAIN.md → Baglam butcesi). Ornek kirlidir: girdi tokeni ic turlarin toplami ve
/// CLI'nin istemde gorunmeyen sabit ek yuku icerir. Oturtma bunlari ayirmali; ayiramiyorsa varsayilana donmeli.
/// </summary>
public sealed class TokenCalibrationTests
{
    /// <summary>Gercek oran 3; ek yuk 4.5k token, her cagri 2 ic tur (yapisal cikti). Duz oran bunu ~1,4 sanir.</summary>
    [Fact]
    public void Sabit_ek_yuk_ve_ic_turlar_egimden_ayrilir()
    {
        const double truth = 3.0;
        const int overhead = 4_500;
        var samples = Enumerable.Range(1, 20)
            .Select(i => i * 2_000)
            .Select((chars, i) => new CalibrationSample(chars, 2 * (overhead + (int)(chars / truth)) + (i % 3 * 40), 2))
            .ToList();

        var fit = TokenCalibration.Fit(samples);

        Assert.Equal(20, fit.Samples);
        Assert.InRange(fit.Value, truth - 0.05, truth + 0.05);
        var naive = (double)samples.Sum(s => s.PromptChars) / samples.Sum(s => s.InputTokens);
        Assert.True(naive < 1.5, $"duz oran {naive:0.00}: kirli ornegi gostermeli"); // duz oran bu yuzden kullanilmiyor
    }

    [Fact]
    public void Az_ornekte_olcum_yok_sayilir()
    {
        var samples = Enumerable.Range(1, TokenCalibration.MinSamples - 1).Select(i => new CalibrationSample(i * 1_000, 5_000 + i * 300, 1)).ToList();

        Assert.Equal(CharsPerToken.Default, TokenCalibration.Fit(samples));
    }

    /// <summary>Hep ayni boyda istem: egim gurultudur, olcum sayilmaz.</summary>
    [Fact]
    public void Yayilim_yoksa_olcum_yok_sayilir()
    {
        var samples = Enumerable.Range(1, 20).Select(i => new CalibrationSample(10_000, 7_000 + i * 50, 1)).ToList();

        Assert.Equal(CharsPerToken.Default, TokenCalibration.Fit(samples));
    }

    /// <summary>Akil disi egim (token neredeyse sabit → ~50 karakter/token) kirpilmaz, reddedilir.</summary>
    [Fact]
    public void Aralik_disi_egim_kirpilmaz_reddedilir()
    {
        var samples = Enumerable.Range(1, 20).Select(i => new CalibrationSample(i * 5_000, 4_500 + i * 100, 1)).ToList();

        Assert.Equal(CharsPerToken.Default, TokenCalibration.Fit(samples));
    }
}
