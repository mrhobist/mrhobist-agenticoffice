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

public enum RunStatus
{
    Running,
    Completed,
    Failed,
    Interrupted,
    BudgetExceeded,
    PolicyRejected,
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

/// <summary>Calisma ustverisi: <c>runs/{Id}/run.json</c>.</summary>
public sealed record Run(
    string Id,
    string Label,
    string Brief,
    Sensitivity Sensitivity,
    DateTimeOffset StartedAt,
    RunStatus Status,
    DateTimeOffset? FinishedAt = null,
    decimal TotalCostUsd = 0m);

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
    string? Output = null);

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
