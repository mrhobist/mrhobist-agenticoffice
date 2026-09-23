using MrHobist.AITeam.Domain.Mcp;

namespace MrHobist.AITeam.Application.Mcp;

/// <summary>
/// OAuth'lu MCP sunucularinin belirtecini tur ONCESI tazeler (docs/DOMAIN.md → MCP → OAuth). Suresi <c>margin</c> icinde dolacak ve
/// yenileme belirteci olan sunucu yenilenir, depoya yazilir; yenileme basarisizsa sunucu oldugu gibi doner (Resolve onu "giris yok"
/// diye atlar, calisma durmaz). Yalniz <paramref name="keys"/>'teki sunuculara dokunulur.
/// </summary>
public interface IMcpTokenRefresher
{
    Task<IReadOnlyList<McpServer>> EnsureFreshAsync(IReadOnlyList<McpServer> servers, IReadOnlyList<string> keys, CancellationToken ct);
}
