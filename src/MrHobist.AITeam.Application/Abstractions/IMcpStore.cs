using MrHobist.AITeam.Domain.Mcp;

namespace MrHobist.AITeam.Application.Abstractions;

/// <summary>
/// Kayitli MCP sunuculari: <c>mcp_server</c> tablosu (docs/DOMAIN.md → MCP sunuculari). Degerler (env, basliklar) acik saklanir --
/// veritabani makinede ve gitignore'da; maskeleme sinirin (Api yaniti) isidir, deponun degil.
/// </summary>
public interface IMcpStore
{
    Task<IReadOnlyList<McpServer>> ListAsync(CancellationToken ct);

    /// <summary>Yoksa null.</summary>
    Task<McpServer?> GetAsync(string key, CancellationToken ct);

    /// <summary>Olusturur ya da uzerine yazar. Cagiran <see cref="McpServer.Validate"/> ile dogrulamis olmali.</summary>
    Task SaveAsync(McpServer server, CancellationToken ct);

    /// <summary>Yoksa sessiz. Ajanlarin yetki denetimi cagiranin isidir.</summary>
    Task DeleteAsync(string key, CancellationToken ct);
}
