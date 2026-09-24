using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.Domain.Settings;

/// <summary>
/// Calisma alani ayarlari (<c>app_settings</c>). Saglayici basina <b>limit korumasi</b> (kullanici karari 2026-09-19):
/// saglayicinin aktif kota penceresi (5 saat, hafta) bu yuzdeye ulastiginda yeni LLM turu baslamaz, calisma <c>Paused</c> +
/// <c>ResumeAt</c> ile bekler, pencere sifirlaninca surer. Varsayilan %99.
/// <see cref="CacheTtl"/> (2026-09-24): Anthropic istem onbelleginin omru; null = CLI varsayilani (1 sa). Sona eklendi.
/// </summary>
public sealed record AppSettings(IReadOnlyDictionary<Provider, int> LimitGuards, string? CacheTtl = null)
{
    public const int DefaultGuardPercent = 99;

    public static AppSettings Default => new(new Dictionary<Provider, int> { [Provider.Anthropic] = DefaultGuardPercent });

    public int GuardFor(Provider provider) => LimitGuards.TryGetValue(provider, out var p) ? p : DefaultGuardPercent;

    public void Validate()
    {
        foreach (var (provider, percent) in LimitGuards)
        {
            if (!Enum.IsDefined(provider))
            {
                throw new DomainException(ErrorCodes.SettingsInvalid, $"bilinmeyen saglayici: {provider}");
            }

            if (percent is < 1 or > 100)
            {
                throw new DomainException(ErrorCodes.SettingsInvalid, $"{provider}: limit esigi 1–100 arasinda olmali ({percent}).");
            }
        }

        if (CacheTtl is not null && !CacheTtls.All.Contains(CacheTtl, StringComparer.Ordinal))
        {
            throw new DomainException(ErrorCodes.SettingsInvalid, $"bilinmeyen onbellek omru '{CacheTtl}' (5m | 1h | bos).");
        }
    }
}

/// <summary>
/// Istem onbellegi omru; tel adi runtime'in bekledigi deger. Olcum 2026-09-24 (4 is, $35): CLI tum yazmalari 1 saatlik yapiyor,
/// yazma maliyetin %40'i; 5 dakikalik yazma 2x yerine 1,25x oldugu icin hesapta ~%15 eder. Bedeli: 5 dk'yi asan bir arac
/// cagrisindan (npm install, uzun test) sonra baglam yeniden yazilir. Varsayilan degismedi; olcum `scripts/context-report.py`.
/// </summary>
public static class CacheTtls
{
    public const string FiveMinutes = "5m";

    public const string OneHour = "1h";

    public static readonly IReadOnlyList<string> All = [FiveMinutes, OneHour];
}
