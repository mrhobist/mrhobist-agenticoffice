using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.Domain.Settings;

/// <summary>
/// Calisma alani ayarlari: <c>config/settings.json</c>. Bugun tek alan: saglayici basina <b>limit korumasi</b>
/// (kullanici karari 2026-09-19): saglayicinin aktif kota penceresi (5 saat, hafta) bu yuzdeye ulastiginda yeni LLM turu
/// baslamaz, calisma <c>Paused</c> + <c>ResumeAt</c> ile bekler, pencere sifirlaninca surer. Varsayilan %99.
/// </summary>
public sealed record AppSettings(IReadOnlyDictionary<Provider, int> LimitGuards)
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
    }
}
