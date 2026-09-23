using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Common;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Mcp;

namespace MrHobist.AITeam.Application.Mcp;

/// <summary>Ortam degiskeni / baslik satiri. Yanitta <see cref="Value"/> DAIMA null'dir (sir); <see cref="HasValue"/> kayitli mi soyler.</summary>
public sealed record McpSecretEntry(string Name, bool HasValue);

/// <summary>
/// Istekte ortam degiskeni / baslik. <see cref="Value"/> null → guncellemede kayitli deger KORUNUR (UI sirri geri okuyamaz,
/// yeniden yazmak zorunda kalmasin); bos metin → bos deger. Yeni sunucuda null bos metin sayilir.
/// </summary>
public sealed record McpSecretInput(string Name, string? Value = null);

/// <summary><c>GET /mcp</c> ogesi. <see cref="Agents"/>: bu sunucuya yetkili ajanlar (md frontmatter <c>mcp</c>).</summary>
public sealed record McpServerView(
    string Key,
    string Name,
    string Description,
    McpTransport Transport,
    string? Command,
    IReadOnlyList<string> Args,
    string? Url,
    IReadOnlyList<McpSecretEntry> Env,
    IReadOnlyList<McpSecretEntry> Headers,
    bool Enabled,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<string> Agents);

/// <summary><c>POST /mcp</c> ve <c>PUT /mcp/{key}</c> govdesi (PUT'ta <see cref="Key"/> yoldan gelir, govdedeki yok sayilir).</summary>
public sealed record McpServerRequest(
    string Key,
    string Name,
    McpTransport Transport,
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    string? Url = null,
    IReadOnlyList<McpSecretInput>? Env = null,
    IReadOnlyList<McpSecretInput>? Headers = null,
    bool Enabled = true,
    string? Description = null);

/// <summary>
/// <c>POST /mcp/catalog/{key}/install</c> govdesi: secilen baglanti yontemi ve formun degerleri (alan adi → deger). <see cref="Key"/>
/// bossa katalog anahtari, <see cref="Name"/> bossa katalog adi. Degerler (sirlar dahil) yalniz kurulan sunucuya yazilir.
/// </summary>
public sealed record McpInstallRequest(string Option, IReadOnlyDictionary<string, string?>? Values = null, string? Key = null, string? Name = null);

/// <summary><c>PUT /mcp/{key}/access</c> govdesi: bu sunucuyu kullanabilecek ajanlarin TAM listesi.</summary>
public sealed record McpAccessRequest(IReadOnlyList<string> Agents);

/// <summary><c>POST /mcp/{key}/test</c> sonucu: baglanti acildi mi, hangi araclar geldi (ajan araci <c>mcp__{key}__{ad}</c>).</summary>
public sealed record McpTestResult(bool Ok, string Detail, IReadOnlyList<RuntimeMcpTool> Tools, string? ServerName, string? ServerVersion);

public interface IMcpService
{
    Task<IReadOnlyList<McpServerView>> ListAsync(CancellationToken ct);

    Task<McpServerView> GetAsync(string key, CancellationToken ct);

    /// <summary>Var olan anahtar <c>mcp.exists</c>.</summary>
    Task<McpServerView> CreateAsync(McpServerRequest request, CancellationToken ct);

    Task<McpServerView> UpdateAsync(string key, McpServerRequest request, CancellationToken ct);

    /// <summary>Bir ajana yetkiliyse <c>mcp.in_use</c>: once yetkiler kaldirilir.</summary>
    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>Ajan md'lerini gunceller: listedekilere yetki eklenir, digerlerinden kaldirilir. Degismeyen md yazilmaz.</summary>
    Task<McpServerView> SetAccessAsync(string key, McpAccessRequest request, CancellationToken ct);

    /// <summary>Runtime sunucuyu acip araclarini listeler. Runtime kapaliysa 503; baglanti hatasi <see cref="McpTestResult.Ok"/> = false.</summary>
    Task<McpTestResult> TestAsync(string key, CancellationToken ct);

    /// <summary>Hazir sunucular (<c>config/mcp-catalog.json</c>). Sir tasimaz.</summary>
    Task<IReadOnlyList<McpCatalogEntry>> CatalogAsync(CancellationToken ct);

    /// <summary>Katalogdan kurar: secenek + degerler → yeni sunucu. Var olan anahtar <c>mcp.exists</c>, bilinmeyen <c>mcp.catalog_not_found</c>.</summary>
    Task<McpServerView> InstallAsync(string catalogKey, McpInstallRequest request, CancellationToken ct);
}

/// <summary>
/// MCP sunucu yonetimi (docs/DOMAIN.md → MCP sunuculari). Tanim veritabaninda (<see cref="IMcpStore"/>), yetki ajan md'sinde
/// (<see cref="Agent.Mcp"/>). Sirlar (env/baslik degerleri) hicbir yanita yazilmaz.
/// </summary>
public sealed class McpService(IMcpStore store, IAgentStore agents, IAgentRuntimeService runtime, IMcpCatalog? catalog = null) : IMcpService
{
    /// <summary><c>/mcp/catalog</c> sabit yolu bu anahtarli bir sunucuyu golgelerdi.</summary>
    private const string ReservedKey = "catalog";

    public async Task<IReadOnlyList<McpCatalogEntry>> CatalogAsync(CancellationToken ct)
        => catalog is null ? [] : await catalog.LoadAsync(ct).ConfigureAwait(false);

    public async Task<McpServerView> InstallAsync(string catalogKey, McpInstallRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entry = (await CatalogAsync(ct).ConfigureAwait(false)).FirstOrDefault(e => e.Key == catalogKey)
            ?? throw new NotFoundException(ErrorCodes.McpCatalogNotFound, $"Katalogda yok: '{catalogKey}'.");
        var option = entry.Options.FirstOrDefault(o => o.Id == request.Option)
            ?? throw new NotFoundException(ErrorCodes.McpCatalogNotFound, $"{entry.Name}: '{request.Option}' baglanti secenegi yok.");
        var key = RequireFreeKey(string.IsNullOrWhiteSpace(request.Key) ? entry.Key : request.Key.Trim());
        if (await store.GetAsync(key, ct).ConfigureAwait(false) is not null)
        {
            throw new DomainException(ErrorCodes.McpExists, $"'{key}' anahtarli MCP sunucusu zaten var; baska bir anahtar ver ya da var olani duzenle.");
        }

        var server = McpCatalogBuilder.Build(entry, option, key, request.Name, request.Values ?? new Dictionary<string, string?>());
        await store.SaveAsync(server, ct).ConfigureAwait(false);
        return await GetAsync(key, ct).ConfigureAwait(false);
    }

    private static string RequireFreeKey(string key)
    {
        Identifiers.Require(key, ErrorCodes.McpInvalidKey, "MCP sunucusu");
        return key == ReservedKey
            ? throw new DomainException(ErrorCodes.McpInvalidKey, $"'{ReservedKey}' ayrilmis bir ad; baska bir anahtar ver.")
            : key;
    }

    public async Task<IReadOnlyList<McpServerView>> ListAsync(CancellationToken ct)
    {
        var team = await agents.LoadTeamAsync(ct).ConfigureAwait(false);
        var servers = await store.ListAsync(ct).ConfigureAwait(false);
        return servers.Select(s => View(s, team)).ToList();
    }

    public async Task<McpServerView> GetAsync(string key, CancellationToken ct)
    {
        var server = await FindAsync(key, ct).ConfigureAwait(false);
        return View(server, await agents.LoadTeamAsync(ct).ConfigureAwait(false));
    }

    public async Task<McpServerView> CreateAsync(McpServerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = RequireFreeKey(request.Key?.Trim() ?? "");
        if (await store.GetAsync(key, ct).ConfigureAwait(false) is not null)
        {
            throw new DomainException(ErrorCodes.McpExists, $"'{key}' anahtarli MCP sunucusu zaten var.");
        }

        var server = Compose(key, request, null);
        server.Validate();
        await store.SaveAsync(server, ct).ConfigureAwait(false);
        return await GetAsync(key, ct).ConfigureAwait(false);
    }

    public async Task<McpServerView> UpdateAsync(string key, McpServerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await FindAsync(key, ct).ConfigureAwait(false);
        var server = Compose(current.Key, request, current);
        server.Validate();
        await store.SaveAsync(server, ct).ConfigureAwait(false);
        return await GetAsync(current.Key, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        var server = await FindAsync(key, ct).ConfigureAwait(false);
        var team = await agents.LoadTeamAsync(ct).ConfigureAwait(false);
        var users = UsersOf(team, server.Key);
        if (users.Count > 0)
        {
            throw new DomainException(ErrorCodes.McpInUse, $"'{server.Key}' su ajanlara yetkili: {string.Join(", ", users)}. Once yetkileri kaldirin.");
        }

        await store.DeleteAsync(server.Key, ct).ConfigureAwait(false);
    }

    public async Task<McpServerView> SetAccessAsync(string key, McpAccessRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var server = await FindAsync(key, ct).ConfigureAwait(false);
        var team = await agents.LoadTeamAsync(ct).ConfigureAwait(false);
        var wanted = (request.Agents ?? []).Select(a => a.Trim()).ToHashSet(StringComparer.Ordinal);
        foreach (var a in wanted.Where(a => !team.Agents.ContainsKey(a)))
        {
            throw new NotFoundException(ErrorCodes.AgentNotFound, $"Ajan yok: '{a}'.");
        }

        // Once hepsi dogrulanir (saglayici MCP destekliyor mu), sonra yazilir: yarim yetki dagitimi kalmasin.
        var changed = new List<Agent>();
        foreach (var agent in team.Agents.Values)
        {
            var has = agent.McpServers.Contains(server.Key, StringComparer.Ordinal);
            var want = wanted.Contains(agent.Key);
            if (has == want)
            {
                continue;
            }

            var list = want ? [.. agent.McpServers, server.Key] : agent.McpServers.Where(m => m != server.Key).ToList();
            var next = agent with { Mcp = list.Count > 0 ? list : null };
            next.Validate();
            changed.Add(next);
        }

        foreach (var agent in changed)
        {
            await agents.SaveAgentAsync(agent, ct).ConfigureAwait(false);
        }

        return await GetAsync(server.Key, ct).ConfigureAwait(false);
    }

    public async Task<McpTestResult> TestAsync(string key, CancellationToken ct)
    {
        var server = await FindAsync(key, ct).ConfigureAwait(false);
        try
        {
            var probe = await runtime.ProbeMcpAsync(ToRuntime(server), ct).ConfigureAwait(false);
            return new McpTestResult(probe.Ok, probe.Detail, probe.Tools, probe.ServerName, probe.ServerVersion);
        }
        catch (InvalidOperationException ex)
        {
            // Runtime ayakta ama deneme ucu hata dondu: baglanti basarisiz sayilir, neden ekranda.
            return new McpTestResult(false, ex.Message, [], null, null);
        }
    }

    /// <summary>
    /// Ajana acilacak sunucular: md'deki anahtarlardan KAYITLI ve ACIK olanlar. Silinmis/kapali anahtar sessizce degil,
    /// <paramref name="skipped"/> ile doner (cagiran kayda yazar).
    /// </summary>
    public static IReadOnlyDictionary<string, RuntimeMcpServer> Resolve(Agent agent, IReadOnlyList<McpServer> servers, out IReadOnlyList<string> skipped)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(servers);
        var byKey = servers.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var result = new Dictionary<string, RuntimeMcpServer>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var key in agent.McpServers)
        {
            if (byKey.TryGetValue(key, out var s) && s.Enabled)
            {
                result[key] = ToRuntime(s);
            }
            else
            {
                missing.Add(key);
            }
        }

        skipped = missing;
        return result;
    }

    public static RuntimeMcpServer ToRuntime(McpServer s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Transport == McpTransport.Stdio
            ? new RuntimeMcpServer(McpServer.Wire(s.Transport), Command: s.Command, Args: s.Args, Env: s.Env)
            : new RuntimeMcpServer(McpServer.Wire(s.Transport), Url: s.Url, Headers: s.Headers);
    }

    private static McpServer Compose(string key, McpServerRequest r, McpServer? current) => new(
        key,
        string.IsNullOrWhiteSpace(r.Name) ? key : r.Name.Trim(),
        r.Transport,
        string.IsNullOrWhiteSpace(r.Command) ? null : r.Command.Trim(),
        (r.Args ?? []).Where(a => a is not null).ToList(),
        string.IsNullOrWhiteSpace(r.Url) ? null : r.Url.Trim(),
        Merge(r.Env, current?.Env, StringComparer.Ordinal),
        Merge(r.Headers, current?.Headers, StringComparer.OrdinalIgnoreCase),
        r.Enabled,
        r.Description?.Trim() ?? "",
        current?.UpdatedAt);

    /// <summary>Degeri null gelen satir kayitli degeri korur; listede olmayan ad silinir. Ayni ad iki kez → <c>mcp.invalid</c>.</summary>
    private static Dictionary<string, string> Merge(IReadOnlyList<McpSecretInput>? input, IReadOnlyDictionary<string, string>? current, StringComparer comparer)
    {
        var result = new Dictionary<string, string>(comparer);
        foreach (var e in input ?? [])
        {
            var name = e.Name?.Trim() ?? "";
            if (name.Length == 0)
            {
                throw new DomainException(ErrorCodes.McpInvalid, "ortam degiskeni / baslik adi bos olamaz.");
            }

            if (!result.TryAdd(name, e.Value ?? (current is not null && current.TryGetValue(name, out var old) ? old : "")))
            {
                throw new DomainException(ErrorCodes.McpInvalid, $"'{name}' iki kez verildi.");
            }
        }

        return result;
    }

    private async Task<McpServer> FindAsync(string key, CancellationToken ct)
    {
        if (!Identifiers.IsValidKey(key))
        {
            throw new DomainException(ErrorCodes.McpInvalidKey, $"Gecersiz MCP anahtari: '{key}'.");
        }

        return await store.GetAsync(key, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(ErrorCodes.McpNotFound, $"MCP sunucusu yok: '{key}'.");
    }

    private static List<string> UsersOf(Team team, string key)
        => team.Agents.Values.Where(a => a.McpServers.Contains(key, StringComparer.Ordinal)).Select(a => a.Key).Order(StringComparer.Ordinal).ToList();

    private static McpServerView View(McpServer s, Team team) => new(
        s.Key,
        s.Name,
        s.Description,
        s.Transport,
        s.Command,
        s.Args,
        s.Url,
        s.Env.Select(kv => new McpSecretEntry(kv.Key, kv.Value.Length > 0)).OrderBy(e => e.Name, StringComparer.Ordinal).ToList(),
        s.Headers.Select(kv => new McpSecretEntry(kv.Key, kv.Value.Length > 0)).OrderBy(e => e.Name, StringComparer.Ordinal).ToList(),
        s.Enabled,
        s.UpdatedAt,
        UsersOf(team, s.Key));
}
