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
}

public sealed record CatalogModel(Provider Provider, string Model);
