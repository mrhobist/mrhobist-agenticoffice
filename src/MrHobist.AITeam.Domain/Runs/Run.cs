namespace MrHobist.AITeam.Domain.Runs;

/// <summary>Icerigin fiilen ulastigi yer. Her tur bununla kaydedilir (CLAUDE.md §4).</summary>
public enum Destination
{
    Local,
    Anthropic,
    Nvidia,
}

/// <summary>Bir calismanin hassasiyeti: icerik hangi hedeflere cikabilir.</summary>
public enum Sensitivity
{
    /// <summary>Yalniz bu makine; hicbir LLM'e cikmaz.</summary>
    Local,
    /// <summary>Anthropic'e cikabilir, NVIDIA'ya cikmaz.</summary>
    Anthropic,
    /// <summary>Ucuncu taraflara da cikabilir.</summary>
    Open,
}

/// <summary>Calisma durumu. JSON'da adiyla tasinir; yeni uye SONA eklenir (CLAUDE.md §5). Gecisler docs/DOMAIN.md.</summary>
public enum RunStatus
{
    Running,
    Completed,
    Failed,
    Interrupted,
    BudgetExceeded,
    PolicyRejected,
    /// <summary>Analist plani uretti; insan onayi (approve) ya da revize notu bekleniyor. Panoya is acilmadi.</summary>
    AwaitingApproval,
    /// <summary>Yurutucusu olmayan bir adima gelindi; hata degil, teslim siniri. <see cref="Run.Detail"/> adimi soyler.</summary>
    Paused,
    /// <summary>Kullanici durdurdu. Bitmis sayilir; "Yeniden dene" ile kaldigi adimdan devam edebilir.</summary>
    Cancelled,
}

public enum PhaseStatus
{
    Started,
    Done,
    Rejected,
    Failed,
    Skipped,
}

public enum MessageKind
{
    Ask,
    Answer,
    Handoff,
    Note,
}

/// <summary>Politikaya aykiri tek bir rol varsa calisma hic baslamaz.</summary>
public static class SensitivityPolicy
{
    public static bool Allows(Sensitivity sensitivity, Destination destination) => sensitivity switch
    {
        Sensitivity.Local => destination == Destination.Local,
        Sensitivity.Anthropic => destination is Destination.Local or Destination.Anthropic,
        Sensitivity.Open => true,
        _ => false,
    };
}

/// <summary>
/// Calisma ustverisi: <c>runs/{Id}/run.json</c>. <see cref="Workflow"/> baslarken secilen akisin anahtaridir;
/// tanimin kendisi <c>runs/{Id}/workflow.json</c> olarak dondurulur (docs/DOMAIN.md). <see cref="Detail"/>
/// durumun insan icin kisa aciklamasi (Paused: hangi adim, Failed: neden). <see cref="MaxCostUsd"/> asildiginda
/// calisma <see cref="RunStatus.BudgetExceeded"/> ile durur (CLAUDE.md §4). <see cref="Retries"/> "Yeniden dene" sayisi.
/// </summary>
public sealed record Run(
    string Id,
    string Label,
    string Brief,
    Sensitivity Sensitivity,
    DateTimeOffset StartedAt,
    RunStatus Status,
    DateTimeOffset? FinishedAt = null,
    decimal TotalCostUsd = 0m,
    string Workflow = "default",
    string? Detail = null,
    decimal? MaxCostUsd = null,
    int Retries = 0)
{
    /// <summary>
    /// Kullanicinin durdurabilecegi ya da "kapat" diyebilecegi durumlar. Dusen calismalar (Failed/Interrupted/BudgetExceeded)
    /// da iptal edilir: yoksa "yeniden dene ya da vazgec" kararinin ikinci sikki olmaz ve is gelen kutusundan hic dusmez.
    /// </summary>
    public bool IsCancellable => Status is RunStatus.Running or RunStatus.AwaitingApproval or RunStatus.Paused
        or RunStatus.Failed or RunStatus.Interrupted or RunStatus.BudgetExceeded;

    /// <summary>"Yeniden dene" ile kaldigi adimdan devam edebilecek durumlar.</summary>
    public bool IsRetryable => Status is RunStatus.Failed or RunStatus.Interrupted or RunStatus.BudgetExceeded or RunStatus.Cancelled;
}

/// <summary>Analistin urettigi gorev; <see cref="DependsOn"/> gercek bagimliliklar.</summary>
public sealed record RunTask(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Acceptance,
    IReadOnlyList<string> DependsOn);

/// <summary>Analist ciktisi: <c>runs/{id}/spec.json</c>.</summary>
public sealed record Spec(string Summary, string Architecture, IReadOnlyList<string> Rules, IReadOnlyList<RunTask> Tasks);

/// <summary>Bir gorevin bir fazi: <c>runs/{id}/tasks/{task}/phases.jsonl</c>.</summary>
public sealed record Phase(
    DateTimeOffset Ts,
    string Task,
    string Stage,
    string StageTitle,
    string Kind,
    string Agent,
    int Round,
    PhaseStatus Status,
    double? DurationS = null,
    string? Detail = null);

/// <summary>Bir ajanin tek bir LLM cagrisi: <c>runs/{id}/conversations/{agent}.jsonl</c>. Tam metinler ayri alanlarda.</summary>
public sealed record Turn(
    DateTimeOffset Ts,
    string Agent,
    string? Stage,
    string? Task,
    int? Round,
    string Provider,
    string Model,
    Destination Destination,
    double DurationS,
    int PromptChars,
    int OutputChars,
    decimal? CostUsd = null,
    string? Prompt = null,
    string? Output = null,
    int? InputTokens = null,
    int? OutputTokens = null);

/// <summary>Ajanlar arasi mesaj: <c>runs/{id}/messages.jsonl</c>. <see cref="Ref"/> ask ile answer'i esler.</summary>
public sealed record Message(
    DateTimeOffset Ts,
    MessageKind Kind,
    string From,
    string To,
    string Body,
    string? Task = null,
    string? Stage = null,
    string? Ref = null,
    string Subject = "");
