using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Agents;
using MrHobist.AITeam.Application.Runs;
using MrHobist.AITeam.Application.Workflows;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.Api.Config;

/// <summary><c>GET /api/v1/providers</c> ogesi: kimlik + o saglayicinin modelleri. <see cref="Method"/>: <c>session | apikey | null</c>.</summary>
public sealed record ProviderStatus(Provider Provider, bool LoggedIn, string? Account, string Detail, IReadOnlyList<RuntimeModelInfo> Models, string? Method = null);

/// <summary>Ajan, bilgi, model ve is akisi uclari (docs/API.md). Ince adaptor: is kurali Application'da.</summary>
public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfig(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1");

        g.MapGet("/agents", (IAgentService s, CancellationToken ct) => s.ListAsync(ct));
        g.MapPost("/agents", async (CreateAgentRequest body, IAgentService s, CancellationToken ct)
            => Results.Created($"/api/v1/agents/{body.Key}", await s.CreateAsync(body, ct).ConfigureAwait(false)));
        g.MapGet("/agents/{key}", (string key, IAgentService s, CancellationToken ct) => s.GetAsync(key, ct));
        g.MapPut("/agents/{key}", (string key, UpdateAgentRequest body, IAgentService s, CancellationToken ct) => s.UpdateAsync(key, body, ct));
        g.MapDelete("/agents/{key}", async (string key, IAgentService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(key, ct).ConfigureAwait(false);
            return Results.NoContent();
        });
        g.MapGet("/knowledge", (IAgentService s, CancellationToken ct) => s.ListKnowledgeAsync(ct));

        // Ajan paneli "Isler" sekmesi: bu ajanin calisma basina turlari (tam prompt/cikti, o anki model), mesajlari, fazlari.
        g.MapGet("/agents/{key}/work", (string key, int? runs, IRunReader reader, CancellationToken ct)
            => reader.GetAgentWorkAsync(key, runs ?? 30, ct));

        // Sorgu parametresi tel adiyla gelir ("nvidia"); enum baglayici buyuk/kucuk harfe duyarli oldugu icin metin alinir.
        g.MapGet("/models", (string? provider, IAgentRuntimeService runtime, CancellationToken ct)
            => runtime.ListModelsAsync(Providers.Parse(provider), ct));

        // UI ilk yuklemede bakar: giris var mi, hangi modeller (docs/DOMAIN.md → Model, efor ve kimlik).
        g.MapGet("/providers", async (bool? refresh, IAgentRuntimeService runtime, CancellationToken ct) =>
        {
            var auth = await runtime.ListAuthAsync(null, refresh ?? false, ct).ConfigureAwait(false);
            var models = await runtime.ListModelsAsync(null, ct).ConfigureAwait(false);
            return auth.Select(a => new ProviderStatus(
                a.Provider, a.LoggedIn, a.Account, a.Detail,
                models.Where(m => m.Provider == a.Provider).ToList(), a.Method)).ToList();
        });

        // Tek tikla giris: runtime, saglayicinin kendi giris akisini kullanicinin makinesinde baslatir (yeni konsol + tarayici).
        // Sifre/token bu uclardan GECMEZ (docs/DOMAIN.md → Model, efor ve kimlik). Istisna `apikey` modu: anahtar runtime'a
        // iletilir, runtime dogrulayip kullanici profiline yazar; Api saklamaz, gunluklemez, yanita yazmaz.
        g.MapPost("/providers/{provider}/login", (string provider, ProviderLoginRequest? body, IAgentRuntimeService runtime, CancellationToken ct)
            => runtime.LoginAsync(RequireProvider(provider), body?.Mode ?? "claudeai", body?.Email, body?.ApiKey, ct));
        g.MapPost("/providers/{provider}/logout", (string provider, IAgentRuntimeService runtime, CancellationToken ct)
            => runtime.LogoutAsync(RequireProvider(provider), ct));

        // Kalan kullanim (ust bar): saglayicinin kota pencereleri; runtime 30 s onbellekler.
        g.MapGet("/limits", (bool? refresh, IAgentRuntimeService runtime, CancellationToken ct)
            => runtime.ListLimitsAsync(null, refresh ?? false, ct));

        // Kullanim: bizim kayitlarimizdan (runs/ turlari), saglayici+model bazinda.
        g.MapGet("/usage", (int? runs, IUsageReader usage, CancellationToken ct) => usage.SummarizeAsync(runs ?? 200, ct));

        // Calisma alani ayarlari (config/settings.json): saglayici basina limit korumasi esigi (docs/DOMAIN.md → Butce ve limit).
        g.MapGet("/settings", async (ISettingsStore s, CancellationToken ct) => SettingsDto.From(await s.LoadAsync(ct).ConfigureAwait(false)));
        g.MapPut("/settings", async (SettingsDto body, ISettingsStore s, CancellationToken ct) =>
        {
            var settings = body.ToDomain();
            await s.SaveAsync(settings, ct).ConfigureAwait(false);
            return SettingsDto.From(settings);
        });

        g.MapGet("/workflows", (IWorkflowService s, CancellationToken ct) => s.ListAsync(ct));
        g.MapGet("/workflows/{key}", (string key, IWorkflowService s, CancellationToken ct) => s.GetAsync(key, ct));
        g.MapPut("/workflows/{key}", (string key, WorkflowModel body, IWorkflowService s, CancellationToken ct) => s.UpsertAsync(key, body, ct));
        g.MapDelete("/workflows/{key}", async (string key, IWorkflowService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(key, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        return app;
    }

    private static Provider RequireProvider(string text)
        => Providers.Parse(text) ?? throw new DomainException(ErrorCodes.AgentInvalidProvider, "provider bos.");
}

/// <summary>
/// <c>POST /providers/{provider}/login</c> govdesi. <c>mode</c>: Anthropic <c>claudeai</c> (abonelik) | <c>console</c> (Console OAuth) | <c>apikey</c>;
/// OpenAI <c>chatgpt</c> (Codex CLI oturumu) | <c>apikey</c>. <c>apiKey</c> yalniz <c>apikey</c> modunda.
/// </summary>
// Ad `ProviderLoginRequest`: Auth/LoginRequest (kullanici adi + sifre) ile OpenAPI semasinda cakismasin (gen:api).
public sealed record ProviderLoginRequest(string? Mode, string? Email, string? ApiKey = null);

/// <summary><c>GET/PUT /settings</c> govdesi: <c>{ limitGuards: { anthropic: 99 } }</c>. Sozlesmede saglayici adi kucuk harf.</summary>
public sealed record SettingsDto(Dictionary<string, int> LimitGuards)
{
    public static SettingsDto From(Domain.Settings.AppSettings s)
        => new(s.LimitGuards.ToDictionary(kv => Domain.Agents.Providers.Wire(kv.Key), kv => kv.Value));

    public Domain.Settings.AppSettings ToDomain()
    {
        var guards = new Dictionary<Domain.Agents.Provider, int>();
        foreach (var (key, value) in LimitGuards ?? [])
        {
            var p = Domain.Agents.Providers.Parse(key) ?? throw new Domain.DomainException(Domain.ErrorCodes.SettingsInvalid, $"bilinmeyen saglayici: '{key}'");
            guards[p] = value;
        }

        return new Domain.Settings.AppSettings(guards);
    }
}
