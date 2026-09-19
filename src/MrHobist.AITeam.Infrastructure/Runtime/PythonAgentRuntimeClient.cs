using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Infrastructure.Runtime;

/// <summary>
/// Python runtime'in tipli HttpClient adaptoru (<c>runtime/app/contracts.py</c> ile birebir).
/// Saglayici DTO'lari, URL'ler ve protokol ayrintilari yalniz burada. Baglanti kurulamazsa
/// <see cref="RuntimeUnavailableException"/>; Api bunu 503 <c>runtime.unavailable</c> yapar.
/// </summary>
public sealed class PythonAgentRuntimeClient(HttpClient http) : IAgentRuntimeService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record TurnDto(
        string SystemPrompt,
        IReadOnlyList<MessageDto> Messages,
        string Provider,
        string Model,
        JsonElement? Schema,
        int MaxTokens,
        string? ReasoningEffort);

    private sealed record MessageDto(string Role, string Content);

    private sealed record UsageDto(int InputTokens, int OutputTokens, int ReasoningChars);

    private sealed record TurnResultDto(
        string Text,
        JsonElement? Structured,
        string Provider,
        string Model,
        string Destination,
        UsageDto? Usage,
        decimal? CostUsd,
        double DurationS,
        int Attempts);

    private sealed record ModelDto(string Provider, string Model, bool Reachable, string? Detail);

    public async Task<RuntimeTurnResponse> TurnAsync(RuntimeTurnRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dto = new TurnDto(
            request.SystemPrompt,
            request.Messages.Select(m => new MessageDto(m.Role, m.Content)).ToList(),
            Providers.Wire(request.Provider),
            request.Model,
            request.SchemaJson is null ? null : JsonDocument.Parse(request.SchemaJson).RootElement,
            request.MaxTokens,
            request.ReasoningEffort);

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("/v1/turn", dto, Json, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new RuntimeUnavailableException($"runtime'a ulasilamadi: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"runtime /v1/turn HTTP {(int)response.StatusCode}: {Clip(body)}");
        }

        var result = await response.Content.ReadFromJsonAsync<TurnResultDto>(Json, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("runtime /v1/turn bos govde dondu.");

        return new RuntimeTurnResponse(
            result.Text,
            result.Structured?.GetRawText(),
            ParseProvider(result.Provider),
            result.Model,
            Enum.Parse<Destination>(result.Destination, ignoreCase: true),
            new RuntimeUsage(result.Usage?.InputTokens ?? 0, result.Usage?.OutputTokens ?? 0, result.Usage?.ReasoningChars ?? 0),
            result.CostUsd,
            result.DurationS,
            result.Attempts);
    }

    public async Task<IReadOnlyList<RuntimeModelInfo>> ListModelsAsync(Provider? provider, CancellationToken ct)
    {
        var url = provider is null ? "/v1/models" : $"/v1/models?provider={Providers.Wire(provider.Value)}";
        IReadOnlyList<ModelDto>? models;
        try
        {
            models = await http.GetFromJsonAsync<IReadOnlyList<ModelDto>>(url, Json, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new RuntimeUnavailableException($"runtime'a ulasilamadi: {ex.Message}");
        }

        return (models ?? []).Select(m => new RuntimeModelInfo(ParseProvider(m.Provider), m.Model, m.Reachable, m.Detail ?? "")).ToList();
    }

    /// <summary>Runtime'in dondugu ad; bos donmez, bilinmeyen ad sozlesme hatasidir (500).</summary>
    private static Provider ParseProvider(string s)
        => Providers.Parse(s) ?? throw new InvalidOperationException("runtime bos provider dondu.");

    private static string Clip(string s) => s.Length <= 300 ? s : s[..300];
}
