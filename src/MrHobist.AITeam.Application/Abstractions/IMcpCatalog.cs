using MrHobist.AITeam.Domain.Mcp;

namespace MrHobist.AITeam.Application.Abstractions;

/// <summary>
/// Hazir MCP sunuculari: <c>config/mcp-catalog.json</c> (docs/DOMAIN.md → MCP sunuculari → Katalog). Kaynak koddur: git'te,
/// elle duzenlenir, sir TASIMAZ -- sirlar kurulumda kullanicidan alinir ve veritabanina gider.
/// </summary>
public interface IMcpCatalog
{
    /// <summary>Dosya yoksa bos liste; bozuksa <c>config.file_invalid</c>.</summary>
    Task<IReadOnlyList<McpCatalogEntry>> LoadAsync(CancellationToken ct);
}
