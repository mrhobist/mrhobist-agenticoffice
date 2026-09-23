using MrHobist.AITeam.Application.Abstractions;

namespace MrHobist.AITeam.Application.Mcp;

/// <summary>Bir aracin cagri sayisi.</summary>
public sealed record McpToolUsage(string Name, int Calls);

/// <summary>Bir ajanin bu sunucuyla iliskisi: kac turda verildi, kac arac cagrisi yapti.</summary>
public sealed record McpAgentUsage(string Agent, int OfferedTurns, int Calls);

/// <summary>
/// Sunucu basina kullanim. <see cref="OfferedTurns"/>: sunucunun ajana acildigi tur (her birinde arac semalari baglama girdi);
/// <see cref="UsedTurns"/>: en az bir araci cagrildigi tur. Verildigi halde hic kullanilmayan sunucu bosuna odenen baglamdir.
/// <see cref="Registered"/> false: silinmis bir sunucunun gecmis kaydi.
/// </summary>
public sealed record McpServerUsage(
    string Key,
    string? Name,
    bool Registered,
    int OfferedTurns,
    int UsedTurns,
    int Calls,
    int Runs,
    DateTimeOffset? LastUsedAt,
    IReadOnlyList<McpToolUsage> Tools,
    IReadOnlyList<McpAgentUsage> Agents);

/// <summary><c>GET /mcp/usage</c>: son <see cref="RunLimit"/> calismada MCP kullanimi.</summary>
public sealed record McpUsageReport(int RunLimit, int TurnsWithMcp, int Calls, IReadOnlyList<McpServerUsage> Servers);

public interface IMcpUsageReader
{
    Task<McpUsageReport> SummarizeAsync(int runLimit, CancellationToken ct);
}

/// <summary>
/// MCP kullanim raporu (kullanici istegi 2026-09-23). Kaynak tur kayitlaridir: acilan sunucular (<c>Turn.McpServers</c>) ve arac
/// cagrilari (<c>Turn.ToolUses</c>, <c>mcp__{sunucu}__{arac}</c>). Tahmin yok: sema tokeni bilinmedigi icin rapora girmez.
/// </summary>
public sealed class McpUsageReader(IRunStore runs, IMcpStore store) : IMcpUsageReader
{
    public async Task<McpUsageReport> SummarizeAsync(int runLimit, CancellationToken ct)
    {
        var limit = Math.Clamp(runLimit, 1, 2000);
        var turns = await runs.ReadMcpUsageAsync(limit, ct).ConfigureAwait(false);
        var registered = (await store.ListAsync(ct).ConfigureAwait(false)).ToDictionary(s => s.Key, s => s.Name, StringComparer.Ordinal);

        var acc = new Dictionary<string, Acc>(StringComparer.Ordinal);
        Acc Of(string key) => acc.TryGetValue(key, out var a) ? a : acc[key] = new Acc();

        var calls = 0;
        foreach (var t in turns)
        {
            foreach (var key in t.McpServers ?? [])
            {
                var a = Of(key);
                a.Offered++;
                a.AgentOffered[t.Agent] = a.AgentOffered.GetValueOrDefault(t.Agent) + 1;
            }

            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in t.Tools)
            {
                var (key, tool) = Split(name, registered.Keys, t.McpServers);
                var a = Of(key);
                a.Calls++;
                calls++;
                a.Tools[tool] = a.Tools.GetValueOrDefault(tool) + 1;
                a.AgentCalls[t.Agent] = a.AgentCalls.GetValueOrDefault(t.Agent) + 1;
                a.Runs.Add(t.RunId);
                a.LastUsed = a.LastUsed is { } l && l > t.Ts ? l : t.Ts;
                used.Add(key);
            }

            foreach (var key in used)
            {
                Of(key).Used++;
            }
        }

        foreach (var key in registered.Keys)
        {
            Of(key);
        }

        var servers = acc
            .Select(kv => new McpServerUsage(
                kv.Key,
                registered.GetValueOrDefault(kv.Key),
                registered.ContainsKey(kv.Key),
                kv.Value.Offered,
                kv.Value.Used,
                kv.Value.Calls,
                kv.Value.Runs.Count,
                kv.Value.LastUsed,
                kv.Value.Tools.Select(x => new McpToolUsage(x.Key, x.Value)).OrderByDescending(x => x.Calls).ThenBy(x => x.Name, StringComparer.Ordinal).ToList(),
                kv.Value.AgentOffered.Keys.Union(kv.Value.AgentCalls.Keys, StringComparer.Ordinal)
                    .Select(a => new McpAgentUsage(a, kv.Value.AgentOffered.GetValueOrDefault(a), kv.Value.AgentCalls.GetValueOrDefault(a)))
                    .OrderByDescending(x => x.Calls).ThenBy(x => x.Agent, StringComparer.Ordinal).ToList()))
            .OrderByDescending(s => s.Calls).ThenByDescending(s => s.OfferedTurns).ThenBy(s => s.Key, StringComparer.Ordinal)
            .ToList();

        return new McpUsageReport(limit, turns.Count, calls, servers);
    }

    /// <summary>
    /// <c>mcp__{sunucu}__{arac}</c> → (sunucu, arac). Sunucu anahtari <c>__</c> icerebilir; once o turda acilan, sonra kayitli anahtarlardan
    /// en uzun eslesen onek secilir, yoksa ilk <c>__</c>'ta bolunur.
    /// </summary>
    internal static (string Key, string Tool) Split(string name, IEnumerable<string> known, IReadOnlyList<string>? offered)
    {
        var rest = name["mcp__".Length..];
        var match = (offered ?? []).Concat(known)
            .Where(k => rest.StartsWith(k + "__", StringComparison.Ordinal))
            .OrderByDescending(k => k.Length)
            .FirstOrDefault();
        if (match is not null)
        {
            return (match, rest[(match.Length + 2)..]);
        }

        var i = rest.IndexOf("__", StringComparison.Ordinal);
        return i > 0 ? (rest[..i], rest[(i + 2)..]) : (rest, "?");
    }

    private sealed class Acc
    {
        public int Offered { get; set; }

        public int Used { get; set; }

        public int Calls { get; set; }

        public DateTimeOffset? LastUsed { get; set; }

        public HashSet<string> Runs { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> Tools { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> AgentOffered { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> AgentCalls { get; } = new(StringComparer.Ordinal);
    }
}
