using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Application.Abstractions;

/// <summary>Bir LLM cagrisinin tamami. Gecmis <see cref="Messages"/> ile gider; runtime hicbir sey hatirlamaz.</summary>
public sealed record RuntimeTurnRequest(
    string SystemPrompt,
    IReadOnlyList<RuntimeMessage> Messages,
    Provider Provider,
    string Model,
    string? SchemaJson = null,
    int MaxTokens = 8192,
    string? ReasoningEffort = "low");

public sealed record RuntimeMessage(string Role, string Content);

public sealed record RuntimeUsage(int InputTokens, int OutputTokens, int ReasoningChars);

public sealed record RuntimeTurnResponse(
    string Text,
    string? StructuredJson,
    Provider Provider,
    string Model,
    Destination Destination,
    RuntimeUsage Usage,
    decimal? CostUsd,
    double DurationS,
    int Attempts);

/// <summary>Katalogda gorunmek erisilebilir olmak DEGILDIR; <see cref="Reachable"/> fiilen cagirarak dogrulanir.</summary>
public sealed record RuntimeModelInfo(Provider Provider, string Model, bool Reachable, string Detail);

/// <summary>
/// Python runtime sozlesmesi (CLAUDE.md §1). Tek isi LLM cagrisi; is kurali burada yoktur.
/// Sahte adaptorle <c>ServiceTests</c> Python olmadan gecer.
/// </summary>
public interface IAgentRuntimeService
{
    Task<RuntimeTurnResponse> TurnAsync(RuntimeTurnRequest request, CancellationToken ct);

    Task<IReadOnlyList<RuntimeModelInfo>> ListModelsAsync(Provider? provider, CancellationToken ct);
}

/// <summary>Runtime'a ulasilamiyor: Api 503 <c>runtime.unavailable</c> doner.</summary>
public sealed class RuntimeUnavailableException(string message) : Exception(message);
