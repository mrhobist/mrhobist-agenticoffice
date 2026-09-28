using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.Application.Agents;

/// <summary><c>GET /models</c> ve <c>GET /providers</c>'in model listesi.</summary>
public interface IModelListService
{
    Task<IReadOnlyList<RuntimeModelInfo>> ListAsync(Provider? provider, CancellationToken ct);
}

/// <summary>
/// Katalog (<see cref="IModelCatalog"/>) + runtime'in kesfi. Siralama katalogdandir; runtime'in bilip katalogda olmayan
/// modelleri (ör. <c>OPENAI_MODELS</c>) sona eklenir. Erisilebilirlik runtime'dandir: katalogdaki model runtime listesinde
/// yoksa ayni saglayicinin durumunu tasir (giris saglayici duzeyindedir, model duzeyinde degil).
/// </summary>
public sealed class ModelListService(IModelCatalog catalog, IAgentRuntimeService runtime) : IModelListService
{
    public async Task<IReadOnlyList<RuntimeModelInfo>> ListAsync(Provider? provider, CancellationToken ct)
    {
        var discovered = await runtime.ListModelsAsync(provider, ct).ConfigureAwait(false);
        var listed = (await catalog.LoadAsync(ct).ConfigureAwait(false)).Where(m => provider is null || m.Provider == provider);
        return Merge(listed, discovered);
    }

    public static IReadOnlyList<RuntimeModelInfo> Merge(IEnumerable<CatalogModel> listed, IReadOnlyList<RuntimeModelInfo> discovered)
    {
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(discovered);
        var result = new List<RuntimeModelInfo>();
        var seen = new HashSet<(Provider, string)>();
        foreach (var m in listed)
        {
            if (!seen.Add((m.Provider, m.Model)))
            {
                continue;
            }

            var known = discovered.FirstOrDefault(d => d.Provider == m.Provider && d.Model == m.Model)
                ?? discovered.FirstOrDefault(d => d.Provider == m.Provider);
            result.Add(new RuntimeModelInfo(m.Provider, m.Model, known?.Reachable ?? false, known?.Detail ?? ""));
        }

        result.AddRange(discovered.Where(d => seen.Add((d.Provider, d.Model))));
        return result;
    }
}
