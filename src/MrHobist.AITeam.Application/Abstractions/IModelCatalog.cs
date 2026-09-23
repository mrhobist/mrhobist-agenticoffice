using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.Application.Abstractions;

/// <summary>
/// Listelenecek modeller: <c>config/models.json</c>, saglayici basina sirali. Tek dogru kaynak .NET'tir -- yeni model
/// cikinca dosyaya satir eklenir, kod degismez (CLAUDE.md §1: runtime is kurali bilmez, katalog da bir is kararidir).
/// </summary>
public interface IModelCatalog
{
    /// <summary>Dosya yoksa bos liste; bozuksa <c>config.file_invalid</c>.</summary>
    Task<IReadOnlyList<CatalogModel>> LoadAsync(CancellationToken ct);

    /// <summary>
    /// Model basina esdeger fiyat (<c>prices</c> anahtari). Kesilen turun maliyeti (runtime sonucu gelmedi, yalniz
    /// mesaj basina kullanim var) ve "kim ne harcadi" raporu bununla hesaplanir. Fiyati olmayan model icin tahmin
    /// yapilmaz: token yazilir, maliyet "olculemedi" kalir.
    /// </summary>
    Task<IReadOnlyDictionary<string, ModelPrice>> LoadPricesAsync(CancellationToken ct);
}

public sealed record CatalogModel(Provider Provider, string Model);

/// <summary>Milyon token basina esdeger $ (abonelikte ucret kesilmez; CLAUDE.md §4).</summary>
public sealed record ModelPrice(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite)
{
    /// <summary><paramref name="usage"/>'in girdisi TOPLAMDIR (dogrudan + okunan + yazilan); dogrudan pay farktan cikar.</summary>
    public decimal Estimate(RuntimeUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return Estimate(usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens);
    }

    public decimal Estimate(long inputTotal, long output, long cacheRead, long cacheWrite)
    {
        var direct = Math.Max(0, inputTotal - cacheRead - cacheWrite);
        return ((direct * Input) + (cacheRead * CacheRead) + (cacheWrite * CacheWrite) + (output * Output)) / 1_000_000m;
    }
}
