using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Mcp;

namespace MrHobist.AITeam.Application.Mcp;

/// <summary>Yeni ajanin varsayilan MCP yetkileri (katalogda <c>default.grantNewAgents</c>): docs/DOMAIN.md → Gercek tarayicida test.</summary>
public static class McpDefaults
{
    /// <summary>
    /// Katalogda yeni ajanlara verilen ve bu makinede kayitli sunucular. Kayitli olmayan verilmez (yetki dogrulamasi onu reddederdi);
    /// MCP calistiramayan saglayicida bos liste.
    /// </summary>
    public static async Task<IReadOnlyList<string>> NewAgentGrantsAsync(IMcpCatalog? catalog, IMcpStore? store, Provider? provider, CancellationToken ct)
    {
        if (catalog is null || store is null || (provider is { } p && !McpSupport.Supports(p)))
        {
            return [];
        }

        var keys = new List<string>();
        foreach (var entry in (await catalog.LoadAsync(ct).ConfigureAwait(false)).Where(e => e.Default is { GrantNewAgents: true }))
        {
            if (await store.GetAsync(entry.Key, ct).ConfigureAwait(false) is not null)
            {
                keys.Add(entry.Key);
            }
        }

        return keys;
    }
}
