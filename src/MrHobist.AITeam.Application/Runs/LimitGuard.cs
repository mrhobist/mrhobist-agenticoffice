using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>
/// Bekleme bitisinin kullaniciya yazilisi. Saat TEK BASINA yaniltir: haftalik kota gunler sonrasina sifirlanir ve
/// "07:00'de sürer" bugunu isaret ediyormus gibi okunur (2026-09-22'de birebir yasandi: gercek sifirlanma 3 gun
/// sonraydi). Bugun degilse tarih de yazilir.
/// </summary>
public static class ResumeText
{
    public static string For(DateTimeOffset? resumeAt)
    {
        if (resumeAt is not { } at)
        {
            return "sıfırlanma zamanı bilinmiyor";
        }

        var local = at.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? $"{local:HH:mm}'de sürer"
            : $"{local:d MMMM HH:mm}'de sürer";
    }
}

/// <summary>Saglayicinin kota penceresi esige ulasti: yeni LLM turu baslamaz. <see cref="ResumeAt"/> pencerenin sifirlanma zamani.</summary>
public sealed class LimitReachedException(Provider provider, double percent, int threshold, DateTimeOffset? resumeAt, Exception? inner = null)
    : Exception($"{Providers.Wire(provider)} kullanımı %{percent:0} ≥ eşik %{threshold}; {ResumeText.For(resumeAt)}", inner)
{
    public Provider Provider { get; } = provider;

    public DateTimeOffset? ResumeAt { get; } = resumeAt;
}

/// <summary>
/// Limit korumasi (kullanici karari 2026-09-19, docs/DOMAIN.md → Butce ve limit): her LLM cagrisindan ONCE saglayicinin
/// aktif kota pencerelerine bakar; herhangi biri ayarlardaki esige (varsayilan %99) ulastiysa <see cref="LimitReachedException"/>.
/// Runtime yuzdeleri 90 s onbellekler; kota ucu vermiyorsa (available=false) koruma sessizce gecer — akisi kilitlemek yerine
/// varsayimla ilerlenir; sonuc ust barda zaten "kalan kullanim yok" olarak gorunur.
///
/// <b>Hangi pencere sayilir (2026-09-23 duzeltmesi):</b> cagrinin modeline UYGULANAN her pencere -- kapsamsiz olanlar
/// (oturum, haftalik tum modeller) her zaman, modele ozel olanlar (<c>scope: "Fable"</c>) yalniz o model ailesinde.
/// Once yalniz saglayicinin <c>isActive</c> isaretine bakiliyordu: Fable'a ozel pencere %99'da "aktif"ken Opus cagrisi
/// da duruyor, haftalik tum-modeller penceresi (%85) ise "aktif" olmadigi icin hic sayilmiyordu -- esik %90'a cekilse
/// bile bekletmezdi. Model bilinmiyorsa (md'de bos: saglayici varsayilani) modele ozel pencereler de sayilir: temkinli taraf.
/// </summary>
public sealed class LimitGuard(IAgentRuntimeService runtime, ISettingsStore settings)
{
    public async Task CheckAsync(Provider provider, string? model, CancellationToken ct)
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
            var hit = l.Limits.Where(x => AppliesTo(x, model) && x.Percent >= threshold).OrderByDescending(x => x.Percent).FirstOrDefault();
            if (hit is not null)
            {
                throw new LimitReachedException(provider, hit.Percent, threshold, hit.ResetsAt);
            }
        }
    }

    /// <summary>Pencere bu modelin cagrisini sinirliyor mu: kapsamsizsa evet; kapsamliysa model adi kapsami iceriyorsa (claude-fable-5-1 ~ Fable).</summary>
    public static bool AppliesTo(RuntimeUsageLimit limit, string? model)
    {
        ArgumentNullException.ThrowIfNull(limit);
        return string.IsNullOrWhiteSpace(limit.Scope)
            || string.IsNullOrWhiteSpace(model)
            || model.Contains(limit.Scope.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
