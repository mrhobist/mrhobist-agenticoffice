using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Domain.Workflows;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>
/// Calismanin durum gecisleri (docs/DOMAIN.md). Is kanalinda (Api, JobWorker havuzu) calisir; uclar <see cref="IRunReader"/> ile okur.
/// <c>Begin*</c>, <c>Retry</c>, <c>Cancel</c> hizli ve dogrulayicidir (uc noktasi 409/400'u hemen doner); <c>AnalyzeAsync</c> ve
/// <c>DispatchAsync</c> uzun surer, kuyrukta kosar. Prompt metinleri <see cref="Prompts"/>'ta, dagitim kurali <see cref="Dispatcher"/>'da.
/// Ayni calisma icin ayni anda tek is kosar (kanal kilidi); farkli calismalar paralel, ajan basina tek LLM cagrisi (<see cref="AgentCaller"/>).
/// </summary>
public interface IRunService
{
    /// <summary>run.json + workflow.json yazar, politika on denetimi yapar. LLM cagirmaz.</summary>
    Task<Run> CreateAsync(RunRequest request, CancellationToken ct);

    /// <summary>Analist turu: plan uretir, <c>AwaitingApproval</c>'a gecer. Revize notlari gecmisten okunur.</summary>
    Task<Run> AnalyzeAsync(string runId, CancellationToken ct);

    /// <summary>Yalniz <c>AwaitingApproval</c>: durumu Running yapar; dagitim kuyrukta kosar.</summary>
    Task<Run> BeginApproveAsync(string runId, CancellationToken ct);

    /// <summary>Yalniz <c>AwaitingApproval</c>: notu yazar, durumu Running yapar; analiz kuyrukta kosar.</summary>
    Task<Run> BeginReviseAsync(string runId, string note, CancellationToken ct);

    /// <summary>Organizatorun kurali: hazir gorevleri bos ajanlara verir; yurutucusu olmayan adimda <c>Paused</c>.</summary>
    Task<Run> DispatchAsync(string runId, CancellationToken ct);

    /// <summary>Failed/Interrupted/BudgetExceeded/Cancelled → Running; kaldigi adimi soyler (kuyruga o girer).</summary>
    Task<RetryResult> RetryAsync(string runId, CancellationToken ct);

    /// <summary>Running/AwaitingApproval/Paused → Cancelled. Suren LLM cagrisini kanal iptal eder; sonucu yazilmaz.</summary>
    Task<Run> CancelAsync(string runId, CancellationToken ct);

    /// <summary>Surec yeniden basladi: yarim kalan Running calismalar Interrupted olur.</summary>
    Task<int> MarkInterruptedAsync(CancellationToken ct);
}

public sealed class RunService(
    IRunStore runs,
    IWorkflowStore workflows,
    IAgentStore agents,
    IProjectStore projects,
    IRunReader reader,
    AgentCaller caller,
    ISceneEventPublisher scene) : IRunService
{
    private const string PlanRevisionSubject = "plan-revision";

    // ------------------------------------------------------------------ olusturma

    public async Task<Run> CreateAsync(RunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Brief))
        {
            throw new DomainException(ErrorCodes.RunBriefEmpty, "brief bos.");
        }

        if (request.MaxCostUsd is { } budget && budget <= 0m)
        {
            throw new DomainException(ErrorCodes.RunBudgetInvalid, "maxCostUsd sifirdan buyuk olmali ya da bos.");
        }

        // Is yalniz bir projenin icinde baslar (kullanici karari, docs/DOMAIN.md → Projeler). Akis bos → projenin varsayilani.
        if (string.IsNullOrWhiteSpace(request.Project))
        {
            throw new DomainException(ErrorCodes.RunProjectRequired, "project bos; is bir projenin icinde baslar.");
        }

        var project = await projects.LoadAsync(request.Project.Trim(), ct).ConfigureAwait(false);
        var wfKey = string.IsNullOrWhiteSpace(request.Workflow) ? project.Workflow : request.Workflow.Trim();
        var wf = await workflows.LoadAsync(wfKey, ct).ConfigureAwait(false);
        var team = await agents.LoadTeamAsync(ct).ConfigureAwait(false);
        wf.ValidateAgainst(team);

        var sensitivity = request.Sensitivity ?? RunDefaults.Sensitivity;
        var brief = request.Brief.Trim();
        var label = string.IsNullOrWhiteSpace(request.Label) ? Ellipsis(brief, 48) : request.Label.Trim();
        var run = new Run(NewId(), label, brief, sensitivity, DateTimeOffset.UtcNow, RunStatus.Running, Workflow: wfKey, Detail: "analiz", MaxCostUsd: request.MaxCostUsd, Project: project.Key, OwnerId: project.OwnerId);

        // Politika calisma baslamadan: tek aykiri rol varsa hic baslamaz (v1 dersi, CLAUDE.md §4).
        var violations = wf.Roles
            .Select(r => (Role: r, Target: AgentTarget.Of(team.Agents[r])))
            .Where(x => !SensitivityPolicy.Allows(sensitivity, x.Target.Destination))
            .Select(x => $"{x.Role} → {x.Target.Destination}")
            .ToList();
        if (violations.Count > 0)
        {
            run = run with
            {
                Status = RunStatus.PolicyRejected,
                FinishedAt = DateTimeOffset.UtcNow,
                Detail = $"hassasiyet {sensitivity}: {string.Join(", ", violations)}",
            };
        }

        await runs.CreateAsync(run, ct).ConfigureAwait(false);
        await runs.WriteWorkflowAsync(run.Id, wf, ct).ConfigureAwait(false);
        if (run.Status == RunStatus.PolicyRejected)
        {
            throw new DomainException(ErrorCodes.RunPolicyViolation, run.Detail!);
        }

        Publish(SceneEventTypes.WorkflowSet, new { key = wf.Key, run = run.Id });
        return run;
    }

    // ------------------------------------------------------------------ analiz

    public async Task<Run> AnalyzeAsync(string runId, CancellationToken ct)
    {
        var run = await reader.GetAsync(runId, ct).ConfigureAwait(false);
        if (run.Status != RunStatus.Running)
        {
            return run;
        }

        var wf = await RequireWorkflowAsync(runId, ct).ConfigureAwait(false);
        var analyze = wf.Stages[0];

        // Revizyon mu: kullanicidan gelen plan notu var mi (Detail metnine degil, kayda bakilir).
        var notes = (await runs.ReadMessagesAsync(run.Id, ct).ConfigureAwait(false))
            .Where(m => m.Subject == PlanRevisionSubject)
            .ToList();
        var isRevision = notes.Count > 0;

        Publish(SceneEventTypes.RunStage, new { stage = analyze.Title, task = "plan", round = notes.Count + 1, run = run.Id });
        PublishAgent(run, analyze.Role, "working", isRevision ? "plan revize ediliyor" : "brief çözümleniyor", "plan");

        try
        {
            var history = await AnalystHistoryAsync(run, wf, notes, ct).ConfigureAwait(false);
            var reply = await caller.CallAsync(run, analyze.Role, history, SpecSchema.Json, analyze.Id, null, null, ct).ConfigureAwait(false);
            if (await WasCancelledAsync(run.Id, ct).ConfigureAwait(false))
            {
                return await reader.GetAsync(run.Id, ct).ConfigureAwait(false);
            }

            var spec = SpecSchema.Parse(reply.StructuredJson, reply.Text);
            await runs.WriteSpecAsync(run.Id, spec, ct).ConfigureAwait(false);

            run = run with { TotalCostUsd = run.TotalCostUsd + reply.CostUsd };
            if (OverBudget(run))
            {
                return await StopForBudgetAsync(run, analyze.Role, ct).ConfigureAwait(false);
            }

            run = run with { Status = RunStatus.AwaitingApproval, Detail = "plan onay bekliyor" };
            await runs.UpdateAsync(run, ct).ConfigureAwait(false);
            PublishAgent(run, analyze.Role, "done", $"{spec.Tasks.Count} görev · plan onay bekliyor", "plan");
            return run;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await FailAsync(run, analyze.Role, analyze.Id, null, "analiz", ex, ct).ConfigureAwait(false);
        }
    }

    /// <summary>[brief istemi] → (her revizyon icin) [onceki plan] → [not] … Runtime hicbir sey hatirlamaz; gecmis buradan gider.</summary>
    private async Task<IReadOnlyList<RuntimeMessage>> AnalystHistoryAsync(Run run, Workflow wf, IReadOnlyList<Message> notes, CancellationToken ct)
    {
        var analyze = wf.Stages[0];
        var list = new List<RuntimeMessage> { new("user", Prompts.AnalystBrief(run, wf)) };

        var turns = (await runs.ReadTurnsAsync(run.Id, analyze.Role, ct).ConfigureAwait(false))
            .Where(t => t.Stage == analyze.Id && !string.IsNullOrWhiteSpace(t.Output))
            .Select(t => (t.Ts, Role: "assistant", Text: t.Output!));
        var noteItems = notes.Select(m => (m.Ts, Role: "user", Text: Prompts.RevisionNote(m.Body)));

        foreach (var item in turns.Concat(noteItems).OrderBy(x => x.Ts))
        {
            list.Add(new RuntimeMessage(item.Role, item.Text));
        }

        // Ardisik ayni roller (ornegin basarisiz bir tur) birlestirilir: saglayicilar user/assistant sirasi ister.
        var merged = new List<RuntimeMessage>();
        foreach (var m in list)
        {
            if (merged.Count > 0 && merged[^1].Role == m.Role)
            {
                merged[^1] = new RuntimeMessage(m.Role, merged[^1].Content + "\n\n" + m.Content);
            }
            else
            {
                merged.Add(m);
            }
        }

        return merged;
    }

    // ------------------------------------------------------------------ onay / revize

    public async Task<Run> BeginApproveAsync(string runId, CancellationToken ct)
    {
        var run = await RequireAwaitingAsync(runId, ct).ConfigureAwait(false);
        run = run with { Status = RunStatus.Running, Detail = "dağıtım" };
        await runs.UpdateAsync(run, ct).ConfigureAwait(false);
        return run;
    }

    public async Task<Run> BeginReviseAsync(string runId, string note, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new DomainException(ErrorCodes.RunNoteEmpty, "note bos.");
        }

        var run = await RequireAwaitingAsync(runId, ct).ConfigureAwait(false);
        var wf = await RequireWorkflowAsync(runId, ct).ConfigureAwait(false);
        await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, "user", wf.Stages[0].Role, note.Trim(), Stage: wf.Stages[0].Id, Subject: PlanRevisionSubject), ct).ConfigureAwait(false);
        run = run with { Status = RunStatus.Running, Detail = "plan revize" };
        await runs.UpdateAsync(run, ct).ConfigureAwait(false);
        return run;
    }

    private async Task<Run> RequireAwaitingAsync(string runId, CancellationToken ct)
    {
        var run = await reader.GetAsync(runId, ct).ConfigureAwait(false);
        if (run.Status != RunStatus.AwaitingApproval)
        {
            throw new DomainException(ErrorCodes.RunNotAwaitingApproval, $"Calisma '{run.Status}' durumunda; onay/revize yalniz AwaitingApproval'da.");
        }

        return run;
    }

    // ------------------------------------------------------------------ tekrar / iptal

    public async Task<RetryResult> RetryAsync(string runId, CancellationToken ct)
    {
        var run = await reader.GetAsync(runId, ct).ConfigureAwait(false);
        if (!run.IsRetryable)
        {
            throw new DomainException(ErrorCodes.RunNotRetryable, $"Calisma '{run.Status}' durumunda; yeniden deneme yalniz Failed/Interrupted/BudgetExceeded/Cancelled'da.");
        }

        // Nereden surecek: plan yoksa analiz. Plan varsa: analiz asamasinda dustuyse (Detail 'analiz' ile baslar) yine analiz,
        // aksi halde dagitim. Iptal/kesinti sirasinda onay bekliyorduysa (spec var, faz yok) dagitim degil onay: kullanici karar verir.
        var spec = await runs.ReadSpecAsync(run.Id, ct).ConfigureAwait(false);
        var hadPhases = (await runs.ListTasksAsync(run.Id, ct).ConfigureAwait(false)).Count > 0;
        var analysisFailed = run.Detail?.StartsWith("analiz", StringComparison.Ordinal) == true;
        RetryStep step;
        RunStatus next;
        string detail;
        if (spec is null || analysisFailed)
        {
            (step, next, detail) = (RetryStep.Analyze, RunStatus.Running, "analiz");
        }
        else if (!hadPhases && run.Status is RunStatus.Cancelled or RunStatus.Interrupted && run.Detail?.Contains("onay", StringComparison.Ordinal) == true)
        {
            (step, next, detail) = (RetryStep.Analyze, RunStatus.AwaitingApproval, "plan onay bekliyor");
        }
        else
        {
            (step, next, detail) = (RetryStep.Dispatch, RunStatus.Running, "dağıtım");
        }

        run = run with { Status = next, FinishedAt = null, Detail = detail, Retries = run.Retries + 1 };
        await runs.UpdateAsync(run, ct).ConfigureAwait(false);
        await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, "user", "organizer", $"yeniden dene #{run.Retries}: {detail}", Subject: "retry"), ct).ConfigureAwait(false);
        Publish(SceneEventTypes.WorkflowSet, new { key = run.Workflow, run = run.Id });
        return new RetryResult(run, next == RunStatus.AwaitingApproval ? RetryStep.Dispatch : step);
    }

    public async Task<Run> CancelAsync(string runId, CancellationToken ct)
    {
        var run = await reader.GetAsync(runId, ct).ConfigureAwait(false);
        if (!run.IsCancellable)
        {
            throw new DomainException(ErrorCodes.RunNotCancellable, $"Calisma '{run.Status}' durumunda; iptal yalniz Running/AwaitingApproval/Paused/Failed/Interrupted/BudgetExceeded'da.");
        }

        var wf = await runs.ReadWorkflowAsync(run.Id, ct).ConfigureAwait(false);
        var wasAwaiting = run.Status == RunStatus.AwaitingApproval;
        run = run with
        {
            Status = RunStatus.Cancelled,
            FinishedAt = DateTimeOffset.UtcNow,
            Detail = wasAwaiting ? "kullanıcı iptal etti (plan onay bekliyordu)" : "kullanıcı iptal etti",
        };
        await runs.UpdateAsync(run, ct).ConfigureAwait(false);
        await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, "user", wf?.HandoffRole ?? "organizer", "iptal", Subject: "cancel"), ct).ConfigureAwait(false);

        // Sahne: bu calismada Started fazi olan ajanlar bosa doner; gorevler panoda kuyrukta kalir (durum turetimli).
        foreach (var id in await runs.ListTasksAsync(run.Id, ct).ConfigureAwait(false))
        {
            var phases = await runs.ReadPhasesAsync(run.Id, id, ct).ConfigureAwait(false);
            if (phases.Count > 0 && phases[^1].Status == PhaseStatus.Started)
            {
                var p = phases[^1];
                await runs.AppendPhaseAsync(run.Id, p with { Ts = DateTimeOffset.UtcNow, Status = PhaseStatus.Skipped, Detail = "iptal" }, ct).ConfigureAwait(false);
                PublishAgent(run, p.Agent, "idle", null, null);
            }
        }

        if (wf is not null)
        {
            PublishAgent(run, wf.Stages[0].Role, "idle", null, null);
        }

        return run;
    }

    private async Task<bool> WasCancelledAsync(string runId, CancellationToken ct)
        => (await runs.GetAsync(runId, ct).ConfigureAwait(false))?.Status is RunStatus.Cancelled;

    // ------------------------------------------------------------------ dagitim

    public async Task<Run> DispatchAsync(string runId, CancellationToken ct)
    {
        var run = await reader.GetAsync(runId, ct).ConfigureAwait(false);
        if (run.Status != RunStatus.Running)
        {
            return run;
        }

        var wf = await RequireWorkflowAsync(runId, ct).ConfigureAwait(false);
        var spec = await runs.ReadSpecAsync(runId, ct).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCodes.RunPlanInvalid, "spec.json yok; dagitim plansiz yapilamaz.");
        var team = await agents.LoadTeamAsync(ct).ConfigureAwait(false);
        var stages = wf.TaskStages;

        var phasesByTask = new Dictionary<string, IReadOnlyList<Phase>>(StringComparer.Ordinal);
        foreach (var id in await runs.ListTasksAsync(runId, ct).ConfigureAwait(false))
        {
            phasesByTask[id] = await runs.ReadPhasesAsync(runId, id, ct).ConfigureAwait(false);
        }

        var ordered = TaskGraph.Order(spec.Tasks);
        if (phasesByTask.Count == 0 && stages.Count > 0)
        {
            // Ilk dagitim: pano onaydan sonra kurulur; gorevler ilk gorev-sutununda sirada bekler.
            Publish(SceneEventTypes.BoardSet, new
            {
                tasks = ordered.Select(t => new { id = t.Id, title = t.Title, stage = stages[0].Id, state = "queued" }).ToList(),
                run = run.Id,
            });
        }

        if (stages.Count == 0 || ordered.All(t => Dispatcher.IsDone(stages, phasesByTask.GetValueOrDefault(t.Id, []))))
        {
            run = run with { Status = RunStatus.Completed, FinishedAt = DateTimeOffset.UtcNow, Detail = "tüm görevler bitti" };
            await runs.UpdateAsync(run, ct).ConfigureAwait(false);
            PublishAgent(run, wf.HandoffRole ?? "organizer", "idle", null, null);
            return run;
        }

        // Ajan basina tek is, CALISMALAR ARASI: baska bir Running calismada Started fazi olan ya da su an LLM cagrisinda olan ajan bos degildir.
        var busy = new HashSet<string>(Dispatcher.BusyAgents(phasesByTask), StringComparer.Ordinal);
        busy.UnionWith(await BusyInOtherRunsAsync(run.Id, ct).ConfigureAwait(false));
        busy.UnionWith(AgentCaller.BusyAgents);

        var assignments = Dispatcher.Plan(wf, spec, phasesByTask, busy);
        var paused = new List<string>();
        foreach (var a in assignments)
        {
            var round = phasesByTask.GetValueOrDefault(a.Task.Id, []).Count(p => p.Stage == a.Stage.Id) + 1;
            if (wf.HandoffRole is { } organizer)
            {
                run = await HandoffAsync(run, spec, a, organizer, team, ct).ConfigureAwait(false);
                if (run.Status != RunStatus.Running)
                {
                    return run;
                }
            }

            await runs.AppendPhaseAsync(run.Id, new Phase(DateTimeOffset.UtcNow, a.Task.Id, a.Stage.Id, a.Stage.Title, a.Stage.Kind.ToString().ToLowerInvariant(), a.Agent, round, PhaseStatus.Started), ct).ConfigureAwait(false);
            Publish(SceneEventTypes.BoardMove, new { task = a.Task.Id, stage = a.Stage.Id, state = "active", run = run.Id });
            PublishAgent(run, a.Agent, "working", Ellipsis(a.Task.Title, 40), a.Task.Id);
            Publish(SceneEventTypes.RunStage, new { stage = a.Stage.Title, task = a.Task.Id, round, run = run.Id });

            // Bu teslimde analiz disinda yurutucu yok (docs/DOMAIN.md → Adim yurutuculeri): is masada bekler.
            paused.Add($"{a.Stage.Id} ({a.Agent})");
        }

        if (wf.HandoffRole is { } org)
        {
            PublishAgent(run, org, "idle", null, null);
        }

        if (paused.Count > 0)
        {
            run = run with { Status = RunStatus.Paused, Detail = "yürütücü yok: " + string.Join(", ", paused.Distinct(StringComparer.Ordinal)) };
            await runs.UpdateAsync(run, ct).ConfigureAwait(false);
        }
        else if (assignments.Count == 0)
        {
            // Hazir gorev var ama ajanlari baska calismalarda dolu: sirada bekler, bir sonraki dagitimda alinir.
            run = run with { Detail = "ajan bekleniyor (başka çalışmada dolu)" };
            await runs.UpdateAsync(run, ct).ConfigureAwait(false);
        }

        return run;
    }

    /// <summary>Diger Running calismalarda son fazi Started olan ajanlar. Paused calismalar sayilmaz: yurutucusuz bekleme kimseyi kilitlemesin.</summary>
    private async Task<IReadOnlySet<string>> BusyInOtherRunsAsync(string exceptRunId, CancellationToken ct)
    {
        var busy = new HashSet<string>(StringComparer.Ordinal);
        foreach (var other in await runs.ListAsync(100, ct).ConfigureAwait(false))
        {
            if (other.Id == exceptRunId || other.Status != RunStatus.Running)
            {
                continue;
            }

            foreach (var id in await runs.ListTasksAsync(other.Id, ct).ConfigureAwait(false))
            {
                var phases = await runs.ReadPhasesAsync(other.Id, id, ct).ConfigureAwait(false);
                if (phases.Count > 0 && phases[^1].Status == PhaseStatus.Started)
                {
                    busy.Add(phases[^1].Agent);
                }
            }
        }

        return busy;
    }

    /// <summary>Organizator devir notu: 1 LLM turu, ucuz model; not sonraki rolun girdisi olur (messages.jsonl).</summary>
    private async Task<Run> HandoffAsync(Run run, Spec spec, Assignment a, string organizer, Team team, CancellationToken ct)
    {
        var toName = team.Agents.TryGetValue(a.Agent, out var toAgent) ? toAgent.Name : a.Agent;
        PublishAgent(run, organizer, "working", $"{a.Task.Id} → {toName}", a.Task.Id);

        try
        {
            var reply = await caller.CallAsync(run, organizer, [new RuntimeMessage("user", Prompts.HandoffRequest(spec, a, toName))], null, a.Stage.Id, a.Task.Id, null, ct).ConfigureAwait(false);
            if (await WasCancelledAsync(run.Id, ct).ConfigureAwait(false))
            {
                return await reader.GetAsync(run.Id, ct).ConfigureAwait(false);
            }

            await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Handoff, organizer, a.Agent, reply.Text.Trim(), a.Task.Id, a.Stage.Id, Subject: "handoff"), ct).ConfigureAwait(false);
            run = run with { TotalCostUsd = run.TotalCostUsd + reply.CostUsd };
            if (OverBudget(run))
            {
                return await StopForBudgetAsync(run, organizer, ct).ConfigureAwait(false);
            }

            await runs.UpdateAsync(run, ct).ConfigureAwait(false);
            Publish(SceneEventTypes.Meet, new { from = organizer, to = a.Agent, kind = "handoff", run = run.Id });
            return run;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await FailAsync(run, organizer, a.Stage.Id, a.Task.Id, $"devir ({a.Task.Id})", ex, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ butce / hata / kurtarma

    private static bool OverBudget(Run run) => run.MaxCostUsd is { } max && run.TotalCostUsd > max;

    private async Task<Run> StopForBudgetAsync(Run run, string agent, CancellationToken ct)
    {
        run = run with
        {
            Status = RunStatus.BudgetExceeded,
            FinishedAt = DateTimeOffset.UtcNow,
            Detail = $"bütçe aşıldı: ${run.TotalCostUsd:0.####} > ${run.MaxCostUsd:0.####}",
        };
        await runs.UpdateAsync(run, ct).ConfigureAwait(false);
        await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, agent, "user", run.Detail!, Subject: "error"), ct).ConfigureAwait(false);
        PublishAgent(run, agent, "blocked", "bütçe aşıldı", null);
        return run;
    }

    /// <summary>Hata gunluge de yazilir (messages.jsonl, subject: error): "takildi" tek basina bilgi degildir (LESSONS).</summary>
    private async Task<Run> FailAsync(Run run, string agent, string stage, string? task, string what, Exception ex, CancellationToken ct)
    {
        if (await WasCancelledAsync(run.Id, ct).ConfigureAwait(false))
        {
            return await reader.GetAsync(run.Id, ct).ConfigureAwait(false);
        }

        run = run with { Status = RunStatus.Failed, FinishedAt = DateTimeOffset.UtcNow, Detail = Ellipsis($"{what}: {ex.Message}", 300) };
        await runs.UpdateAsync(run, ct).ConfigureAwait(false);
        await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, agent, "user", ex.Message, task, stage, Subject: "error"), ct).ConfigureAwait(false);
        PublishAgent(run, agent, "blocked", Ellipsis($"{what} başarısız: {ex.Message}", 60), task);
        return run;
    }

    public async Task<int> MarkInterruptedAsync(CancellationToken ct)
    {
        var count = 0;
        foreach (var run in await runs.ListAsync(200, ct).ConfigureAwait(false))
        {
            if (run.Status == RunStatus.Running)
            {
                await runs.UpdateAsync(run with { Status = RunStatus.Interrupted, FinishedAt = DateTimeOffset.UtcNow, Detail = "süreç yeniden başladı" }, ct).ConfigureAwait(false);
                count++;
            }
        }

        return count;
    }

    // ------------------------------------------------------------------ yardimcilar

    private async Task<Workflow> RequireWorkflowAsync(string runId, CancellationToken ct)
        => await runs.ReadWorkflowAsync(runId, ct).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCodes.ConfigFileMissing, $"{runId}/workflow.json yok.");

    /// <summary>agent.state olayi; calisma kimligi ve etiketi eklenir ki UI "hangi calisma, hangi gorev" gostersin.</summary>
    private void PublishAgent(Run run, string agent, string state, string? note, string? task)
        => Publish(SceneEventTypes.AgentState, new { agent, state, note, run = run.Id, runLabel = run.Label, task });

    private void Publish(string type, object data) => scene.Publish(type, JsonSerializer.Serialize(data, SceneJson));

    private static readonly JsonSerializerOptions SceneJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary><c>yyyyMMdd-HHmmss-xxxx</c>: dizin adi olarak sirali, ayni saniyede iki calisma carpismaz.</summary>
    private static string NewId()
        => DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + RandomNumberGenerator.GetHexString(4, lowercase: true);

    private static string Ellipsis(string s, int max)
    {
        var line = s.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }
}
