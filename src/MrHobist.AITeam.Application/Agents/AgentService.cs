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
    string? CanAsk);

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
    string ComposedPrompt);

/// <summary>
/// PUT govdesi; anahtar yoldan gelir. TUM alanlar tasinir: eksik/null liste ya da metin 400 <c>request.invalid</c>
/// (Api JSON secenekleri nullable notlarina uyar). <c>null</c> yalniz <see cref="Provider"/>, <see cref="Model"/>,
/// <see cref="CanAsk"/> icin gecerlidir ve "yok / varsayilan" demektir; kismi guncelleme yoktur.
/// <see cref="Provider"/> metin gelir ki bilinmeyen deger JSON hatasi degil <c>agent.invalid_provider</c> olsun.
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
    string? Effort = null);

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
    string? Effort = null);

public sealed record KnowledgeItem(string Key, string Title, string Body);

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

    /// <summary>Modele fiilen giden metin: govde + alt md'ler. RunService bunu kullanir.</summary>
    Task<string> ComposePromptAsync(string key, CancellationToken ct);
}

public sealed class AgentService(IAgentStore store, IWorkflowStore workflows) : IAgentService
{
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
            new UpdateAgentRequest(request.Name, request.Summary, request.OfficeRoles, request.Provider, request.Model, request.Includes, request.CanAsk, request.Prompt, request.Effort));
        return await SaveValidatedAsync(team, agent, ct).ConfigureAwait(false);
    }

    public async Task<AgentDetail> UpdateAsync(string key, UpdateAgentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var team = await store.LoadTeamAsync(ct).ConfigureAwait(false);
        return await SaveValidatedAsync(team, Compose(Find(team, key), request), ct).ConfigureAwait(false);
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
    }

    public async Task<IReadOnlyList<KnowledgeItem>> ListKnowledgeAsync(CancellationToken ct)
    {
        var team = await store.LoadTeamAsync(ct).ConfigureAwait(false);
        return team.Knowledge.Values.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new KnowledgeItem(k.Key, k.Title, k.Body)).ToList();
    }

    public async Task<string> ComposePromptAsync(string key, CancellationToken ct)
    {
        var team = await store.LoadTeamAsync(ct).ConfigureAwait(false);
        return Find(team, key).ComposePrompt(team.Knowledge);
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
    };

    /// <summary>Once ekip butunu dogrulanir: yazilan dosya bir sonraki yuklemede patlamamali.</summary>
    private async Task<AgentDetail> SaveValidatedAsync(Team team, Agent agent, CancellationToken ct)
    {
        var agents = new Dictionary<string, Agent>(team.Agents, StringComparer.Ordinal) { [agent.Key] = agent };
        var next = team with { Agents = agents };
        next.Validate();

        await store.SaveAgentAsync(agent, ct).ConfigureAwait(false);
        return ToDetail(agent, next);
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
        => new(a.Key, a.Name, a.Summary, a.OfficeRoles, a.Provider, a.Model, a.Effort, a.Includes, a.CanAsk);

    private static AgentDetail ToDetail(Agent a, Team team)
        => new(a.Key, a.Name, a.Summary, a.OfficeRoles, a.Provider, a.Model, a.Effort, a.Includes, a.CanAsk, a.Prompt, a.ComposePrompt(team.Knowledge));
}
