using Microsoft.EntityFrameworkCore;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Domain.Workflows;

namespace MrHobist.AITeam.Infrastructure.Persistence;

/// <summary>
/// Calisma gecmisinin deposu. JSONL doneminin yerlesimi tablolara birebir tasindi:
/// <c>run.json</c> -> <c>run</c> · <c>spec.json</c>/<c>workflow.json</c> -> <c>run</c> sutunlari ·
/// <c>conversations/{agent}.jsonl</c> -> <c>run_turn</c> · <c>messages.jsonl</c> -> <c>run_message</c> ·
/// <c>tasks/{task}/phases.jsonl</c> -> <c>run_phase</c>.
///
/// Append-only satir duzeyinde korunur: satirlar eklenir, guncellenmez (tek istisna calismanin tamami
/// silinirken, FK cascade ile). Sira AUTOINCREMENT <c>id</c>'dir; ayri bir sayac hesaplanmaz, cunku istek yolu
/// (iptal, cevap, revize) ile is kanali ayni calismaya AYNI ANDA yazabilir ve MAX+1 orada yarisir. Dosya adina donusen kimlik kalmadigi icin
/// <c>runs/</c> disina cikma (path traversal) sinifi tamamen ortadan kalkti -- kimlik artik bir sutun degeri.
/// </summary>
internal sealed class SqliteRunStore(IDbContextFactory<AiTeamContext> factory) : IRunStore
{
    public async Task CreateAsync(Run run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Runs.FirstOrDefaultAsync(x => x.Id == run.Id, ct).ConfigureAwait(false);
        if (existing is null)
        {
            var row = new RunRow { Id = run.Id, CreatedAt = DateTimeOffset.UtcNow };
            Fill(row, run);
            db.Runs.Add(row);
        }
        else
        {
            Fill(existing, run);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Ustveri guncellemesi. Satir yoksa olusturulur: JSONL doneminde de <c>run.json</c> kosulsuz yazilirdi.</summary>
    public Task UpdateAsync(Run run, CancellationToken ct) => CreateAsync(run, ct);

    public async Task WriteSpecAsync(string runId, Spec spec, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await RequireAsync(db, runId, ct).ConfigureAwait(false);
        row.Spec = PersistenceJson.Write(spec);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Akis kopyasi: hesaplanan ozellikler (Roles, TaskStages) yazilmaz, yalniz tanim.</summary>
    public async Task WriteWorkflowAsync(string runId, Workflow workflow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var dto = new WorkflowSnapshot(
            workflow.Key,
            workflow.Title,
            workflow.MaxReviewRounds,
            workflow.HandoffRole,
            [.. workflow.Stages.Select(s => new StageSnapshot(s.Id, s.Title, s.Kind, s.Role, s.OfficeRole, s.Description))],
            workflow.AskRole,
            workflow.PlanApprover);

        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await RequireAsync(db, runId, ct).ConfigureAwait(false);
        row.WorkflowSnapshot = PersistenceJson.Write(dto);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<Workflow?> ReadWorkflowAsync(string runId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var json = await db.Runs.Where(x => x.Id == runId).Select(x => x.WorkflowSnapshot).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var dto = PersistenceJson.Read<WorkflowSnapshot>(json);
        if (dto is null)
        {
            return null;
        }

        return new Workflow(
            dto.Key,
            dto.Title,
            dto.MaxReviewRounds,
            dto.HandoffRole,
            [.. (dto.Stages ?? []).Select(s => new Stage(s.Id, s.Title, s.Kind, s.Role, s.OfficeRole, s.Description ?? ""))],
            dto.AskRole,
            dto.PlanApprover);
    }

    private sealed record StageSnapshot(string Id, string Title, StageKind Kind, string Role, string OfficeRole, string? Description);

    private sealed record WorkflowSnapshot(string Key, string Title, int MaxReviewRounds, string? HandoffRole, IReadOnlyList<StageSnapshot>? Stages,
        string? AskRole = null, string? PlanApprover = null);

    public async Task AppendTurnAsync(string runId, Turn turn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(turn);
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Turns.Add(new TurnRow
        {
            RunId = runId,
            Agent = turn.Agent,
            Ts = turn.Ts,
            Stage = turn.Stage,
            Task = turn.Task,
            Round = turn.Round,
            Provider = turn.Provider,
            Model = turn.Model,
            Destination = turn.Destination,
            DurationS = turn.DurationS,
            CostUsd = turn.CostUsd,
            InputTokens = turn.InputTokens,
            OutputTokens = turn.OutputTokens,
            CacheReadTokens = turn.CacheReadTokens,
            CacheWriteTokens = turn.CacheWriteTokens,
            Data = PersistenceJson.Write(turn),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AppendMessageAsync(string runId, Message message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Messages.Add(new MessageRow
        {
            RunId = runId,
            Ts = message.Ts,
            Kind = message.Kind,
            FromAgent = message.From,
            ToAgent = message.To,
            Task = message.Task,
            Data = PersistenceJson.Write(message),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AppendPhaseAsync(string runId, Phase phase, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(phase);
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Phases.Add(new PhaseRow
        {
            RunId = runId,
            Task = phase.Task,
            Ts = phase.Ts,
            Stage = phase.Stage,
            Agent = phase.Agent,
            Round = phase.Round,
            Status = phase.Status,
            Cause = phase.Cause,
            Data = PersistenceJson.Write(phase),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<Run?> GetAsync(string runId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Runs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == runId, ct).ConfigureAwait(false);
        return row is null ? null : ToRun(row);
    }

    /// <summary>
    /// Yeni -> eski. Zaman damgalari metin olarak saklanir; tumu UTC yazildigi icin sozluk sirasi = zaman sirasi.
    /// (Yerel ofsetli bir damga yazilirsa bu kirilir -- yazan taraf daima UTC uretir.)
    /// </summary>
    public async Task<IReadOnlyList<Run>> ListAsync(int limit, CancellationToken ct, string? project = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.Runs.AsNoTracking();
        if (project is not null)
        {
            query = query.Where(x => x.ProjectKey == project);
        }

        var rows = await query.OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id).Take(limit).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToRun)];
    }

    /// <summary>Calismanin tamami: alt satirlar FK cascade ile duser (docs/DOMAIN.md -> Projeler -> Silme). Yoksa sessiz.</summary>
    public async Task DeleteAsync(string runId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Runs.Where(x => x.Id == runId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<Spec?> ReadSpecAsync(string runId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var json = await db.Runs.Where(x => x.Id == runId).Select(x => x.Spec).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return PersistenceJson.Read<Spec>(json);
    }

    public async Task<IReadOnlyList<Turn>> ReadTurnsAsync(string runId, string agent, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Turns.AsNoTracking()
            .Where(x => x.RunId == runId && x.Agent == agent)
            .OrderBy(x => x.Id)
            .Select(x => x.Data)
            .ToListAsync(ct).ConfigureAwait(false);
        return Revive<Turn>(rows);
    }

    /// <summary>Tek sorgu: son N calismanin turlarindan yalniz toplama sutunlari; <c>data</c> (prompt/cikti) okunmaz.</summary>
    public async Task<IReadOnlyList<TurnUsage>> ReadUsageAsync(int runLimit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var recent = db.Runs.OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id).Take(runLimit).Select(x => x.Id);
        return await db.Turns.AsNoTracking()
            .Where(t => recent.Contains(t.RunId))
            .OrderBy(t => t.Id)
            .Select(t => new TurnUsage(t.RunId, t.Provider, t.Model, t.InputTokens, t.OutputTokens, t.CostUsd, t.Ts))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Kalibrasyon ornekleri: <c>data</c> govdesi (prompt/cikti, onlarca KB) okunmaz, <c>json_extract</c> ile iki sayi ve
    /// bir bayrak alinir. <c>toolsOffered</c> olmayan eski satirlar (null) ornege girmez: arac tanimli mi bilinmiyor.
    /// </summary>
    public async Task<IReadOnlyList<CalibrationSample>> ReadCalibrationSamplesAsync(string provider, string model, int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var take = Math.Clamp(limit, 1, 500);
        var rows = await db.Database.SqlQuery<CalibrationRow>($"""
            SELECT CAST(json_extract(data, '$.promptChars') AS INTEGER) AS PromptChars,
                   input_tokens AS InputTokens,
                   COALESCE(CAST(json_extract(data, '$.turns') AS INTEGER), 1) AS Turns
            FROM run_turn
            WHERE provider = {provider} AND model = {model} AND input_tokens > 0
              AND json_extract(data, '$.toolsOffered') = 0
            ORDER BY id DESC
            LIMIT {take}
            """).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(r => new CalibrationSample(r.PromptChars ?? 0, r.InputTokens, r.Turns))];
    }

    private sealed class CalibrationRow
    {
        public int? PromptChars { get; set; }

        public int InputTokens { get; set; }

        public int Turns { get; set; }
    }

    public async Task<IReadOnlyList<string>> ListConversationsAsync(string runId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Turns.AsNoTracking()
            .Where(x => x.RunId == runId)
            .Select(x => x.Agent)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Message>> ReadMessagesAsync(string runId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Messages.AsNoTracking()
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.Id)
            .Select(x => x.Data)
            .ToListAsync(ct).ConfigureAwait(false);
        return Revive<Message>(rows);
    }

    public async Task<IReadOnlyList<Phase>> ReadPhasesAsync(string runId, string task, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Phases.AsNoTracking()
            .Where(x => x.RunId == runId && x.Task == task)
            .OrderBy(x => x.Id)
            .Select(x => x.Data)
            .ToListAsync(ct).ConfigureAwait(false);
        return Revive<Phase>(rows);
    }

    public async Task<IReadOnlyList<string>> ListTasksAsync(string runId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Phases.AsNoTracking()
            .Where(x => x.RunId == runId)
            .Select(x => x.Task)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    private static async Task<RunRow> RequireAsync(AiTeamContext db, string runId, CancellationToken ct)
        => await db.Runs.FirstOrDefaultAsync(x => x.Id == runId, ct).ConfigureAwait(false)
           ?? throw new InvalidOperationException($"Calisma yok: '{runId}'. Once CreateAsync cagrilmali.");

    /// <summary>Bozuk govde satirlari elenir: JSONL'deki "yarim satir yok sayilir" kuralinin karsiligi.</summary>
    private static IReadOnlyList<T> Revive<T>(List<string> rows)
        where T : class
        => [.. rows.Select(PersistenceJson.Read<T>).Where(x => x is not null).Select(x => x!)];

    private static void Fill(RunRow row, Run run)
    {
        row.ProjectKey = run.Project;
        row.Label = run.Label;
        row.Brief = run.Brief;
        row.Sensitivity = run.Sensitivity;
        row.Status = run.Status;
        row.Step = run.Step;
        row.WorkflowKey = run.Workflow;
        row.OwnerId = run.OwnerId;
        row.Detail = run.Detail;
        row.Question = run.Question is null ? null : PersistenceJson.Write(run.Question);
        row.TotalCostUsd = run.TotalCostUsd;
        row.MaxCostUsd = run.MaxCostUsd;
        row.InputTokens = run.InputTokens;
        row.OutputTokens = run.OutputTokens;
        row.Retries = run.Retries;
        row.StartedAt = run.StartedAt;
        row.FinishedAt = run.FinishedAt;
        row.ResumeAt = run.ResumeAt;
        row.WaitingSince = run.WaitingSince;
        row.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static Run ToRun(RunRow row) => new(
        row.Id,
        row.Label,
        row.Brief,
        row.Sensitivity,
        row.StartedAt,
        row.Status,
        row.FinishedAt,
        row.TotalCostUsd,
        row.WorkflowKey,
        row.Detail,
        row.MaxCostUsd,
        row.Retries,
        row.ProjectKey,
        row.OwnerId,
        PersistenceJson.Read<UserQuestion>(row.Question),
        row.ResumeAt,
        row.Step,
        row.WaitingSince,
        row.InputTokens,
        row.OutputTokens);
}
