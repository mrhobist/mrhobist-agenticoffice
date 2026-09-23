using System.Collections.Concurrent;
using System.Diagnostics;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>Bir ajanin cagrilacagi saglayici/model/efor: md frontmatter'i, bossa varsayilan (CLAUDE.md §4).</summary>
public sealed record AgentTarget(Provider Provider, string Model, string Effort, Destination Destination)
{
    public static AgentTarget Of(Agent agent) => Of(agent.Provider, agent.Model, agent.Effort);

    public static AgentTarget Of(Provider? provider, string? model, string? effort)
    {
        var p = provider ?? RunDefaults.Provider;
        return new AgentTarget(p, string.IsNullOrWhiteSpace(model) ? RunDefaults.Model : model, effort ?? Efforts.Default, RunDefaults.DestinationOf(p));
    }
}

/// <summary>
/// Tek bir LLM turunun sonucu; tur kaydi deposuna zaten yazilmistir. <see cref="InputTokens"/> /
/// <see cref="OutputTokens"/> calismanin toplamina eklenir (proje butcesi bunu okur, docs/DOMAIN.md → Butce ve limit);
/// tur basina kirilim <c>run_turn</c>'de kalir. Yeni alan SONA eklenir (CLAUDE.md §5).
/// </summary>
public sealed record AgentReply(
    string Text,
    string? StructuredJson,
    decimal CostUsd,
    IReadOnlyList<ToolUse> ToolUses,
    int InputTokens = 0,
    int OutputTokens = 0);

/// <summary>
/// Ajanin araclari ve calisma dizini (kullanici karari 2026-09-19: developer/testci dosyayi kendisi yazar, testi kendisi kosar).
/// Hangi adimin hangi araci aldigi <see cref="ForKind"/>'da; runtime yalniz iletir, yazma <see cref="Cwd"/> disina cikamaz.
/// </summary>
public sealed record ToolAccess(IReadOnlyList<string> Tools, string Cwd, int MaxTurns)
{
    public static readonly IReadOnlyList<string> ReadOnly = ["Read", "Glob", "Grep"];

    public static readonly IReadOnlyList<string> Full = ["Read", "Glob", "Grep", "Write", "Edit", "Bash"];

    /// <summary>
    /// Yurutme adimi mi: dosya yazar / komut kosar. Claude Code'un kendi kilavuzu bu adimlarda korunur
    /// (<see cref="SystemPromptModes.ClaudeCode"/>); plan ureten adimlarda korunmaz, cunku orada buyuk semayi
    /// doldurmayi bozdugu olculdu (bkz. <see cref="SystemPromptModes"/>).
    /// </summary>
    public bool IsExecution => Tools.Contains("Write", StringComparer.Ordinal);

    public string SystemPromptMode => IsExecution ? SystemPromptModes.ClaudeCode : SystemPromptModes.Replace;

    /// <summary>analyze/design: yalniz okuma (var olan kodu gorsun) · implement/review: tam · handoff: yok.</summary>
    public static ToolAccess? ForKind(Domain.Workflows.StageKind kind, string cwd) => kind switch
    {
        Domain.Workflows.StageKind.Analyze or Domain.Workflows.StageKind.Design => new ToolAccess(ReadOnly, cwd, 30),
        Domain.Workflows.StageKind.Implement => new ToolAccess(Full, cwd, 120),
        Domain.Workflows.StageKind.Review => new ToolAccess(Full, cwd, 80),
        _ => null,
    };
}

/// <summary>Otomatik tekrar ayari: gecici hatalarda (429, zaman asimi, ag, 5xx) <see cref="Attempts"/> deneme, artan bekleme.</summary>
public sealed record RetryPolicy(int Attempts, TimeSpan BaseDelay)
{
    public static readonly RetryPolicy Default = new(3, TimeSpan.FromSeconds(3));

    public static readonly RetryPolicy None = new(1, TimeSpan.Zero);

    public TimeSpan DelayFor(int attempt) => BaseDelay * Math.Pow(2, Math.Max(0, attempt - 1));
}

/// <summary>
/// Bir ajan adina LLM cagrisi: ekipten ajani bulur, prompt'u kurar, hassasiyet politikasini cagridan ONCE denetler,
/// runtime'i cagirir, turu calismanin tur kaydina yazar. Is kurali burada yok; yalniz "nasil cagrilir".
/// Ajan basina tek is (kullanici karari): ayni ajanin iki LLM cagrisi ayni anda kosmaz, ikincisi bekler.
/// Gecici hatalarda otomatik tekrar (docs/DOMAIN.md → Tekrar).
/// </summary>
public sealed class AgentCaller(IAgentStore agents, IAgentRuntimeService runtime, IRunStore runs, ISceneEventPublisher scene, RetryPolicy? retry = null, LimitGuard? limits = null, ProgressRegistry? progress = null)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AgentLocks = new(StringComparer.Ordinal);

    private readonly RetryPolicy _retry = retry ?? RetryPolicy.Default;

    /// <summary>Su anda LLM cagrisi icinde olan ajanlar (calismalar arasi "ajan basina tek is" icin).</summary>
    public static IReadOnlySet<string> BusyAgents
        => AgentLocks.Where(kv => kv.Value.CurrentCount == 0).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);

    public async Task<AgentReply> CallAsync(
        Run run,
        string agentKey,
        IReadOnlyList<RuntimeMessage> messages,
        string? schemaJson,
        string? stage,
        string? task,
        int? round,
        CancellationToken ct,
        ToolAccess? tools = null,
        ContextStats? context = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(messages);
        var team = await agents.LoadTeamAsync(ct).ConfigureAwait(false);
        if (!team.Agents.TryGetValue(agentKey, out var agent))
        {
            throw new DomainException(ErrorCodes.WorkflowUnknownRole, $"Ekipte '{agentKey}' diye bir ajan yok.");
        }

        var target = AgentTarget.Of(agent);
        if (!SensitivityPolicy.Allows(run.Sensitivity, target.Destination))
        {
            throw new DomainException(
                ErrorCodes.RunPolicyViolation,
                $"{agentKey}: hedef '{target.Destination}' calismanin hassasiyetine ({run.Sensitivity}) aykiri.");
        }

        var system = agent.ComposePrompt(team.Knowledge);
        // Canli arac akisi: yalniz aracli turda; belirtec tur boyunca yasar.
        var progressToken = tools is not null && progress?.BaseUrl is { } baseUrl ? progress.Register(new ProgressContext(run.Id, agentKey, task, stage)) : null;
        var progressUrl = progressToken is null ? null : $"{progress!.BaseUrl!.TrimEnd('/')}/{progressToken}";
        var request = new RuntimeTurnRequest(system, messages, target.Provider, target.Model, schemaJson, ReasoningEffort: target.Effort,
            Tools: tools?.Tools, Cwd: tools?.Cwd, MaxTurns: tools?.MaxTurns, ProgressUrl: progressUrl,
            SystemPromptMode: tools?.SystemPromptMode ?? SystemPromptModes.Replace);

        var gate = AgentLocks.GetOrAdd(agentKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Limit korumasi cagridan ONCE: esik asildiysa LimitReachedException; RunService calismayi bekletir.
            if (limits is not null)
            {
                await limits.CheckAsync(target.Provider, target.Model, ct).ConfigureAwait(false);
            }

            var response = await CallWithRetryAsync(run, agentKey, request, ct).ConfigureAwait(false);

            // Runtime'in soyledigi hedef de politikaya uymali: adaptor yanlis yere gittiyse burada yakalanir.
            if (!SensitivityPolicy.Allows(run.Sensitivity, response.Response.Destination))
            {
                throw new DomainException(
                    ErrorCodes.RunPolicyViolation,
                    $"{agentKey}: runtime icerigi '{response.Response.Destination}' hedefine goturdu; hassasiyet {run.Sensitivity}.");
            }

            var r = response.Response;
            var prompt = messages.Count == 0 ? "" : messages[^1].Content;
            var output = string.IsNullOrWhiteSpace(r.StructuredJson) ? r.Text : r.StructuredJson;
            var turn = new Turn(
                DateTimeOffset.UtcNow,
                agentKey,
                stage,
                task,
                round,
                Providers.Wire(r.Provider),
                r.Model,
                r.Destination,
                r.DurationS > 0 ? r.DurationS : response.Elapsed.TotalSeconds,
                system.Length + messages.Sum(m => m.Content.Length),
                output.Length,
                r.CostUsd,
                prompt,
                output,
                r.Usage.InputTokens,
                r.Usage.OutputTokens,
                r.ToolUses is { Count: > 0 } ? r.ToolUses.Select(t => new ToolUse(t.Tool, t.Target)).ToList() : null,
                r.Turns,
                r.Usage.CacheReadTokens,
                r.Usage.CacheWriteTokens,
                ToolsOffered: tools is not null,
                Context: context);
            await runs.AppendTurnAsync(run.Id, turn, ct).ConfigureAwait(false);

            return new AgentReply(r.Text, r.StructuredJson, r.CostUsd ?? 0m, turn.ToolUses ?? [], r.Usage.InputTokens, r.Usage.OutputTokens);
        }
        finally
        {
            if (progressToken is not null)
            {
                progress!.Release(progressToken);
            }

            gate.Release();
        }
    }

    private sealed record Attempted(RuntimeTurnResponse Response, TimeSpan Elapsed);

    private async Task<Attempted> CallWithRetryAsync(Run run, string agentKey, RuntimeTurnRequest request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var response = await runtime.TurnAsync(request, ct).ConfigureAwait(false);
                return new Attempted(response, sw.Elapsed);
            }
            catch (RuntimeLimitReachedException ex)
            {
                // Pencere cagri sirasinda doldu (LimitGuard'in 90 s'lik onbellegi bunu kaciriyor). Tekrar denemek
                // ANLAMSIZ ve pahali olurdu: sifirlanma dakikalar/gunler sonra. Akisin zaten bildigi bekleme
                // turune cevrilir -> calisma Paused + ResumeAt, RunResumer kaldigi adimdan surdurur (2026-09-22).
                // Sifirlanma zamani bu yoldan gelmiyor: null birakilir, PauseForLimitAsync 15 dk sonra bakar.
                throw new LimitReachedException(request.Provider, 100, 100, null, ex);
            }
            catch (Exception ex) when (attempt < _retry.Attempts && IsTransient(ex))
            {
                var wait = _retry.DelayFor(attempt);
                await runs.AppendMessageAsync(
                    run.Id,
                    new Message(DateTimeOffset.UtcNow, MessageKind.Note, agentKey, "user", $"geçici hata, tekrar {attempt + 1}/{_retry.Attempts} ({wait.TotalSeconds:0} s sonra): {ex.Message}", Subject: "retry"),
                    ct).ConfigureAwait(false);
                scene.Publish(SceneEventTypes.AgentState, $$"""{"agent":"{{agentKey}}","state":"waiting","note":"yeniden deneniyor {{attempt + 1}}/{{_retry.Attempts}}","run":"{{run.Id}}"}""");
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Gecici: runtime kapali, 429, 5xx, zaman asimi, ag. Kalici (sema, politika, 4xx) hemen durur.</summary>
    public static bool IsTransient(Exception ex)
    {
        if (ex is RuntimeUnavailableException or TimeoutException)
        {
            return true;
        }

        if (ex is not InvalidOperationException)
        {
            return false;
        }

        var m = ex.Message;
        return m.Contains("429", StringComparison.Ordinal)
            || m.Contains("HTTP 5", StringComparison.Ordinal)
            || m.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || m.Contains("zaman", StringComparison.OrdinalIgnoreCase)
            || m.Contains("rate", StringComparison.OrdinalIgnoreCase)
            || m.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
            || m.Contains("ulasilamadi", StringComparison.OrdinalIgnoreCase);
    }
}
