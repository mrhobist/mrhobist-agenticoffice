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
        string? ReasoningEffort,
        IReadOnlyList<string>? Tools,
        string? Cwd,
        int? MaxTurns,
        string? ProgressUrl);

    private sealed record MessageDto(string Role, string Content);

    private sealed record ToolUseDto(string Tool, string? Target);

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
        int Attempts,
        IReadOnlyList<ToolUseDto>? ToolUses,
        int? Turns);

    private sealed record ModelDto(string Provider, string Model, bool Reachable, string? Detail);

    private sealed record AuthDto(string Provider, bool LoggedIn, string? Account, string? Detail, string? Method);

    private sealed record LoginDto(string Provider, string Mode, string? Email, string? ApiKey);

    private sealed record LoginStartedDto(string Provider, bool Started, string? Detail);

    private sealed record LogoutDto(string Provider);

    private sealed record LimitDto(string Kind, string? Group, double Percent, string? Severity, DateTimeOffset? ResetsAt, string? Scope, bool IsActive);

    private sealed record LimitsDto(string Provider, bool Available, string? Detail, string? Subscription, DateTimeOffset? FetchedAt, IReadOnlyList<LimitDto>? Limits);

    public async Task<IReadOnlyList<RuntimeProviderLimits>> ListLimitsAsync(Provider? provider, bool refresh, CancellationToken ct)
    {
        var query = new List<string>();
        if (provider is not null)
        {
            query.Add($"provider={Providers.Wire(provider.Value)}");
        }

        if (refresh)
        {
            query.Add("refresh=true");
        }

        var url = query.Count == 0 ? "/v1/limits" : "/v1/limits?" + string.Join('&', query);
        IReadOnlyList<LimitsDto>? items;
        try
        {
            items = await http.GetFromJsonAsync<IReadOnlyList<LimitsDto>>(url, Json, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            // Runtime ayakta ama kota ucu patladi: "kapali" degil, "bozuk". UI ikisini ayri soyler (LESSONS: yanlis teshis).
            throw new RuntimeErrorException($"runtime /v1/limits HTTP {(int)ex.StatusCode}");
        }
        catch (HttpRequestException ex)
        {
            throw new RuntimeUnavailableException($"runtime'a ulasilamadi: {ex.Message}");
        }

        return (items ?? []).Select(l => new RuntimeProviderLimits(
            ParseProvider(l.Provider), l.Available, l.Detail ?? "", l.Subscription, l.FetchedAt,
            (l.Limits ?? []).Select(x => new RuntimeUsageLimit(x.Kind, x.Group, x.Percent, x.Severity, x.ResetsAt, x.Scope, x.IsActive)).ToList())).ToList();
    }

    public async Task<RuntimeLoginStarted> LoginAsync(Provider provider, string mode, string? email, string? apiKey, CancellationToken ct)
    {
        var result = await PostAsync<LoginDto, LoginStartedDto>("/v1/auth/login", new LoginDto(Providers.Wire(provider), mode, email, apiKey), ct).ConfigureAwait(false);
        return new RuntimeLoginStarted(ParseProvider(result.Provider), result.Started, result.Detail ?? "");
    }

    public async Task<RuntimeAuthStatus> LogoutAsync(Provider provider, CancellationToken ct)
    {
        var result = await PostAsync<LogoutDto, AuthDto>("/v1/auth/logout", new LogoutDto(Providers.Wire(provider)), ct).ConfigureAwait(false);
        return new RuntimeAuthStatus(ParseProvider(result.Provider), result.LoggedIn, result.Account, result.Detail ?? "", result.Method);
    }

    private async Task<TResult> PostAsync<TBody, TResult>(string path, TBody body, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(path, body, Json, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new RuntimeUnavailableException($"runtime'a ulasilamadi: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException(DescribeError((int)response.StatusCode, text));
            }

            return await response.Content.ReadFromJsonAsync<TResult>(Json, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"runtime {path} bos govde dondu.");
        }
    }

    public async Task<IReadOnlyList<RuntimeAuthStatus>> ListAuthAsync(Provider? provider, bool refresh, CancellationToken ct)
    {
        var query = new List<string>();
        if (provider is not null)
        {
            query.Add($"provider={Providers.Wire(provider.Value)}");
        }

        if (refresh)
        {
            query.Add("refresh=true");
        }

        var url = query.Count == 0 ? "/v1/auth" : "/v1/auth?" + string.Join('&', query);
        IReadOnlyList<AuthDto>? items;
        try
        {
            items = await http.GetFromJsonAsync<IReadOnlyList<AuthDto>>(url, Json, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new RuntimeUnavailableException($"runtime'a ulasilamadi: {ex.Message}");
        }

        return (items ?? []).Select(a => new RuntimeAuthStatus(ParseProvider(a.Provider), a.LoggedIn, a.Account, a.Detail ?? "", a.Method)).ToList();
    }

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
            request.ReasoningEffort,
            request.Tools is { Count: > 0 } ? request.Tools : null,
            request.Cwd,
            request.MaxTurns,
            request.ProgressUrl);

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
            throw new InvalidOperationException(DescribeError((int)response.StatusCode, body));
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
            result.Attempts,
            result.ToolUses?.Select(t => new RuntimeToolUse(t.Tool, t.Target)).ToList(),
            result.Turns ?? 1);
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

    /// <summary>Runtime hatasi <c>{"detail":{"errorCode","message"}}</c> gelir; insan icin tek satira indirilir.</summary>
    private static string DescribeError(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Object)
            {
                var code = detail.TryGetProperty("errorCode", out var c) ? c.GetString() : null;
                var message = detail.TryGetProperty("message", out var m) ? m.GetString() : null;
                if (code is not null)
                {
                    return $"runtime {code}: {Clip(message ?? "")}";
                }
            }
        }
        catch (JsonException)
        {
            // Govde JSON degil; oldugu gibi kirp.
        }

        return $"runtime /v1/turn HTTP {status}: {Clip(body)}";
    }

    private static string Clip(string s) => s.Length <= 300 ? s : s[..300];
}
