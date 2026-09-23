using Microsoft.EntityFrameworkCore;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain.Mcp;

namespace MrHobist.AITeam.Infrastructure.Persistence;

/// <summary>
/// <c>mcp_server</c>: sorgulanan alanlar (ad, tasima, komut, adres, acik/kapali) sutunda; args/env/basliklar <c>data</c> JSON'unda.
/// Tasima adi bilinmeyen satir (ileride silinmis bir uye) yok sayilir -- bozuk govde kuralinin karsiligi.
/// </summary>
internal sealed class SqliteMcpStore(IDbContextFactory<AiTeamContext> factory) : IMcpStore
{
    private sealed record Dto(
        List<string>? Args,
        Dictionary<string, string>? Env,
        Dictionary<string, string>? Headers,
        List<string>? Tools = null,
        List<McpKnownTool>? KnownTools = null,
        DateTimeOffset? ToolsCheckedAt = null,
        McpOAuth? OAuth = null);

    public async Task<IReadOnlyList<McpServer>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.McpServers.AsNoTracking().OrderBy(x => x.Key).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(ToDomain).Where(x => x is not null).Select(x => x!).ToList();
    }

    public async Task<McpServer?> GetAsync(string key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.McpServers.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct).ConfigureAwait(false);
        return row is null ? null : ToDomain(row);
    }

    public async Task SaveAsync(McpServer server, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.McpServers.FirstOrDefaultAsync(x => x.Key == server.Key, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            row = new McpServerRow { Key = server.Key, CreatedAt = now };
            db.McpServers.Add(row);
        }

        row.Name = server.Name;
        row.Description = server.Description;
        row.Transport = server.Transport.ToString();
        row.Command = server.Transport == McpTransport.Stdio ? server.Command : null;
        row.Url = server.Transport == McpTransport.Stdio ? null : server.Url;
        row.Enabled = server.Enabled;
        row.Data = PersistenceJson.Write(new Dto(
            server.Args.Count > 0 ? [.. server.Args] : null,
            server.Env.Count > 0 ? new Dictionary<string, string>(server.Env, StringComparer.Ordinal) : null,
            server.Headers.Count > 0 ? new Dictionary<string, string>(server.Headers, StringComparer.OrdinalIgnoreCase) : null,
            server.Tools is null ? null : [.. server.Tools],
            server.KnownTools is null ? null : [.. server.KnownTools],
            server.ToolsCheckedAt,
            server.OAuth));
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.McpServers.Where(x => x.Key == key).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private static McpServer? ToDomain(McpServerRow row)
    {
        if (!Enum.TryParse<McpTransport>(row.Transport, ignoreCase: true, out var transport))
        {
            return null;
        }

        var dto = PersistenceJson.Read<Dto>(row.Data) ?? new Dto(null, null, null);
        return new McpServer(
            row.Key,
            row.Name,
            transport,
            row.Command,
            dto.Args ?? [],
            row.Url,
            dto.Env ?? [],
            dto.Headers ?? [],
            row.Enabled,
            row.Description,
            row.UpdatedAt,
            dto.Tools,
            dto.KnownTools,
            dto.ToolsCheckedAt,
            dto.OAuth);
    }
}
