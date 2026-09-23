using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Infrastructure.Persistence;

/// <summary>
/// Kalicilik satirlari. Domain kayitlari (record) degismez ve EF gormez (ARCHITECTURE.md §8: Domain'de EF yok);
/// bu siniflar yalniz sema ile Domain arasindaki cevirinin durak noktasidir.
/// Kural: buyuk/degisken yuk <c>Data</c> icinde JSON; sutunlar yalniz SUZME ve TOPLAMA icindir.
/// Append tablolarinda sira <c>Id</c>'dir (AUTOINCREMENT, monoton); ayri bir sayac tutulmaz.
/// Okuma daima <c>Data</c>'dan nesneyi kurar -- sutun ile JSON caliserse dogru olan JSON'dur.
/// </summary>
internal sealed class ProjectRow
{
    public string Key { get; set; } = "";

    public string Title { get; set; } = "";

    public string Description { get; set; } = "";

    public string Workflow { get; set; } = "";

    public string TargetDir { get; set; } = "";

    public string OwnerId { get; set; } = "";

    public string Color { get; set; } = "";

    public int SortOrder { get; set; }

    /// <summary>Proje butcesi: $ tavani. <c>null</c> = sinirsiz.</summary>
    public decimal? MaxCostUsd { get; set; }

    /// <summary>Proje butcesi: token tavani (girdi + cikti). <c>null</c> = sinirsiz.</summary>
    public long? MaxTokens { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class RunRow
{
    public string Id { get; set; } = "";

    public string ProjectKey { get; set; } = "";

    public string Label { get; set; } = "";

    public string Brief { get; set; } = "";

    public Sensitivity Sensitivity { get; set; }

    public RunStatus Status { get; set; }

    public RunStep? Step { get; set; }

    public string WorkflowKey { get; set; } = "";

    public string OwnerId { get; set; } = "";

    public string? Detail { get; set; }

    /// <summary>JSON: <see cref="UserQuestion"/>.</summary>
    public string? Question { get; set; }

    public decimal TotalCostUsd { get; set; }

    public decimal? MaxCostUsd { get; set; }

    /// <summary>Calismanin girdi token toplami (tur basina kirilim <c>run_turn</c>'de).</summary>
    public long InputTokens { get; set; }

    /// <summary>Calismanin cikti token toplami.</summary>
    public long OutputTokens { get; set; }

    public int Retries { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public DateTimeOffset? ResumeAt { get; set; }

    public DateTimeOffset? WaitingSince { get; set; }

    /// <summary>JSON: <see cref="Spec"/> -- analist ciktisi.</summary>
    public string? Spec { get; set; }

    /// <summary>JSON: calisma baslarken donan akis kopyasi.</summary>
    public string? WorkflowSnapshot { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class TurnRow
{
    public long Id { get; set; }

    public string RunId { get; set; } = "";

    public string Agent { get; set; } = "";

    public DateTimeOffset Ts { get; set; }

    public string? Stage { get; set; }

    public string? Task { get; set; }

    public int? Round { get; set; }

    public string Provider { get; set; } = "";

    public string Model { get; set; } = "";

    public Destination Destination { get; set; }

    public double DurationS { get; set; }

    public decimal? CostUsd { get; set; }

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    /// <summary><see cref="InputTokens"/> icindeki onbellekten okunan pay.</summary>
    public int? CacheReadTokens { get; set; }

    /// <summary><see cref="InputTokens"/> icindeki onbellege yazilan pay.</summary>
    public int? CacheWriteTokens { get; set; }

    public string Data { get; set; } = "";
}

internal sealed class MessageRow
{
    public long Id { get; set; }

    public string RunId { get; set; } = "";

    public DateTimeOffset Ts { get; set; }

    public MessageKind Kind { get; set; }

    public string FromAgent { get; set; } = "";

    public string ToAgent { get; set; } = "";

    public string? Task { get; set; }

    public string Data { get; set; } = "";
}

internal sealed class PhaseRow
{
    public long Id { get; set; }

    public string RunId { get; set; } = "";

    public string Task { get; set; } = "";

    public DateTimeOffset Ts { get; set; }

    public string Stage { get; set; } = "";

    public string Agent { get; set; } = "";

    public int Round { get; set; }

    public PhaseStatus Status { get; set; }

    public PhaseCause? Cause { get; set; }

    public string Data { get; set; } = "";
}

/// <summary>Tek satir (<c>id = 1</c>): calisma alani ayarlari.</summary>
internal sealed class SettingsRow
{
    public int Id { get; set; } = 1;

    public string Data { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }
}
