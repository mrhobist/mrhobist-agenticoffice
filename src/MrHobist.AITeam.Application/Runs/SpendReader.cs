using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>Bir kaynagin (ofis ajani ya da etkilesimli oturumlar) bir klasor/model kirilimi.</summary>
public sealed record SpendLine(string Source, string Project, string Model, int Messages, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, decimal? CostUsd);

/// <summary>
/// Bir harcama kaynagi: toplam token, esdeger $ ve haftalik kotadaki tahmini payi (puan). <see cref="CostUsd"/> fiyati bilinen
/// modellerin toplamidir; fiyatsiz modeller <see cref="UnpricedModels"/>'da (o zaman $ ve pay alt sinirdir). Sona eklenir.
/// </summary>
public sealed record SpendSource(string Key, string Label, int Messages, long InputTokens, long OutputTokens, decimal? CostUsd, double? QuotaPoints, IReadOnlyList<SpendLine> Lines, IReadOnlyList<string>? UnpricedModels = null);

/// <summary>
/// <c>GET /usage/split</c>: kim ne harcadi. <see cref="OfficeRecordedUsd"/> ofisin kendi tur kaydidir (capraz denetim);
/// <see cref="Sources"/> makinedeki tum CLI kayitlarindan gelir. Notlar olcumun sinirini soyler.
/// </summary>
public sealed record SpendReport(
    DateTimeOffset Since,
    DateTimeOffset? Until,
    double? WeeklyPercent,
    DateTimeOffset? WeeklyResetsAt,
    decimal OfficeRecordedUsd,
    int OfficeRecordedTurns,
    IReadOnlyList<SpendSource> Sources,
    IReadOnlyList<string> Notes);

public interface ISpendReader
{
    /// <summary><paramref name="since"/> bossa haftalik pencerenin basi (sifirlanma − 7 gun), o da yoksa son 7 gun.</summary>
    Task<SpendReport> GetAsync(DateTimeOffset? since, DateTimeOffset? until, CancellationToken ct);
}

/// <summary>
/// Kim ne harcadi (kullanici istegi 2026-09-23: haftalik kota ortak, ofis ajani ile yoneten Claude Code oturumu ayni hesaptan
/// harciyor). Kota yuzdesi kaynaga gore ayrilmaz; ama makinedeki her CLI cagrisi kendi oturum kaydina kullanimiyla yazilir ve
/// giris noktasi (<c>entrypoint</c>) kaynagi soyler. Esdeger $ fiyat tablosundan (<see cref="IModelCatalog.LoadPricesAsync"/>);
/// kotadaki pay = haftalik yuzde × kaynagin $ payi. Varsayim: pencere bu makinedeki kullanimdan ibarettir (baska cihaz/web sayilmaz).
/// </summary>
public sealed class SpendReader(IAgentRuntimeService runtime, IRunStore runs, IModelCatalog catalog) : ISpendReader
{
    /// <summary>Agent SDK'nin (ofisin runtime'i) CLI giris noktasi.</summary>
    public const string OfficeSource = "sdk-py";

    public async Task<SpendReport> GetAsync(DateTimeOffset? since, DateTimeOffset? until, CancellationToken ct)
    {
        var notes = new List<string>();
        RuntimeUsageLimit? weekly = null;
        try
        {
            weekly = (await runtime.ListLimitsAsync(Provider.Anthropic, false, ct).ConfigureAwait(false))
                .SelectMany(p => p.Limits)
                .FirstOrDefault(l => l.Kind == "weekly_all");
        }
        catch (Exception ex) when (ex is RuntimeUnavailableException or RuntimeErrorException)
        {
            notes.Add("Kota okunamadı: haftalık paya çevrilmedi.");
        }

        var from = since ?? (weekly?.ResetsAt is { } r ? r.AddDays(-7) : DateTimeOffset.UtcNow.AddDays(-7));
        var prices = await catalog.LoadPricesAsync(ct).ConfigureAwait(false);

        var lines = (await runtime.ListLocalUsageAsync(from, until, ct).ConfigureAwait(false))
            .Select(u => new SpendLine(u.Source, u.Project, u.Model, u.Messages, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens,
                prices.TryGetValue(u.Model, out var p) ? Math.Round(p.Estimate(u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.CacheWrite5mTokens), 2) : null))
            .ToList();

        var sources = lines
            .GroupBy(l => l.Source == OfficeSource ? "office" : "sessions")
            .Select(g => new SpendSource(
                g.Key,
                g.Key == "office" ? "Ofis ajanı" : "Claude Code oturumları",
                g.Sum(l => l.Messages),
                g.Sum(l => l.InputTokens),
                g.Sum(l => l.OutputTokens),
                g.All(l => l.CostUsd is null) ? null : g.Sum(l => l.CostUsd ?? 0m),
                null,
                g.OrderByDescending(l => l.CostUsd ?? 0m).ThenByDescending(l => l.OutputTokens).ToList(),
                g.Where(l => l.CostUsd is null).Select(l => l.Model).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()))
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .ToList();

        var unpriced = lines.Where(l => l.CostUsd is null).GroupBy(l => l.Model, StringComparer.Ordinal)
            .Select(g => $"{g.Key} ({g.Sum(l => l.OutputTokens):N0} çıktı tk)").ToList();
        if (unpriced.Count > 0)
        {
            notes.Add($"Fiyatı olmayan model: {string.Join(", ", unpriced)} — $ ve kota payı bu modeller HARİÇ hesaplandı (config/models.json → prices).");
        }

        var total = sources.Sum(s => s.CostUsd ?? 0m);
        if (weekly is not null && total > 0 && since is null && until is null)
        {
            sources = sources.Select(s => s with { QuotaPoints = Math.Round(weekly.Percent * (double)((s.CostUsd ?? 0m) / total), 1) }).ToList();
            notes.Add("Kota payı tahmindir: haftalık yüzde, kaynakların eşdeğer $ oranına bölündü. Başka cihaz ya da claude.ai kullanımı sayılmaz; o pay oturumlara biner.");
        }
        else if (weekly is not null && (since is not null || until is not null))
        {
            notes.Add("Özel aralıkta kota payı verilmez: haftalık yüzde yalnız pencerenin tamamına aittir.");
        }

        var recorded = (await runs.ReadUsageAsync(1000, ct).ConfigureAwait(false))
            .Where(t => t.Ts >= from && (until is null || t.Ts < until))
            .ToList();

        return new SpendReport(from, until, weekly?.Percent, weekly?.ResetsAt, recorded.Sum(t => t.CostUsd ?? 0m), recorded.Count, sources, notes);
    }
}
