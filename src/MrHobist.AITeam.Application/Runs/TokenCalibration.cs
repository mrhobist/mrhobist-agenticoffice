using MrHobist.AITeam.Application.Abstractions;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>Olculen karakter/token orani. <see cref="Samples"/> 0: olcum yok, <see cref="TokenCalibration.Fallback"/> kullanildi.</summary>
public sealed record CharsPerToken(double Value, int Samples)
{
    public static readonly CharsPerToken Default = new(TokenCalibration.Fallback, 0);
}

/// <summary>
/// Karakter/token oranini kayitli turlardan cikarir (docs/DOMAIN.md → Baglam butcesi). Sabit "4 karakter = 1 token"
/// Turkce metinde ve kodda olculmemis bir varsayimdir; tur kaydinda hem istemin karakteri hem saglayicinin saydigi
/// girdi tokeni zaten var.
///
/// Duz oran (karakter / token) KULLANILMAZ, cunku ornek kirlidir: runtime'in girdi tokeni ic turlarin TOPLAMIDIR
/// (yapisal cikti en az 2 tur) ve CLI istemde gorunmeyen sabit bir ek yuk koyar (~4.5k token, araCsiz turda bile).
/// 10k karakterlik bir istemde duz oran ~1,4 cikar ve tahmin ~3 kat sisar. Bunun yerine tur basina girdi
/// (<c>InputTokens / Turns</c>) istem karakterine karsi dogrusal oturtulur: sabit terim ek yuku, EGIM karakter
/// basina tokeni verir. Tasinan gecmisin olcusu icin gereken yalniz egimdir.
///
/// Olcum yetersizse (az ornek, yayilim yok, egim akil disi) <see cref="Fallback"/> doner: olcum gelene kadar
/// davranis bugunkuyle aynidir. Aralik disi egim KIRPILMAZ, reddedilir -- kirpmak bozuk bir oturtmayi olculmus gibi gosterir.
/// </summary>
public static class TokenCalibration
{
    /// <summary>Olcum yokken: bugunku (ve MAF'in) varsayimi. Degistirmek davranisi olcumsuz degistirir.</summary>
    public const double Fallback = 4.0;

    public const int MinSamples = 8;

    /// <summary>Oturtmaya giren en yeni ornek sayisi: model/istem yapisi degisince eski turlar agir basmasin.</summary>
    public const int Window = 60;

    public const double MinCharsPerToken = 1.5;

    public const double MaxCharsPerToken = 6.0;

    /// <summary>Istem boylari birbirine cok yakinsa egim gurultudur: degisim katsayisi bunun altindaysa olcum yok sayilir.</summary>
    private const double MinSpread = 0.1;

    public static CharsPerToken Fit(IReadOnlyList<CalibrationSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var pts = samples
            .Where(s => s.PromptChars > 0 && s.InputTokens > 0 && s.Turns > 0)
            .Select(s => (X: (double)s.PromptChars, Y: (double)s.InputTokens / s.Turns))
            .ToList();
        if (pts.Count < MinSamples)
        {
            return CharsPerToken.Default;
        }

        var mx = pts.Average(p => p.X);
        var my = pts.Average(p => p.Y);
        var sxx = pts.Sum(p => (p.X - mx) * (p.X - mx));
        var sxy = pts.Sum(p => (p.X - mx) * (p.Y - my));
        if (sxx <= 0 || Math.Sqrt(sxx / pts.Count) / mx < MinSpread)
        {
            return CharsPerToken.Default;
        }

        var tokensPerChar = sxy / sxx;
        if (tokensPerChar <= 0)
        {
            return CharsPerToken.Default;
        }

        var cpt = 1 / tokensPerChar;
        return cpt is < MinCharsPerToken or > MaxCharsPerToken ? CharsPerToken.Default : new CharsPerToken(Math.Round(cpt, 3), pts.Count);
    }
}
