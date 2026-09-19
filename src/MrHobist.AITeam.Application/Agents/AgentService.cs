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
    IReadOnlyList<string> Includes,
    string? CanAsk);

public sealed record AgentDetail(
    string Key,
    string Name,
    string Summary,
    IReadOnlyList<string> OfficeRoles,
    Provider? Provider,
    string? Model,
    IReadOnlyList<string> Includes,
    string? CanAsk,
    string Prompt,
    string ComposedPrompt);

/// <summary>PUT govdesi; anahtar yoldan gelir. <see cref="Provider"/> metin gelir ki bilinmeyen deger
/// JSON hatasi degil <c>agent.invalid_provider</c> olsun ('claude' sessizce cevrilmez).</summary>
public sealed record UpdateAgentRequest(
    string Name,
    string Summary,
    IReadOnlyList<string>? OfficeRoles,
    string? Provider,
    string? Model,
    IReadOnlyList<string>? Includes,
    string? CanAsk,
    string Prompt);

public sealed record KnowledgeItem(string Key, string Title, string Body);

public interface IAgentService
{
    Task<IReadOnlyList<AgentListItem>> ListAsync(CancellationToken ct);

    Task<AgentDetail> GetAsync(string key, CancellationToken ct);

    /// <summary>Ekibi butun olarak dogrular (eksik include, gecersiz can_ask), sonra atomik yazar.</summary>
    Task<AgentDetail> UpdateAsync(string key, UpdateAgentRequest request, CancellationToken ct);

    Task<IReadOnlyList<KnowledgeItem>> ListKnowledgeAsync(CancellationToken ct);

    /// <summary>Modele fiilen giden metin: govde + alt md'ler. RunService bunu kullanir.</summary>
    Task<string> ComposePromptAsync(string key, CancellationToken ct);
}

public sealed class AgentService(IAgentStore store) : IAgentService
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

    public async Task<AgentDetail> UpdateAsync(string key, UpdateAgentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var team = await store.LoadTeamAsync(ct).ConfigureAwait(false);
        var current = Find(team, key);

        var updated = current with
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? current.Name : request.Name.Trim(),
            Summary = request.Summary?.Trim() ?? "",
            OfficeRoles = request.OfficeRoles ?? current.OfficeRoles,
            Provider = ParseProvider(request.Provider),
            Model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim(),
            Includes = request.Includes ?? [],
            CanAsk = string.IsNullOrWhiteSpace(request.CanAsk) ? null : request.CanAsk.Trim(),
            Prompt = request.Prompt ?? "",
        };

        // Once ekip butunu dogrulanir: yazilan dosya bir sonraki yuklemede patlamamali.
        var agents = new Dictionary<string, Agent>(team.Agents, StringComparer.Ordinal) { [key] = updated };
        var next = team with { Agents = agents };
        next.Validate();

        await store.SaveAgentAsync(updated, ct).ConfigureAwait(false);
        return ToDetail(updated, next);
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

    private static Provider? ParseProvider(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return Enum.TryParse<Provider>(text.Trim(), ignoreCase: true, out var p)
            ? p
            : throw new DomainException(ErrorCodes.AgentInvalidProvider, $"Bilinmeyen provider '{text}' (anthropic | nvidia | ollama).");
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
        => new(a.Key, a.Name, a.Summary, a.OfficeRoles, a.Provider, a.Model, a.Includes, a.CanAsk);

    private static AgentDetail ToDetail(Agent a, Team team)
        => new(a.Key, a.Name, a.Summary, a.OfficeRoles, a.Provider, a.Model, a.Includes, a.CanAsk, a.Prompt, a.ComposePrompt(team.Knowledge));
}
