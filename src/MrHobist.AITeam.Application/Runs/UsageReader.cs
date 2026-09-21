using MrHobist.AITeam.Application.Abstractions;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>Bir saglayici/model ciftinin bu makinedeki toplam kullanimi (kayitli turlardan).</summary>
public sealed record UsageItem(
    string Provider,
    string Model,
    int Turns,
    int Runs,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd,
    DateTimeOffset? LastAt);

/// <summary>
/// <c>GET /usage</c>: son N calismanin turlarini saglayici+model bazinda toplar (tek sorgu, yalniz toplama sutunlari).
/// Kaynak yalniz bizim kayitlarimizdir; saglayicinin abonelik limitini/kalan kotasini vermez (CLI bunu sunmuyor).
/// </summary>
public interface IUsageReader
{
    Task<IReadOnlyList<UsageItem>> SummarizeAsync(int runLimit, CancellationToken ct);
}

public sealed class UsageReader(IRunStore runs) : IUsageReader
{
    private sealed class Acc
    {
        public int Turns;
        public HashSet<string> Runs = new(StringComparer.Ordinal);
        public long In;
        public long Out;
        public decimal Cost;
        public DateTimeOffset? Last;
    }

    public async Task<IReadOnlyList<UsageItem>> SummarizeAsync(int runLimit, CancellationToken ct)
    {
        var acc = new Dictionary<(string Provider, string Model), Acc>();
        // Para SQL'de toplanmaz (SQLite'ta ondalik metin): satirlar sutun bazinda gelir, toplama burada.
        foreach (var t in await runs.ReadUsageAsync(Math.Clamp(runLimit, 1, 1000), ct).ConfigureAwait(false))
        {
            var key = (t.Provider, t.Model);
            if (!acc.TryGetValue(key, out var a))
            {
                acc[key] = a = new Acc();
            }

            a.Turns++;
            a.Runs.Add(t.RunId);
            a.In += t.InputTokens ?? 0;
            a.Out += t.OutputTokens ?? 0;
            a.Cost += t.CostUsd ?? 0m;
            if (a.Last is null || t.Ts > a.Last)
            {
                a.Last = t.Ts;
            }
        }

        return acc
            .Select(kv => new UsageItem(kv.Key.Provider, kv.Key.Model, kv.Value.Turns, kv.Value.Runs.Count, kv.Value.In, kv.Value.Out, kv.Value.Cost, kv.Value.Last))
            .OrderByDescending(u => u.CostUsd)
            .ThenByDescending(u => u.Turns)
            .ToList();
    }
}
