using System.Text.Json;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Common;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.Application.Agents;

public sealed record AgentListItem(
    string Key,
    string Name,
    string Summary,
    IReadOnlyList<string> OfficeRoles,
    Provider? Provider,
    string? Model,
    string? Effort,
    IReadOnlyList<string> Includes,
    string? CanAsk,
    /// <summary>Yetkili MCP sunuculari (docs/DOMAIN.md → MCP sunuculari). Sona eklendi (CLAUDE.md §5).</summary>
    IReadOnlyList<string>? Mcp = null);

public sealed record AgentDetail(
    string Key,
    string Name,
    string Summary,
    IReadOnlyList<string> OfficeRoles,
    Provider? Provider,
    string? Model,
    string? Effort,
    IReadOnlyList<string> Includes,
    string? CanAsk,
    string Prompt,
    string ComposedPrompt,
    /// <summary>Yetkili MCP sunuculari. Sona eklendi (CLAUDE.md §5).</summary>
    IReadOnlyList<string>? Mcp = null);

/// <summary>
/// PUT govdesi; anahtar yoldan gelir. TUM alanlar tasinir: eksik/null liste ya da metin 400 <c>request.invalid</c>
/// (Api JSON secenekleri nullable notlarina uyar). <c>null</c> yalniz <see cref="Provider"/>, <see cref="Model"/>,
/// <see cref="CanAsk"/> icin gecerlidir ve "yok / varsayilan" demektir; kismi guncelleme yoktur.
/// <see cref="Provider"/> metin gelir ki bilinmeyen deger JSON hatasi degil <c>agent.invalid_provider</c> olsun.
/// <see cref="Mcp"/> sonradan eklendi: null = mevcut yetkiler KORUNUR (alani bilmeyen istemci yetkiyi silmesin), [] = hepsi kalkar.
/// </summary>
public sealed record UpdateAgentRequest(
    string Name,
    string Summary,
    IReadOnlyList<string> OfficeRoles,
    string? Provider,
    string? Model,
    IReadOnlyList<string> Includes,
    string? CanAsk,
    string Prompt,
    string? Effort = null,
    IReadOnlyList<string>? Mcp = null);

/// <summary>POST govdesi: <see cref="UpdateAgentRequest"/> + anahtar.</summary>
public sealed record CreateAgentRequest(
    string Key,
    string Name,
    string Summary,
    IReadOnlyList<string> OfficeRoles,
    string? Provider,
    string? Model,
    IReadOnlyList<string> Includes,
    string? CanAsk,
    string Prompt,
    string? Effort = null,
    IReadOnlyList<string>? Mcp = null);

public sealed record KnowledgeItem(string Key, string Title, string Body);

/// <summary><c>PUT /knowledge/{key}</c> govdesi. <see cref="Title"/> bos → anahtar.</summary>
public sealed record KnowledgeModel(string? Title, string Body);

/// <summary><c>POST /agents/import</c> ve <c>POST /knowledge/import</c> govdesi: kullanicinin yukledigi md metni.</summary>
public sealed record ImportMarkdownRequest(string Key, string Markdown);

public interface IAgentService
{
    Task<IReadOnlyList<AgentListItem>> ListAsync(CancellationToken ct);

    Task<AgentDetail> GetAsync(string key, CancellationToken ct);

    /// <summary>Var olan anahtar <c>agent.exists</c>. Ekip butunu dogrulanir, sonra atomik yazilir.</summary>
    Task<AgentDetail> CreateAsync(CreateAgentRequest request, CancellationToken ct);

    /// <summary>Ekibi butun olarak dogrular (eksik include, gecersiz can_ask), sonra atomik yazar.</summary>
    Task<AgentDetail> UpdateAsync(string key, UpdateAgentRequest request, CancellationToken ct);

    /// <summary>Bir akista (role/handoffRole) ya da bir can_ask'ta geciyorsa <c>agent.in_use</c>; once oradan cikarilir.</summary>
    Task DeleteAsync(string key, CancellationToken ct);

    Task<IReadOnlyList<KnowledgeItem>> ListKnowledgeAsync(CancellationToken ct);

    /// <summary>Hazir ajan md'sini (frontmatter + prompt) ekibe ekler; var olan anahtar <c>agent.exists</c>.</summary>
    Task<AgentDetail> ImportAsync(ImportMarkdownRequest request, CancellationToken ct);

    /// <summary>Bilgi dosyasi olusturur ya da uzerine yazar (baslik + govde).</summary>
    Task<KnowledgeItem> UpsertKnowledgeAsync(string key, KnowledgeModel model, CancellationToken ct);

    /// <summary>Yuklenen bilgi md'sini (frontmatter title? + govde) yazar.</summary>
    Task<KnowledgeItem> ImportKnowledgeAsync(ImportMarkdownRequest request, CancellationToken ct);

    /// <summary>Bir ajanin <c>includes</c>'inde geciyorsa <c>knowledge.in_use</c>.</summary>
    Task DeleteKnowledgeAsync(string key, CancellationToken ct);

}

public sealed class AgentService(IAgentStore store, IWorkflowStore workflows, ISceneLayout scene, ISceneEventPublisher events, IMcpStore? mcp = null) : IAgentService
{
    /// <summary>Sahne yerlesimi degisti: UI yeniden kurar (docs/SCENE.md → scene.reload).</summary>
    private void PublishReload(string reason) => events.Publish(SceneEventTypes.SceneReload, JsonSerializer.Serialize(new { reason }));

    public async Task<IReadOnlyList<AgentListItem>> ListAsync(CancellationToken ct)
    {
        var team = await store.LoadTeamAsync(ct).ConfigureAwait(false);
        return team.Agents.Values.OrderBy(a => a.Key, StringComparer.Ordinal).Select(ToItem).ToList();
    }

    public async Task<AgentDetail> GetAsync(string key, CancellationToken ct)
    {
        var team = await store.LoadTeamAsync(ct).ConfigureAwait(false);
        return ToDetail(Find(team, key), team);
    }

    public async Task<AgentDetail> CreateAsync(CreateAgentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = Identifiers.Require(request.Key, ErrorCodes.AgentInvalidKey, "ajan");
        var team = await store.LoadTeamAsync(ct).ConfigureAwait(false);
        if (team.Agents.ContainsKey(key))
        {
            throw new DomainException(ErrorCodes.AgentExists, $"'{key}' anahtarli ajan zaten var.");
        }

        var agent = Compose(
            new Agent(key, key, "", [], null, null, [], null, ""),
            new UpdateAgentRequest(request.Name, request.Summary, request.OfficeRoles, request.Provider, request.Model, request.Includes, request.CanAsk, request.Prompt, request.Effort, request.Mcp));
        var detail = await SaveValidatedAsync(team, agent, ct).ConfigureAwait(false);

        // Sahne: bos sprite + bos masa (yoksa ziyaretci). Ekip md'si yazildiktan sonra; yerlesim hatasi ajani geri almaz.
        await scene.UpsertAgentAsync(agent.Key, agent.Name, ct).ConfigureAwait(false);
        PublishReload($"ajan eklendi: {agent.Key}");
        return detail;
    }

    public async Task<AgentDetail> UpdateAsync(string key, UpdateAgentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var team = await store.LoadTeamAsync(ct).ConfigureAwait(false);
        var current = Find(team, key);
        var detail = await SaveValidatedAsync(team, Compose(current, request), ct).ConfigureAwait(false);

        // Kosulsuz: sahne kaydi turetilmis durumdur. Ekipte olup sahnede olmayan bir ajan (md dosyasi elle
        // eklenmisse boyle olur) her kayitta kendiliginden yerlesir; hicbir sey degismediyse dosya yazilmaz.
        if (await scene.UpsertAgentAsync(key, detail.Name, ct).ConfigureAwait(false))
        {
            PublishReload($"ajan sahne kaydi guncellendi: {key}");
        }

        return detail;
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        var team = await store.LoadTeamAsync(ct).ConfigureAwait(false);
        var agent = Find(team, key);

        var usedBy = new List<string>();
        foreach (var wfKey in await workflows.ListKeysAsync(ct).ConfigureAwait(false))
        {
            var wf = await workflows.LoadAsync(wfKey, ct).ConfigureAwait(false);
            if (wf.Roles.Contains(agent.Key, StringComparer.Ordinal))
            {
                usedBy.Add($"akis '{wfKey}'");
            }
        }

        usedBy.AddRange(team.AskersOf(agent.Key).Select(a => $"'{a}' ajaninin can_ask'i"));
        if (usedBy.Count > 0)
        {
            throw new DomainException(ErrorCodes.AgentInUse, $"'{agent.Key}' kullanimda: {string.Join(", ", usedBy)}. Once oradan cikarin.");
        }

        await store.DeleteAgentAsync(agent.Key, ct).ConfigureAwait(false);
        await scene.RemoveAgentAsync(agent.Key, ct).ConfigureAwait(false);
        PublishReload($"ajan silindi: {agent.Key}");
    }

    public async Task<AgentDetail> ImportAsync(ImportMarkdownRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = Identifiers.Require(request.Key, ErrorCodes.AgentInvalidKey, "ajan");
        var team = await store.LoadTeamAsync(ct).ConfigureAwait(false);
        if (team.Agents.ContainsKey(key))
        {
            throw new DomainException(ErrorCodes.AgentExists, $"'{key}' anahtarli ajan zaten var.");
        }

        var agent = store.ParseAgentMarkdown(key, request.Markdown ?? "");
        var detail = await SaveValidatedAsync(team, agent, ct).ConfigureAwait(false);
        await scene.UpsertAgentAsync(agent.Key, agent.Name, ct).ConfigureAwait(false);
        PublishReload($"ajan md'den eklendi: {agent.Key}");
        return detail;
    }

    public async Task<KnowledgeItem> UpsertKnowledgeAsync(string key, KnowledgeModel model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        var k = Identifiers.Require(key, ErrorCodes.KnowledgeInvalidKey, "bilgi");
        if (string.IsNullOrWhiteSpace(model.Body))
        {
            throw new DomainException(ErrorCodes.KnowledgeBodyEmpty, $"{k}: govde bos; bos bilgi dosyasi prompt'a bir sey katmaz.");
        }

        var item = new Knowledge(k, string.IsNullOrWhiteSpace(model.Title) ? k.Replace('-', ' ') : model.Title.Trim(), model.Body.Trim() + "\n");
        await store.SaveKnowledgeAsync(item, ct).ConfigureAwait(false);
        return new KnowledgeItem(item.Key, item.Title, item.Body);
    }

    public async Task<KnowledgeItem> ImportKnowledgeAsync(ImportMarkdownRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parsed = store.ParseKnowledgeMarkdown(request.Key, request.Markdown ?? "");
        return await UpsertKnowledgeAsync(parsed.Key, new KnowledgeModel(parsed.Title, parsed.Body), ct).ConfigureAwait(false);
    }

    public async Task DeleteKnowledgeAsync(string key, CancellationToken ct)
    {
        var k = Identifiers.Require(key, ErrorCodes.KnowledgeInvalidKey, "bilgi");
        var team = await store.LoadTeamAsync(ct).ConfigureAwait(false);
        if (!team.Knowledge.ContainsKey(k))
        {
            throw new NotFoundException(ErrorCodes.KnowledgeNotFound, $"Bilgi dosyasi yok: '{k}'.");
        }

        var users = team.Agents.Values.Where(a => a.Includes.Contains(k, StringComparer.Ordinal)).Select(a => a.Key).ToList();
        if (users.Count > 0)
        {
            throw new DomainException(ErrorCodes.KnowledgeInUse, $"'{k}' su ajanlarin bilgi dosyalarinda: {string.Join(", ", users)}. Once oradan cikarin.");
        }

        await store.DeleteKnowledgeAsync(k, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<KnowledgeItem>> ListKnowledgeAsync(CancellationToken ct)
    {
        var team = await store.LoadTeamAsync(ct).ConfigureAwait(false);
        return team.Knowledge.Values.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new KnowledgeItem(k.Key, k.Title, k.Body)).ToList();
    }


    /// <summary>Istekten ajan kaydi: metinler kirpilir, bos model/can_ask null olur.</summary>
    private static Agent Compose(Agent current, UpdateAgentRequest request) => current with
    {
        Name = string.IsNullOrWhiteSpace(request.Name) ? current.Key : request.Name.Trim(),
        Summary = request.Summary.Trim(),
        OfficeRoles = request.OfficeRoles,
        Provider = Providers.Parse(request.Provider),
        Model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim(),
        Effort = Efforts.Parse(request.Effort),
        Includes = request.Includes,
        CanAsk = string.IsNullOrWhiteSpace(request.CanAsk) ? null : request.CanAsk.Trim(),
        Prompt = request.Prompt,
        Mcp = request.Mcp is null ? current.Mcp : request.Mcp.Select(m => m.Trim()).Distinct(StringComparer.Ordinal).ToList() is { Count: > 0 } list ? list : null,
    };

    /// <summary>Once ekip butunu dogrulanir: yazilan dosya bir sonraki yuklemede patlamamali.</summary>
    private async Task<AgentDetail> SaveValidatedAsync(Team team, Agent agent, CancellationToken ct)
    {
        var agents = new Dictionary<string, Agent>(team.Agents, StringComparer.Ordinal) { [agent.Key] = agent };
        var next = team with { Agents = agents };
        next.Validate();
        await RequireKnownMcpAsync(agent, team.Agents.GetValueOrDefault(agent.Key), ct).ConfigureAwait(false);

        await store.SaveAgentAsync(agent, ct).ConfigureAwait(false);
        return ToDetail(agent, next);
    }

    /// <summary>
    /// Yeni verilen MCP yetkisi kayitli bir sunucuya isaret etmeli. Onceden var olan anahtar denetlenmez: sunucu sonradan
    /// silindiyse ajanin baska bir alanini kaydetmek engellenmesin (calisma aninda atlanir ve kayda yazilir).
    /// </summary>
    private async Task RequireKnownMcpAsync(Agent agent, Agent? before, CancellationToken ct)
    {
        var added = agent.McpServers.Except(before?.McpServers ?? [], StringComparer.Ordinal).ToList();
        if (added.Count == 0 || mcp is null)
        {
            return;
        }

        var known = (await mcp.ListAsync(ct).ConfigureAwait(false)).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var unknown = added.Where(a => !known.Contains(a)).ToList();
        if (unknown.Count > 0)
        {
            throw new DomainException(ErrorCodes.AgentUnknownMcp, $"{agent.Key}: kayitli olmayan MCP sunucusu: {string.Join(", ", unknown)}.");
        }
    }

    private static Agent Find(Team team, string key)
    {
        if (!Identifiers.IsValidKey(key))
        {
            throw new DomainException(ErrorCodes.AgentInvalidKey, $"Gecersiz ajan anahtari: '{key}'.");
        }

        return team.Agents.TryGetValue(key, out var agent)
            ? agent
            : throw new NotFoundException(ErrorCodes.AgentNotFound, $"Ajan yok: '{key}'.");
    }

    private static AgentListItem ToItem(Agent a)
        => new(a.Key, a.Name, a.Summary, a.OfficeRoles, a.Provider, a.Model, a.Effort, a.Includes, a.CanAsk, a.McpServers);

    private static AgentDetail ToDetail(Agent a, Team team)
        => new(a.Key, a.Name, a.Summary, a.OfficeRoles, a.Provider, a.Model, a.Effort, a.Includes, a.CanAsk, a.Prompt, a.ComposePrompt(team.Knowledge), a.McpServers);
}
