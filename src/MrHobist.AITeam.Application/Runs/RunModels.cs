using MrHobist.AITeam.Application.Workflows;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>
/// <c>POST /runs</c> govdesi. <see cref="Workflow"/> yoksa <c>default</c>, <see cref="Sensitivity"/> yoksa <c>anthropic</c>,
/// <see cref="MaxCostUsd"/> yoksa butce siniri yok.
/// </summary>
public sealed record RunRequest(string Brief, string? Workflow = null, Sensitivity? Sensitivity = null, string? Label = null, decimal? MaxCostUsd = null, string? Project = null);

/// <summary><c>POST /runs/{id}/revise</c> govdesi.</summary>
public sealed record ReviseRequest(string Note);

/// <summary><c>POST /runs/{id}/answer</c> govdesi: <see cref="Choice"/> sorunun seceneklerinden birinin kimligi; <see cref="Note"/> secenek isterse zorunlu.</summary>
public sealed record AnswerRequest(string Choice, string? Note = null);

/// <summary>"Yeniden dene" hangi adimdan surer: plan yoksa/analiz dustuyse analiz, yoksa dagitim.</summary>
public enum RetryStep
{
    Analyze,
    Dispatch,
}

public sealed record RetryResult(Run Run, RetryStep Step);

/// <summary>Bir gorevin faz kayitlari (<c>tasks/{task}/phases.jsonl</c>).</summary>
public sealed record TaskPhases(string Id, IReadOnlyList<Phase> Phases);

/// <summary>
/// <c>GET /runs/{id}</c>: <see cref="Run"/> alanlari + dondurulan akis + plan + kayitlar (docs/API.md).
/// Duz tutulur ki UI tek nesneyi okusun.
/// </summary>
public sealed record RunDetail(
    string Id,
    string Label,
    string Brief,
    Sensitivity Sensitivity,
    string Workflow,
    RunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    decimal TotalCostUsd,
    string? Detail,
    decimal? MaxCostUsd,
    int Retries,
    string Project,
    string OwnerId,
    UserQuestion? Question,
    DateTimeOffset? ResumeAt,
    WorkflowDetail? WorkflowDef,
    Spec? Spec,
    IReadOnlyList<string> Order,
    IReadOnlyList<TaskPhases> Tasks,
    IReadOnlyList<Message> Messages);

/// <summary>Bos birakilan ajan alanlari icin varsayilanlar (docs/DOMAIN.md → Model, efor ve kimlik; kullanici karari).</summary>
public static class RunDefaults
{
    public const string Model = "claude-opus-5";

    public const Domain.Agents.Provider Provider = Domain.Agents.Provider.Anthropic;

    public const Sensitivity Sensitivity = Domain.Runs.Sensitivity.Anthropic;

    /// <summary>Bir saglayicinin icerigi fiilen goturdugu yer; politika cagridan ONCE buna bakar.</summary>
    public static Destination DestinationOf(Domain.Agents.Provider provider) => provider switch
    {
        Domain.Agents.Provider.Anthropic => Destination.Anthropic,
        Domain.Agents.Provider.Nvidia => Destination.Nvidia,
        Domain.Agents.Provider.Ollama => Destination.Local,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "bilinmeyen saglayici"),
    };
}

/// <summary>Bir ajanin bir calismadaki isi: turlari (tam prompt/cikti, o anki model) ve ona gelen/giden mesajlar. Ajan paneli "Isler" sekmesi.</summary>
public sealed record AgentRunWork(Run Run, IReadOnlyList<Turn> Turns, IReadOnlyList<Message> Messages, IReadOnlyList<Phase> Phases);

/// <summary>Kullanicidan ne bekleniyor. JSON'da adiyla tasinir; yeni uye SONA eklenir (CLAUDE.md §5).</summary>
public enum InboxKind
{
    /// <summary>Soru: plan onayi (onayla ya da revize notu yaz).</summary>
    Approval,
    /// <summary>Karar: calisma durdu (durakladi / dustu / kesildi) — yeniden dene ya da iptal et.</summary>
    Decision,
    /// <summary>Soru: bir ajan kullaniciya <c>ask</c> yazdi ve cevap yok.</summary>
    Question,
}

/// <summary>Gelen kutusu satiri: bir calismanin kullanicidan bekledigi tek sey (docs/DOMAIN.md → Gelen kutusu).</summary>
public sealed record InboxItem(
    string RunId,
    string Label,
    RunStatus Status,
    InboxKind Kind,
    DateTimeOffset Ts,
    string Title,
    string? Detail,
    string? Task);

/// <summary>
/// <c>GET /runs/overview</c>: kac is var, kaci ne durumda, kaci kullanicidan bir sey bekliyor.
/// <see cref="Failed"/> tum dusme turlerini toplar (Failed, Interrupted, BudgetExceeded, PolicyRejected).
/// </summary>
public sealed record RunsOverview(
    int Total,
    int Running,
    int AwaitingApproval,
    int Paused,
    int Failed,
    int Completed,
    int Cancelled,
    IReadOnlyList<InboxItem> Inbox,
    /// <summary>Takilip kullanicidan secim bekleyen calismalar (AwaitingInput). Sona eklendi (CLAUDE.md §5).</summary>
    int AwaitingInput = 0);
