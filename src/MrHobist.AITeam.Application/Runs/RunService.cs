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

    /// <summary>
    /// Organizatorun kurali + yurutme: hazir gorevi bos ajana verir ve adimi KOSAR (implement/review/design), bitince yeniden
    /// planlar; hazir is kalmayinca ya da calisma Running'den cikinca doner. Takilmada <c>AwaitingInput</c>, limitte <c>Paused</c>.
    /// </summary>
    Task<Run> DispatchAsync(string runId, CancellationToken ct);

    /// <summary>Failed/Interrupted/BudgetExceeded/Cancelled (ve limit beklemesi) → Running; <see cref="Run.Step"/> kaldigi adimi soyler (kuyruga o girer).</summary>
    Task<Run> RetryAsync(string runId, CancellationToken ct);

    /// <summary>Limit beklemesi bitti (otomatik): Running; kullanici tekrari SAYILMAZ, not organizatorden.</summary>
    Task<Run> ResumeAsync(string runId, CancellationToken ct);

    /// <summary>Yalniz <c>AwaitingInput</c>: kullanicinin secimini uygular (retry / skip / cancel). Running donerse dagitim kuyruga girer.</summary>
    Task<Run> BeginAnswerAsync(string runId, AnswerRequest answer, CancellationToken ct);

    /// <summary>Running/AwaitingApproval/Paused → Cancelled. Suren LLM cagrisini kanal iptal eder; sonucu yazilmaz.</summary>
    Task<Run> CancelAsync(string runId, CancellationToken ct);

    /// <summary>Surec yeniden basladi: yarim kalan Running calismalar Interrupted olur; ajan bekleyenler kuyruga geri konur.</summary>
    Task<int> MarkInterruptedAsync(CancellationToken ct);
}

public sealed class RunService(
    IRunStore runs,
    IWorkflowStore workflows,
    IAgentStore agents,
    IProjectStore projects,
    IRunReader reader,
    AgentCaller caller,
    ISceneEventPublisher scene,
    IWorkspaceLocator workspace,
    IRunScheduler scheduler) : IRunService
{
    private const string PlanRevisionSubject = "plan-revision";

    /// <summary>Takilma sorusunun sabit secenekleri (docs/DOMAIN.md → Takilma). Etiket baglama gore degisir, kimlik degismez.</summary>
    private static IReadOnlyList<QuestionOption> Options(string retryLabel, string retryDetail, string skipLabel, string skipDetail, bool retryNeedsNote) =>
    [
        new("retry", retryLabel, retryDetail, retryNeedsNote),
        new("skip", skipLabel, skipDetail),
        new("cancel", "Kapat", "Çalışmayı iptal et; geçmiş kalır, dosyalar silinmez."),
    ];

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
        var run = new Run(NewId(), label, brief, sensitivity, DateTimeOffset.UtcNow, RunStatus.Running, Workflow: wfKey, Detail: "analiz", MaxCostUsd: request.MaxCostUsd, Project: project.Key, OwnerId: project.OwnerId, Step: RunStep.Analyze);

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
            var root = await RootAsync(run, ct).ConfigureAwait(false);
            var history = await AnalystHistoryAsync(run, wf, notes, root, ct).ConfigureAwait(false);
            var reply = await caller.CallAsync(run, analyze.Role, history, SpecSchema.Json, analyze.Id, null, null, ct, ToolAccess.ForKind(StageKind.Analyze, root)).ConfigureAwait(false);
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

            run = run with { Status = RunStatus.AwaitingApproval, Detail = "plan onay bekliyor", Step = RunStep.Approval };
            await runs.UpdateAsync(run, ct).ConfigureAwait(false);
            PublishAgent(run, analyze.Role, "done", $"{spec.Tasks.Count} görev · plan onay bekliyor", "plan");
            return run;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LimitReachedException ex)
        {
            return await PauseForLimitAsync(run, analyze.Role, "analiz", ex, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await FailAsync(run, analyze.Role, analyze.Id, null, "analiz", ex, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Projenin hedef dizini (mutlak); Infrastructure yoksa olusturur. Ajan araclari burada acilir.</summary>
    private async Task<string> RootAsync(Run run, CancellationToken ct)
        => workspace.RootOf(await projects.LoadAsync(run.Project, ct).ConfigureAwait(false));

    /// <summary>[brief istemi] → (her revizyon icin) [onceki plan] → [not] … Runtime hicbir sey hatirlamaz; gecmis buradan gider.</summary>
    private async Task<IReadOnlyList<RuntimeMessage>> AnalystHistoryAsync(Run run, Workflow wf, IReadOnlyList<Message> notes, string root, CancellationToken ct)
    {
        var analyze = wf.Stages[0];
        var list = new List<RuntimeMessage> { new("user", Prompts.AnalystBrief(run, wf, root)) };

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
        run = run with { Status = RunStatus.Running, Detail = "dağıtım", Step = RunStep.Dispatch };
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
        run = run with { Status = RunStatus.Running, Detail = "plan revize", Step = RunStep.Analyze };
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

    public Task<Run> RetryAsync(string runId, CancellationToken ct) => ContinueAsync(runId, byUser: true, ct);

    public Task<Run> ResumeAsync(string runId, CancellationToken ct) => ContinueAsync(runId, byUser: false, ct);

    /// <summary>Ortak surdurme: kullanici "yeniden dene" (sayac + user notu) ya da otomatik limit surdurmesi (sayac yok, organizer notu).</summary>
    private async Task<Run> ContinueAsync(string runId, bool byUser, CancellationToken ct)
    {
        var run = await reader.GetAsync(runId, ct).ConfigureAwait(false);
        if (!run.IsRetryable)
        {
            throw new DomainException(ErrorCodes.RunNotRetryable, $"Calisma '{run.Status}' durumunda; yeniden deneme yalniz Failed/Interrupted/BudgetExceeded/Cancelled ve limit beklemesinde.");
        }

        if (!byUser && !(run.Status == RunStatus.Paused && run.ResumeAt is not null))
        {
            throw new DomainException(ErrorCodes.RunNotRetryable, $"Otomatik surdurme yalniz limit beklemesinde; calisma '{run.Status}'.");
        }

        // Nereden surecek: kaydedilen adim (Run.Step). Plan yoksa her durumda analiz. Onay beklerken iptal/kesinti olduysa
        // dagitim degil onay: kullanici karar verir, kuyruga is girmez. Eski kayitta Step yok: plan varsa dagitim sayilir.
        var spec = await runs.ReadSpecAsync(run.Id, ct).ConfigureAwait(false);
        var step = spec is null ? RunStep.Analyze : run.Step ?? RunStep.Dispatch;
        var (next, detail) = step switch
        {
            RunStep.Analyze => (RunStatus.Running, "analiz"),
            RunStep.Approval => (RunStatus.AwaitingApproval, "plan onay bekliyor"),
            _ => (RunStatus.Running, "dağıtım"),
        };

        run = run with { Status = next, Step = step, FinishedAt = null, Detail = detail, Retries = byUser ? run.Retries + 1 : run.Retries, ResumeAt = null, Question = null, WaitingSince = null };
        await runs.UpdateAsync(run, ct).ConfigureAwait(false);
        await runs.AppendMessageAsync(run.Id, byUser
            ? new Message(DateTimeOffset.UtcNow, MessageKind.Note, "user", "organizer", $"yeniden dene #{run.Retries}: {detail}", Subject: "retry")
            : new Message(DateTimeOffset.UtcNow, MessageKind.Note, "organizer", "user", $"limit penceresi sıfırlandı, sürüyor: {detail}", Subject: "limit-resume"), ct).ConfigureAwait(false);
        Publish(SceneEventTypes.WorkflowSet, new { key = run.Workflow, run = run.Id });
        return run;
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
            Question = null,
            ResumeAt = null,
            WaitingSince = null,
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
                // Failed (Skipped degil): "yeniden dene" ayni adimi yeniden kosar; Skipped bir sonraki adima gecirir ve kod yazilmadan test baslar.
                await runs.AppendPhaseAsync(run.Id, p with { Ts = DateTimeOffset.UtcNow, Status = PhaseStatus.Failed, Detail = "iptal: kullanıcı durdurdu", Cause = PhaseCause.Cancelled }, ct).ConfigureAwait(false);
                PublishAgent(run, p.Agent, "idle", null, null);
            }
        }

        if (wf is not null)
        {
            PublishAgent(run, wf.Stages[0].Role, "idle", null, null);
        }

        // Bu calismanin ajanlari bosaldi: ajan bekleyen baska calisma varsa sirasi geldi.
        await WakeWaitingAsync(run.Id, ct).ConfigureAwait(false);
        return run;
    }

    private async Task<bool> WasCancelledAsync(string runId, CancellationToken ct)
        => (await runs.GetAsync(runId, ct).ConfigureAwait(false))?.Status is RunStatus.Cancelled;

    // ------------------------------------------------------------------ dagitim

    public async Task<Run> DispatchAsync(string runId, CancellationToken ct)
    {
        try
        {
            return await DispatchLoopAsync(runId, ct).ConfigureAwait(false);
        }
        finally
        {
            // Dongu nasil bitmis olsun (bitti, dustu, soru, limit, iptal): bu calismanin ajanlari bosaldi. Ajan bekleyen
            // baska calisma varsa dagitimi kuyruga girer; iptal belirteci kesilmis olsa da uyandirma yapilir.
            await WakeWaitingAsync(runId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ajan bekleyen (Running + <see cref="Run.WaitingSince"/>) diger calismalari dagitima koyar. Tek uyandirma noktasi:
    /// bir adim kapaninca, dagitim dongusu bitince, iptalde. Bekleyen calismanin isi kuyrukta DEGILDIR; bu olmadan
    /// "ajan bekleniyor" diyen calisma surec yeniden baslayana kadar Running kalirdi.
    /// </summary>
    private async Task WakeWaitingAsync(string exceptRunId, CancellationToken ct)
    {
        foreach (var other in await runs.ListAsync(100, ct).ConfigureAwait(false))
        {
            if (other.Id != exceptRunId && other.Status == RunStatus.Running && other.WaitingSince is not null)
            {
                scheduler.Schedule(other.Id, RunStep.Dispatch);
            }
        }
    }

    private async Task<Run> DispatchLoopAsync(string runId, CancellationToken ct)
    {
        var run = await reader.GetAsync(runId, ct).ConfigureAwait(false);
        if (run.Status != RunStatus.Running)
        {
            return run;
        }

        var wf = await RequireWorkflowAsync(runId, ct).ConfigureAwait(false);
        var spec = await runs.ReadSpecAsync(runId, ct).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCodes.RunPlanInvalid, "spec.json yok; dagitim plansiz yapilamaz.");
        var team = wf.HandoffRole is null ? null : await agents.LoadTeamAsync(ct).ConfigureAwait(false);
        var stages = wf.TaskStages;
        var root = await RootAsync(run, ct).ConfigureAwait(false);
        var ordered = TaskGraph.Order(spec.Tasks);

        // Dongu: planla → bir adimi kos → yeniden planla. Calisma icinde SIRALI (calisma basina tek is, kanal kilidi);
        // farkli calismalar JobWorker havuzunda paralel. Hazir is kalmayinca ya da durum Running'den cikinca doner.
        for (; ; )
        {
            var phasesByTask = new Dictionary<string, IReadOnlyList<Phase>>(StringComparer.Ordinal);
            foreach (var id in await runs.ListTasksAsync(runId, ct).ConfigureAwait(false))
            {
                phasesByTask[id] = await runs.ReadPhasesAsync(runId, id, ct).ConfigureAwait(false);
            }

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
                run = run with { Status = RunStatus.Completed, FinishedAt = DateTimeOffset.UtcNow, Detail = "tüm görevler bitti", WaitingSince = null };
                await runs.UpdateAsync(run, ct).ConfigureAwait(false);
                PublishAgent(run, wf.HandoffRole ?? "organizer", "idle", null, null);
                return run;
            }

            // Ajan basina tek is, CALISMALAR ARASI: baska bir Running calismada Started fazi olan ya da su an LLM cagrisinda olan ajan bos degildir.
            var busy = new HashSet<string>(Dispatcher.BusyAgents(phasesByTask), StringComparer.Ordinal);
            busy.UnionWith(await BusyInOtherRunsAsync(run.Id, ct).ConfigureAwait(false));
            busy.UnionWith(AgentCaller.BusyAgents);

            var assignments = Dispatcher.Plan(wf, spec, phasesByTask, busy);
            if (assignments.Count == 0)
            {
                // Hazir gorev var ama ajanlari baska calismalarda dolu: is kuyruktan cikar, WaitingSince isaretlenir;
                // o calismanin bir adimi kapaninca WakeWaitingAsync bunu yeniden dagitima koyar.
                run = run with { Detail = "ajan bekleniyor (başka çalışmada dolu)", WaitingSince = run.WaitingSince ?? DateTimeOffset.UtcNow };
                await runs.UpdateAsync(run, ct).ConfigureAwait(false);
                if (wf.HandoffRole is { } idle)
                {
                    PublishAgent(run, idle, "idle", null, null);
                }

                return run;
            }

            var a = assignments[0];
            var taskPhases = phasesByTask.GetValueOrDefault(a.Task.Id, []);
            var round = taskPhases.Count(p => p.Stage == a.Stage.Id && p.Status != PhaseStatus.Started && !p.IsSystemFailure) + 1; // tur = bitmis denemeler + 1
            run = run with { WaitingSince = null };

            // Devir notu yalniz implement adimina atamada (ilk ve red sonrasi): inceleme adimlarina not bilgi katmiyor,
            // her gecis bir organizator turu (olculdu: 14–70 s, ≈$0.02–0.05) ediyordu.
            if (wf.HandoffRole is { } organizer && team is not null && a.Stage.Kind == StageKind.Implement)
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
            run = run with { Detail = $"{a.Stage.Title}: {a.Task.Id} ({a.Agent}, tur {round})" };
            await runs.UpdateAsync(run, ct).ConfigureAwait(false);

            run = await ExecuteStepAsync(run, wf, spec, a, round, root, ct).ConfigureAwait(false);
            if (run.Status != RunStatus.Running)
            {
                return run;
            }

            // Adim kapandi, ajani bosaldi: ajan bekleyen baska calisma varsa sirasi geldi (dongu bitmeden).
            await WakeWaitingAsync(run.Id, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ adim yurutuculeri (docs/DOMAIN.md → Adim yurutuculeri)

    /// <summary>
    /// Bir adimi kosar ve fazi kapatir. implement → developer araclarla yazar, rapor verir · review → testci/manager kabul ya da
    /// gerekceli red (red developer'a doner; tavan asilirsa kullaniciya sorulur) · design → rehberlik notu · handoff → not.
    /// Hata: gecici olmayan → faz Failed + calisma Failed (karar: retry/cancel) · limit → Paused + ResumeAt · engel → AwaitingInput.
    /// </summary>
    private async Task<Run> ExecuteStepAsync(Run run, Workflow wf, Spec spec, Assignment a, int round, string root, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var notes = (await runs.ReadMessagesAsync(run.Id, ct).ConfigureAwait(false))
            .Where(m => m.Task == a.Task.Id && m.Subject is not "retry")
            .OrderBy(m => m.Ts)
            .ToList();
        var tools = ToolAccess.ForKind(a.Stage.Kind, root);

        try
        {
            switch (a.Stage.Kind)
            {
                case StageKind.Implement:
                {
                    var reply = await caller.CallAsync(run, a.Agent, [new RuntimeMessage("user", Prompts.ImplementTask(spec, a, root, notes, round))], StepSchemas.Implement, a.Stage.Id, a.Task.Id, round, ct, tools).ConfigureAwait(false);
                    if (await WasCancelledAsync(run.Id, ct).ConfigureAwait(false))
                    {
                        return await reader.GetAsync(run.Id, ct).ConfigureAwait(false);
                    }

                    run = await AddCostAsync(run, reply.CostUsd, ct).ConfigureAwait(false);
                    var report = StepSchemas.ParseImplement(reply.StructuredJson, reply.Text);
                    var files = report.FilesChanged.Count > 0 ? report.FilesChanged : reply.ToolUses.Where(t => t.Tool is "Write" or "Edit").Select(t => t.Target ?? "?").Distinct().ToList();
                    var body = $"{report.Summary}\n\nDosyalar: {(files.Count == 0 ? "—" : string.Join(", ", files))}\nKomutlar: {(report.CommandsRun.Count == 0 ? "—" : string.Join(" · ", report.CommandsRun))}";
                    var next = wf.TaskStages.SkipWhile(s => s.Id != a.Stage.Id).Skip(1).FirstOrDefault();
                    await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, a.Agent, next?.Role ?? "user", body, a.Task.Id, a.Stage.Id, Subject: "implement-report"), ct).ConfigureAwait(false);

                    if (report.Blocked)
                    {
                        // Developer tahmin etmedi, sordu (kullanici karari: soru + mudahale). Faz Failed: cevap gelince ayni adim yeniden kosar.
                        var q = string.IsNullOrWhiteSpace(report.Question) ? "Görev bitirilemedi; ne yapılmalı?" : report.Question.Trim();

                        // can_ask: soru once ekipteki hedefe (manager) gider; o cevaplarsa kullanici hic gormez (docs/DOMAIN.md → Takilma, ajan → ajan sorusu).
                        var asked = await AskColleagueAsync(run, spec, a, q, body, notes, root, round, ct).ConfigureAwait(false);
                        run = asked.Run;
                        if (run.Status != RunStatus.Running)
                        {
                            return run;
                        }

                        if (asked.Answered)
                        {
                            await ClosePhaseAsync(run, wf, a, round, PhaseStatus.Failed, started, Ellipsis($"soru → {asked.Target}: {q}", 200), ct).ConfigureAwait(false);
                            break; // dagitici Failed → ayni adim; cevap notlar arasinda gider. Ust uste hata tavani asagida korur.
                        }

                        await ClosePhaseAsync(run, wf, a, round, PhaseStatus.Failed, started, Ellipsis("soru: " + q, 200), ct).ConfigureAwait(false);
                        var context = asked.EscalateReason is null ? body : $"{body}\n\n{asked.Target}: {asked.EscalateReason}";
                        return await AskUserAsync(run, a.Agent, q, Options("Cevapla ve yeniden dene", "Cevabın developer'a not olarak gider; görev aynı adımdan sürer.", "Bu adımı geç", "Elle hallettim ya da gerekmiyor: görev bir sonraki adıma geçer.", retryNeedsNote: true), a, context, ct).ConfigureAwait(false);
                    }

                    await ClosePhaseAsync(run, wf, a, round, PhaseStatus.Done, started, Ellipsis(report.Summary, 200), ct).ConfigureAwait(false);
                    PublishAgent(run, a.Agent, "done", Ellipsis(report.Summary, 40), a.Task.Id);
                    break;
                }

                case StageKind.Review:
                {
                    var reply = await caller.CallAsync(run, a.Agent, [new RuntimeMessage("user", Prompts.ReviewTask(spec, a, root, notes, a.Stage, round, wf.MaxReviewRounds))], StepSchemas.Review, a.Stage.Id, a.Task.Id, round, ct, tools).ConfigureAwait(false);
                    if (await WasCancelledAsync(run.Id, ct).ConfigureAwait(false))
                    {
                        return await reader.GetAsync(run.Id, ct).ConfigureAwait(false);
                    }

                    run = await AddCostAsync(run, reply.CostUsd, ct).ConfigureAwait(false);
                    var report = StepSchemas.ParseReview(reply.StructuredJson, reply.Text);
                    var developer = wf.ImplementBefore(a.Stage).Role;
                    if (report.Accepted)
                    {
                        await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, a.Agent, developer, $"Kabul ({a.Stage.Title}). {(report.TestsRun ? "Testler çalıştırıldı." : "Testler çalıştırılmadı.")} {report.Feedback}".Trim(), a.Task.Id, a.Stage.Id, Subject: "review-accept"), ct).ConfigureAwait(false);
                        await ClosePhaseAsync(run, wf, a, round, PhaseStatus.Done, started, report.TestsRun ? "kabul · testler koşuldu" : "kabul · test koşulmadı", ct).ConfigureAwait(false);
                        PublishAgent(run, a.Agent, "done", "kabul", a.Task.Id);
                        break;
                    }

                    var feedback = report.Feedback + (report.Findings.Count == 0 ? "" : "\n\nBulgular:\n" + string.Join("\n", report.Findings.Select(f => "- " + f)));
                    await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, a.Agent, developer, feedback, a.Task.Id, a.Stage.Id, Subject: "review-feedback"), ct).ConfigureAwait(false);
                    await ClosePhaseAsync(run, wf, a, round, PhaseStatus.Rejected, started, Ellipsis("red: " + ((report.Findings.Count > 0 ? report.Findings[0] : report.Feedback)), 200), ct).ConfigureAwait(false);
                    Publish(SceneEventTypes.Meet, new { from = a.Agent, to = developer, kind = "reject", run = run.Id });
                    PublishAgent(run, a.Agent, "blocked", "red → developer", a.Task.Id);

                    if (round >= wf.MaxReviewRounds)
                    {
                        // Red tavani (kapi basina sayilir, docs/DOMAIN.md → Geri donus kurali): kullaniciya sorulur.
                        var q = $"{a.Stage.Title} {round}. kez reddetti (tavan {wf.MaxReviewRounds}). Son bulgu: {Ellipsis((report.Findings.Count > 0 ? report.Findings[0] : report.Feedback), 160)}";
                        return await AskUserAsync(run, a.Agent, q, Options("Bir tur daha", "Notunla birlikte developer'a geri gider; sonraki redde yine sorulur.", "Olduğu gibi kabul et", $"{a.Stage.Title} adımı geçilir; görev bir sonraki adıma geçer.", retryNeedsNote: false), a, feedback, ct).ConfigureAwait(false);
                    }

                    break;
                }

                case StageKind.Design:
                {
                    var reply = await caller.CallAsync(run, a.Agent, [new RuntimeMessage("user", Prompts.DesignTask(spec, a, root, notes))], StepSchemas.Design, a.Stage.Id, a.Task.Id, round, ct, tools).ConfigureAwait(false);
                    if (await WasCancelledAsync(run.Id, ct).ConfigureAwait(false))
                    {
                        return await reader.GetAsync(run.Id, ct).ConfigureAwait(false);
                    }

                    run = await AddCostAsync(run, reply.CostUsd, ct).ConfigureAwait(false);
                    var report = StepSchemas.ParseDesign(reply.StructuredJson, reply.Text);
                    var developer = wf.TaskStages.FirstOrDefault(s => s.Kind == StageKind.Implement)?.Role ?? "developer";
                    var body = report.Guidance + (report.Decisions.Count == 0 ? "" : "\n\nKararlar:\n" + string.Join("\n", report.Decisions.Select(d => "- " + d)));
                    await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, a.Agent, developer, body, a.Task.Id, a.Stage.Id, Subject: "design"), ct).ConfigureAwait(false);
                    await ClosePhaseAsync(run, wf, a, round, PhaseStatus.Done, started, Ellipsis(report.Guidance, 200), ct).ConfigureAwait(false);
                    PublishAgent(run, a.Agent, "done", "rehberlik hazır", a.Task.Id);
                    break;
                }

                default:
                {
                    // handoff turu adim: devir notu zaten yazildi; adim bloklamaz.
                    await ClosePhaseAsync(run, wf, a, round, PhaseStatus.Done, started, "devir", ct).ConfigureAwait(false);
                    break;
                }
            }

            if (OverBudget(run))
            {
                return await StopForBudgetAsync(run, a.Agent, ct).ConfigureAwait(false);
            }

            // Ayni adimda ust uste hata/red: bir gorev sonsuza kadar donmesin (tavan = maxReviewRounds).
            var phases = await runs.ReadPhasesAsync(run.Id, a.Task.Id, ct).ConfigureAwait(false);
            var failures = phases.Count(p => p.Stage == a.Stage.Id && p.Status == PhaseStatus.Failed && !p.IsSystemFailure);
            if (failures >= wf.MaxReviewRounds && phases[^1].Status == PhaseStatus.Failed)
            {
                return await AskUserAsync(run, a.Agent, $"{a.Stage.Title} adımı {failures} kez başarısız oldu: {phases[^1].Detail}", Options("Notla yeniden dene", "Notun ajana gider, adım yeniden koşar.", "Bu adımı geç", "Elle hallettim: görev bir sonraki adıma geçer.", retryNeedsNote: false), a, phases[^1].Detail, ct).ConfigureAwait(false);
            }

            return await reader.GetAsync(run.Id, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LimitReachedException ex)
        {
            await ClosePhaseAsync(run, wf, a, round, PhaseStatus.Failed, started, Ellipsis("limit: " + ex.Message, 200), ct, PhaseCause.Limit).ConfigureAwait(false);
            return await PauseForLimitAsync(run, a.Agent, $"{a.Stage.Title}: {a.Task.Id}", ex, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!await WasCancelledAsync(run.Id, ct).ConfigureAwait(false))
            {
                await ClosePhaseAsync(run, wf, a, round, PhaseStatus.Failed, started, Ellipsis(ex.Message, 200), ct).ConfigureAwait(false);
            }

            return await FailAsync(run, a.Agent, a.Stage.Id, a.Task.Id, $"{a.Stage.Title} ({a.Task.Id})", ex, ct).ConfigureAwait(false);
        }
    }

    private async Task ClosePhaseAsync(Run run, Workflow wf, Assignment a, int round, PhaseStatus status, DateTimeOffset started, string? detail, CancellationToken ct, PhaseCause? cause = null)
    {
        var phase = new Phase(DateTimeOffset.UtcNow, a.Task.Id, a.Stage.Id, a.Stage.Title, a.Stage.Kind.ToString().ToLowerInvariant(), a.Agent, round, status, (DateTimeOffset.UtcNow - started).TotalSeconds, detail, cause);
        await runs.AppendPhaseAsync(run.Id, phase, ct).ConfigureAwait(false);

        // Pano canli kalsin: faz kapaninca not hedef sutuna gecer (atamada yalniz "active" yayimlaniyordu; bitince yerinde kaliyordu).
        var (stage, state) = BoardTarget(wf, phase);
        Publish(SceneEventTypes.BoardMove, new { task = a.Task.Id, stage, state, run = run.Id });
    }

    /// <summary>
    /// Kapanan fazin panodaki yeri; kural dagiticinin kendisidir (<see cref="Dispatcher.NextStage"/>, tek kaynak): Done/Skipped →
    /// sonraki sutunda sirada, son adimsa Bitti (<c>done</c>) · Rejected → geri donulen implement sutununda takildi · Failed → ayni sutunda takildi.
    /// </summary>
    private static (string Stage, string State) BoardTarget(Workflow wf, Phase closed)
    {
        var next = Dispatcher.NextStage(wf.TaskStages, [closed]);
        return closed.Status is PhaseStatus.Done or PhaseStatus.Skipped
            ? (next is null ? (closed.Stage, "done") : (next.Id, "queued"))
            : (next?.Id ?? closed.Stage, "blocked");
    }

    private async Task<Run> AddCostAsync(Run run, decimal cost, CancellationToken ct)
    {
        run = run with { TotalCostUsd = run.TotalCostUsd + cost };
        await runs.UpdateAsync(run, ct).ConfigureAwait(false);
        return run;
    }

    // ------------------------------------------------------------------ takilma: soru ve cevap (docs/DOMAIN.md → Takilma)

    /// <summary>Ajan → ajan sorusunun sonucu: cevaplandi (not yazildi) · yukseltildi (sebep kullaniciya) · hedef yok (kullaniciya).</summary>
    private sealed record ColleagueAsk(Run Run, bool Answered, string? Target, string? EscalateReason);

    /// <summary>
    /// Takilan ajanin sorusu <c>can_ask</c> hedefine (ajan md'si; bugun manager): 1 LLM turu, yalniz okuma araci. Kayit
    /// messages.jsonl'de <c>ask</c> (soran → hedef) + <c>answer</c> (hedef → soran, ayni ref); sonraki turda notlar arasinda gider.
    /// Kural (varsayimla): hedef ayni gorevde BIR kez sorulur — ikinci takilma kullaniciya gider. Yukseltme (escalate) ya da
    /// hedefin hatasi kullaniciya duser; limit dogrudan yukari cikar (adim limit fazi olur).
    /// </summary>
    private async Task<ColleagueAsk> AskColleagueAsync(Run run, Spec spec, Assignment a, string question, string report, IReadOnlyList<Message> notes, string root, int round, CancellationToken ct)
    {
        var team = await agents.LoadTeamAsync(ct).ConfigureAwait(false);
        var target = team.Agents.TryGetValue(a.Agent, out var asker) ? asker.CanAsk : null;
        if (target is null || asker is null || !team.Agents.TryGetValue(target, out var colleague))
        {
            return new ColleagueAsk(run, false, null, null);
        }

        if (notes.Any(m => m.Kind == MessageKind.Ask && m.From == a.Agent && m.To == target))
        {
            return new ColleagueAsk(run, false, target, null); // bir kez soruldu; yine takildi → kullanici
        }

        var now = DateTimeOffset.UtcNow;
        var reference = $"q-{now.UtcTicks}";
        await runs.AppendMessageAsync(run.Id, new Message(now, MessageKind.Ask, a.Agent, target, question, a.Task.Id, a.Stage.Id, Ref: reference, Subject: "ask"), ct).ConfigureAwait(false);
        Publish(SceneEventTypes.Meet, new { from = a.Agent, to = target, kind = "ask", run = run.Id });
        PublishAgent(run, a.Agent, "waiting", $"{colleague.Name} → soru", a.Task.Id);
        PublishAgent(run, target, "working", $"{asker.Name} soruyor", a.Task.Id);

        try
        {
            var prompt = Prompts.AskColleague(spec, a, root, notes, asker.Name, question, report);
            var reply = await caller.CallAsync(run, target, [new RuntimeMessage("user", prompt)], StepSchemas.Ask, a.Stage.Id, a.Task.Id, round, ct, new ToolAccess(ToolAccess.ReadOnly, root, 20)).ConfigureAwait(false);
            if (await WasCancelledAsync(run.Id, ct).ConfigureAwait(false))
            {
                return new ColleagueAsk(await reader.GetAsync(run.Id, ct).ConfigureAwait(false), false, target, null);
            }

            run = await AddCostAsync(run, reply.CostUsd, ct).ConfigureAwait(false);
            if (OverBudget(run))
            {
                return new ColleagueAsk(await StopForBudgetAsync(run, target, ct).ConfigureAwait(false), false, target, null);
            }

            var answer = StepSchemas.ParseAsk(reply.StructuredJson, reply.Text);
            if (answer.Answered)
            {
                await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Answer, target, a.Agent, answer.Answer!.Trim(), a.Task.Id, a.Stage.Id, Ref: reference, Subject: "answer"), ct).ConfigureAwait(false);
                Publish(SceneEventTypes.Meet, new { from = target, to = a.Agent, kind = "handoff", run = run.Id });
                PublishAgent(run, target, "done", "cevapladı", a.Task.Id);
                return new ColleagueAsk(run, true, target, null);
            }

            var reason = string.IsNullOrWhiteSpace(answer.Reason) ? "karar yetkisi dışında" : answer.Reason.Trim();
            await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, target, "user", $"{asker.Name} sorusunu kullanıcıya yükseltti: {reason}", a.Task.Id, a.Stage.Id, Ref: reference, Subject: "escalate"), ct).ConfigureAwait(false);
            PublishAgent(run, target, "idle", null, null);
            return new ColleagueAsk(run, false, target, reason);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LimitReachedException)
        {
            throw; // adim limit fazi olur; surdurmede developer yeniden kosar (soru kaydi durur, ikinci takilma kullaniciya)
        }
        catch (Exception ex)
        {
            // Hedef cevaplayamadi (sema, saglayici): akis durmaz, soru kullaniciya duser; hata gunlukte.
            await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, target, "user", ex.Message, a.Task.Id, a.Stage.Id, Subject: "error"), ct).ConfigureAwait(false);
            PublishAgent(run, target, "idle", null, null);
            return new ColleagueAsk(run, false, target, $"cevaplayamadı ({Ellipsis(ex.Message, 120)})");
        }
    }

    /// <summary>Akis takildi: calisma <c>AwaitingInput</c>, soru run.json'da, kayit messages.jsonl'de (ask, ref). Bildirim zili bunu gosterir.</summary>
    private async Task<Run> AskUserAsync(Run run, string agent, string text, IReadOnlyList<QuestionOption> options, Assignment a, string? context, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var question = new UserQuestion(now, agent, text, options, a.Task.Id, a.Stage.Id, context is null ? null : Ellipsis(context, 600));
        run = run with { Status = RunStatus.AwaitingInput, Question = question, Detail = Ellipsis($"senden cevap bekleniyor · {a.Task.Id}: {text}", 200) };
        await runs.UpdateAsync(run, ct).ConfigureAwait(false);
        await runs.AppendMessageAsync(run.Id, new Message(now, MessageKind.Ask, agent, "user", text, a.Task.Id, a.Stage.Id, Ref: $"q-{now.UtcTicks}", Subject: "question"), ct).ConfigureAwait(false);
        PublishAgent(run, agent, "waiting", "senden cevap bekliyor", a.Task.Id);
        return run;
    }

    public async Task<Run> BeginAnswerAsync(string runId, AnswerRequest answer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var run = await reader.GetAsync(runId, ct).ConfigureAwait(false);
        if (run.Status != RunStatus.AwaitingInput || run.Question is null)
        {
            throw new DomainException(ErrorCodes.RunNotAwaitingInput, $"Calisma '{run.Status}' durumunda; cevap yalniz AwaitingInput'ta.");
        }

        var q = run.Question;
        var option = q.Options.FirstOrDefault(o => o.Id == answer.Choice?.Trim())
            ?? throw new DomainException(ErrorCodes.RunInvalidChoice, $"Secenek yok: '{answer.Choice}'. Gecerli: {string.Join(", ", q.Options.Select(o => o.Id))}.");
        if (option.NeedsNote && string.IsNullOrWhiteSpace(answer.Note))
        {
            throw new DomainException(ErrorCodes.RunNoteEmpty, $"'{option.Label}' icin not gerekli.");
        }

        var ask = (await runs.ReadMessagesAsync(run.Id, ct).ConfigureAwait(false)).LastOrDefault(m => m.Kind == MessageKind.Ask && m.To == "user" && m.Task == q.Task);
        await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Answer, "user", q.Agent, Prompts.UserAnswer(option.Label, answer.Note), q.Task, q.Stage, Ref: ask?.Ref, Subject: "answer"), ct).ConfigureAwait(false);

        switch (option.Id)
        {
            case "cancel":
                run = run with { Status = RunStatus.Running, Question = null }; // CancelAsync Running'i kabul eder
                await runs.UpdateAsync(run, ct).ConfigureAwait(false);
                return await CancelAsync(run.Id, ct).ConfigureAwait(false);

            case "skip":
            {
                // Adim gecildi / elle halledildi / oldugu gibi kabul: faz Skipped, dagitici sonraki adima gecer.
                var wf = await RequireWorkflowAsync(run.Id, ct).ConfigureAwait(false);
                var stage = wf.Stages.FirstOrDefault(s => s.Id == q.Stage);
                if (q.Task is not null && stage is not null)
                {
                    await runs.AppendPhaseAsync(run.Id, new Phase(DateTimeOffset.UtcNow, q.Task, stage.Id, stage.Title, stage.Kind.ToString().ToLowerInvariant(), "user", 0, PhaseStatus.Skipped, null, Ellipsis("kullanıcı: " + (answer.Note ?? option.Label), 200)), ct).ConfigureAwait(false);
                    Publish(SceneEventTypes.BoardMove, new { task = q.Task, stage = stage.Id, state = "queued", run = run.Id });
                }

                break;
            }

            default:
                break; // retry: not zaten yazildi; dagitici Failed → ayni adim, Rejected → developer
        }

        run = run with { Status = RunStatus.Running, Question = null, Detail = "dağıtım", Step = RunStep.Dispatch };
        await runs.UpdateAsync(run, ct).ConfigureAwait(false);
        PublishAgent(run, q.Agent, "idle", null, null);
        return run;
    }

    /// <summary>Limit korumasi: calisma bekler (Paused + ResumeAt), pencere sifirlaninca Api surdurur; kullanici isterse hemen "yeniden dene".</summary>
    private async Task<Run> PauseForLimitAsync(Run run, string agent, string where, LimitReachedException ex, CancellationToken ct)
    {
        var resume = ex.ResumeAt ?? DateTimeOffset.UtcNow.AddMinutes(15);
        run = run with { Status = RunStatus.Paused, ResumeAt = resume, Detail = Ellipsis($"{where} · limit: {ex.Message}", 300), WaitingSince = null };
        await runs.UpdateAsync(run, ct).ConfigureAwait(false);
        await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, agent, "user", $"{where}: {ex.Message}", Subject: "limit"), ct).ConfigureAwait(false);
        PublishAgent(run, agent, "waiting", "limit doldu, bekliyor", null);
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
        catch (LimitReachedException ex)
        {
            return await PauseForLimitAsync(run, organizer, $"devir ({a.Task.Id})", ex, ct).ConfigureAwait(false);
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
            if (run.Status != RunStatus.Running)
            {
                continue;
            }

            if (run.WaitingSince is not null)
            {
                // Ajan bekliyordu, ortada yarim kalan LLM cagrisi yok: kesinti degil, dagitim kuyruga geri girer.
                scheduler.Schedule(run.Id, RunStep.Dispatch);
                continue;
            }

            {
                await runs.UpdateAsync(run with { Status = RunStatus.Interrupted, FinishedAt = DateTimeOffset.UtcNow, Detail = "süreç yeniden başladı" }, ct).ConfigureAwait(false);
                count++;

                // Yarim kalan adim Started kalirsa dagitici ona hic dokunmaz (Started → null) ve "yeniden dene" sonsuza kadar bekler.
                foreach (var id in await runs.ListTasksAsync(run.Id, ct).ConfigureAwait(false))
                {
                    var phases = await runs.ReadPhasesAsync(run.Id, id, ct).ConfigureAwait(false);
                    if (phases.Count > 0 && phases[^1].Status == PhaseStatus.Started)
                    {
                        await runs.AppendPhaseAsync(run.Id, phases[^1] with { Ts = DateTimeOffset.UtcNow, Status = PhaseStatus.Failed, Detail = "süreç yeniden başladı", Cause = PhaseCause.Interrupted }, ct).ConfigureAwait(false);
                    }
                }
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
