using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>Saglayicinin kota penceresi esige ulasti: yeni LLM turu baslamaz. <see cref="ResumeAt"/> pencerenin sifirlanma zamani.</summary>
public sealed class LimitReachedException(Provider provider, double percent, int threshold, DateTimeOffset? resumeAt)
    : Exception($"{Providers.Wire(provider)} kullanımı %{percent:0} ≥ eşik %{threshold}; {(resumeAt is null ? "sıfırlanma zamanı bilinmiyor" : $"{resumeAt.Value.ToLocalTime():HH:mm}'de sürer")}")
{
    public Provider Provider { get; } = provider;

    public DateTimeOffset? ResumeAt { get; } = resumeAt;
}

/// <summary>
/// Limit korumasi (kullanici karari 2026-09-19, docs/DOMAIN.md → Butce ve limit): her LLM cagrisindan ONCE saglayicinin
/// aktif kota pencerelerine bakar; herhangi biri ayarlardaki esige (varsayilan %99) ulastiysa <see cref="LimitReachedException"/>.
/// Runtime yuzdeleri 90 s onbellekler; kota ucu vermiyorsa (available=false) koruma sessizce gecer — akisi kilitlemek yerine
/// varsayimla ilerlenir; sonuc ust barda zaten "kalan kullanim yok" olarak gorunur.
/// </summary>
public sealed class LimitGuard(IAgentRuntimeService runtime, ISettingsStore settings)
{
    public async Task CheckAsync(Provider provider, CancellationToken ct)
    {
        var threshold = (await settings.LoadAsync(ct).ConfigureAwait(false)).GuardFor(provider);
        IReadOnlyList<RuntimeProviderLimits> limits;
        try
        {
            limits = await runtime.ListLimitsAsync(provider, false, ct).ConfigureAwait(false);
        }
        catch (RuntimeErrorException)
        {
            return; // kota ucu bozuk: koruma degil, gorunurluk sorunu (ust bar soyler)
        }

        foreach (var l in limits.Where(l => l.Provider == provider && l.Available))
        {
            var hit = l.Limits.Where(x => x.IsActive && x.Percent >= threshold).OrderByDescending(x => x.Percent).FirstOrDefault();
            if (hit is not null)
            {
                throw new LimitReachedException(provider, hit.Percent, threshold, hit.ResetsAt);
            }
        }
    }
}
