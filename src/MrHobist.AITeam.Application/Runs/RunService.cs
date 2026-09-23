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
    IRunScheduler scheduler,
    IHistoryCompactor? compactor = null,
    IWorkspaceSnapshot? snapshots = null) : IRunService
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

        // Proje butcesi doluysa yeni is HIC baslamaz: baslayip ilk turdan sonra BudgetExceeded olmasi
        // bir tur token'i bosa harcar ve gelen kutusuna olu bir is birakir.
        if (project.MaxCostUsd is not null || project.MaxTokens is not null)
        {
            var spent = await reader.ListAsync(10_000, ct, project.Key).ConfigureAwait(false);
            if (project.MaxCostUsd is { } capCost && spent.Sum(r => r.TotalCostUsd) >= capCost)
            {
                throw new DomainException(ErrorCodes.ProjectBudgetExceeded, $"'{project.Key}' proje bütçesi dolu: ${spent.Sum(r => r.TotalCostUsd):0.####} / ${capCost:0.####}.");
            }

            if (project.MaxTokens is { } capTokens && spent.Sum(r => r.TotalTokens) >= capTokens)
            {
                throw new DomainException(ErrorCodes.ProjectBudgetExceeded, $"'{project.Key}' proje bütçesi dolu: {spent.Sum(r => r.TotalTokens):N0} / {capTokens:N0} token.");
            }
        }

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

            run = Accrue(run, reply);
            if (OverBudget(run))
            {
                return await StopForBudgetAsync(run, analyze.Role, null, ct).ConfigureAwait(false);
            }

            if (await ProjectOverBudgetAsync(run, ct).ConfigureAwait(false) is { } overP)
            {
                return await StopForBudgetAsync(run, analyze.Role, overP, ct).ConfigureAwait(false);
            }

            // Plani kim onaylar: akis soyler (docs/DOMAIN.md -> Plan onayi).
            if (wf.PlanApproverAgent is { } approver)
            {
                PublishAgent(run, analyze.Role, "done", $"{spec.Tasks.Count} görev · onaya gitti", "plan");
                return await ApprovePlanByAgentAsync(run, wf, approver, spec, notes.Count + 1, ct).ConfigureAwait(false);
            }

            // `auto`: onay kapisi yok, plan uretilir uretilmez dagitima gecilir. Kullanici yalniz
            // takilan ajanin sorusunda devreye girer (2026-09-21 karari).
            if (!wf.PlanNeedsUser)
            {
                PublishAgent(run, analyze.Role, "done", $"{spec.Tasks.Count} görev · dağıtıma geçildi", "plan");
                run = run with { Status = RunStatus.Running, Detail = "dağıtım", Step = RunStep.Dispatch };
                await runs.UpdateAsync(run, ct).ConfigureAwait(false);
                // Kuyruga KOY: analiz zaten bir isin icinde kosuyor, donusu kendiliginden yeni is uretmez.
                // (Kullanici onayinda bunu ucun cagrisi yapiyordu: BeginApproveAsync -> ScheduleAndAccept.)
                scheduler.Schedule(run.Id, RunStep.Dispatch);
                return run;
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

    /// <summary>
    /// Plani ajan onaylar (akis <c>planApprover</c>). Kabul ederse dagitima gecilir; red ederse geri bildirim
    /// revize notu olarak yazilir ve analiz yeniden kosar -- kullanicinin "Revize et" akisinin birebir ayni yolu.
    /// Tur limiti: <c>maxReviewRounds</c>. Asilirsa karar KULLANICIYA birakilir (sonsuz donguye girilmez).
    /// </summary>
    private async Task<Run> ApprovePlanByAgentAsync(Run run, Workflow wf, string approver, Spec spec, int round, CancellationToken ct)
    {
        var analyze = wf.Stages[0];
        if (round > wf.MaxReviewRounds)
        {
            run = run with { Status = RunStatus.AwaitingApproval, Detail = $"plan onay bekliyor ({approver} {wf.MaxReviewRounds} turda onaylamadı)", Step = RunStep.Approval };
            await runs.UpdateAsync(run, ct).ConfigureAwait(false);
            return run;
        }

        PublishAgent(run, approver, "working", "plan inceleniyor", "plan");
        var reply = await caller.CallAsync(run, approver, [new RuntimeMessage("user", Prompts.PlanApproval(run, spec, round))], StepSchemas.Review, analyze.Id, null, round, ct).ConfigureAwait(false);
        if (await WasCancelledAsync(run.Id, ct).ConfigureAwait(false))
        {
            return await reader.GetAsync(run.Id, ct).ConfigureAwait(false);
        }

        run = await AddCostAsync(run, reply, ct).ConfigureAwait(false);
        if (OverBudget(run))
        {
            return await StopForBudgetAsync(run, approver, null, ct).ConfigureAwait(false);
        }

        if (await ProjectOverBudgetAsync(run, ct).ConfigureAwait(false) is { } overApproval)
        {
            return await StopForBudgetAsync(run, approver, overApproval, ct).ConfigureAwait(false);
        }

        var report = StepSchemas.ParseReview(reply.StructuredJson, reply.Text);
        if (report.Accepted)
        {
            await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, approver, analyze.Role, $"Plan onaylandı. {report.Feedback}".Trim(), Stage: analyze.Id, Subject: "plan-approved"), ct).ConfigureAwait(false);
            PublishAgent(run, approver, "done", "plan onaylandı", "plan");
            run = run with { Status = RunStatus.Running, Detail = "dağıtım", Step = RunStep.Dispatch };
            await runs.UpdateAsync(run, ct).ConfigureAwait(false);
            scheduler.Schedule(run.Id, RunStep.Dispatch);
            return run;
        }

        var feedback = report.Feedback + (report.Findings.Count == 0 ? "" : "\n\nBulgular:\n" + string.Join("\n", report.Findings.Select(f => "- " + f)));
        await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, approver, analyze.Role, feedback.Trim(), Stage: analyze.Id, Subject: PlanRevisionSubject), ct).ConfigureAwait(false);
        Publish(SceneEventTypes.Meet, new { from = approver, to = analyze.Role, kind = "reject", run = run.Id });
        PublishAgent(run, approver, "done", "plan reddedildi", "plan");
        run = run with { Status = RunStatus.Running, Detail = "plan revize", Step = RunStep.Analyze };
        await runs.UpdateAsync(run, ct).ConfigureAwait(false);
        scheduler.Schedule(run.Id, RunStep.Analyze);
        return run;
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

        return Alternate(list);
    }

    /// <summary>Ardisik ayni roller birlestirilir: saglayicilar user/assistant sirasi ister.</summary>
    private static List<RuntimeMessage> Alternate(List<RuntimeMessage> list)
    {
        var merged = new List<RuntimeMessage>(list.Count);
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

    /// <summary>
    /// Ajanin BU GOREVDEKI kendi gecmisi + yeni istem. Ayni ajan ardisik adimlarda kostugunda
    /// (tek kisilik akista gelistirme → test, ya da red sonrasi ikinci tur) baglamini SIFIRDAN
    /// kurmasi gerekmez.
    ///
    /// Olculdu 2026-09-21 (ayni brief, ayni model/efor): tek kisilik akis 55 ic tur ve $1,38 ederken
    /// Claude Code ayni isi tek surekli konusmada 16 ic tur ve $0,43'e yapiyordu. Fark onbellek DEGILDI
    /// (%92,9 ≈ %93,4); fark, her adimin tek mesajlik gecmisle baslamasiydi: test adimi, gelistirme
    /// adiminin AZ ONCE yazdigi dosyalari sifirdan okuyup dogruluyordu (19 ic tur).
    ///
    /// Tasinan sey ajanin kendi CIKTISIDIR, onceki istemi degil: istem zaten gorev baglamini
    /// (plan, kurallar, notlar) tasiyor, tekrari her turda bedel oduretir. Kapsam GOREV basina:
    /// baska gorevin gecmisi tasinmaz.
    ///
    /// Token olcusu ajanin modeli icin kayitli turlardan kalibre edilir (<see cref="TokenCalibration"/>); sikistirma
    /// oncesi/sonrasi olcu <see cref="ContextStats"/> olarak tura yazilir (docs/DOMAIN.md → Baglam butcesi).
    /// </summary>
    private async Task<TaskHistory> AgentTaskHistoryAsync(Run run, Assignment a, string prompt, CancellationToken ct)
    {
        var prior = (await runs.ReadTurnsAsync(run.Id, a.Agent, ct).ConfigureAwait(false))
            .Where(t => t.Task == a.Task.Id && !string.IsNullOrWhiteSpace(t.Output))
            .ToList();

        if (prior.Count == 0)
        {
            return new TaskHistory([new RuntimeMessage("user", prompt)], null);
        }

        var carried = new List<RuntimeMessage>(prior.Count * 2);
        foreach (var t in prior)
        {
            var label = $"[{a.Task.Id} · {t.Stage ?? "önceki adım"}{(t.Round is { } r ? $" · tur {r}" : "")}] Bu adımda verdiğin çıktı:";
            carried.Add(new RuntimeMessage("user", label));
            carried.Add(new RuntimeMessage("assistant", t.Output!));
        }

        // Tasinan gecmis red turlariyla buyur: butceyi asarsa en eski turlar duser.
        // Yeni istem sikistirmaya GIRMEZ (son mesaj daima tam). Politika: CompactionBudget.TaskHistory.
        var cpt = await CalibrateAsync(a.Agent, ct).ConfigureAwait(false);
        var budget = CompactionBudget.TaskHistory with { CharsPerToken = cpt.Value };
        var kept = await (compactor ?? new NoCompaction()).CompactAsync(carried, budget, ct).ConfigureAwait(false);
        var context = new ContextStats(carried.Count, carried.Sum(m => m.Content.Length), kept.Count, kept.Sum(m => m.Content.Length), cpt.Value, cpt.Samples);

        var list = new List<RuntimeMessage>(kept.Count + 1);
        list.AddRange(kept);
        list.Add(new RuntimeMessage("user", prompt));
        return new TaskHistory(Alternate(list), context);
    }

    private sealed record TaskHistory(IReadOnlyList<RuntimeMessage> Messages, ContextStats? Context);

    /// <summary>Ajanin hedef modeli icin karakter/token orani; olcum yetersizse varsayilan (docs/DOMAIN.md → Baglam butcesi).</summary>
    private async Task<CharsPerToken> CalibrateAsync(string agentKey, CancellationToken ct)
    {
        var team = await agents.LoadTeamAsync(ct).ConfigureAwait(false);
        if (!team.Agents.TryGetValue(agentKey, out var agent))
        {
            return CharsPerToken.Default; // cagri zaten WorkflowUnknownRole ile duser; olcu icin hata uretme
        }

        var target = AgentTarget.Of(agent);
        var samples = await runs.ReadCalibrationSamplesAsync(Providers.Wire(target.Provider), target.Model, TokenCalibration.Window, ct).ConfigureAwait(false);
        return TokenCalibration.Fit(samples);
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
            if (assignments.Count == 0 && busy.Count == 0 && Dispatcher.Plan(wf, spec, phasesByTask, new HashSet<string>()).Count == 0)
            {
                // Kimse dolu degil ve yine de hazir gorev yok: bekleme degil TAKILMA. Once "ajan bekleniyor" diye sessizce
                // asili kaliyordu (2026-09-23, Skipped sayilmayinca). Gorunur dus; karar kullanicinin ("Yeniden dene").
                run = run with { Status = RunStatus.Failed, FinishedAt = DateTimeOffset.UtcNow, WaitingSince = null, Detail = "ilerleyebilecek görev yok (dağıtım takıldı)" };
                await runs.UpdateAsync(run, ct).ConfigureAwait(false);
                return run;
            }

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
                var handoffNotes = (await runs.ReadMessagesAsync(run.Id, ct).ConfigureAwait(false)).Where(m => m.Task == a.Task.Id).ToList();
                run = await HandoffAsync(run, a, organizer, team, round, handoffNotes, ct).ConfigureAwait(false);
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
                    // Yazmadan ONCEKI hal: red tavaninda kullanici "geri al" derse donulecek nokta (ornek: opencode snapshot).
                    var before = await (snapshots ?? new NoWorkspaceSnapshot()).TrackAsync(root, ct).ConfigureAwait(false);
                    // Onceki deneme yarida kesildiyse (zaman asimi, yeniden baslatma) dizinde onun isi var: ajan bastan yazmasin, devam etsin.
                    var cutShort = (await runs.ReadPhasesAsync(run.Id, a.Task.Id, ct).ConfigureAwait(false))
                        .LastOrDefault(p => p.Stage == a.Stage.Id && p.Status != PhaseStatus.Started) is { IsCutShort: true };
                    var history = await AgentTaskHistoryAsync(run, a, Prompts.ImplementTask(spec, a, root, notes, round, cutShort), ct).ConfigureAwait(false);
                    var reply = await caller.CallAsync(run, a.Agent, history.Messages, StepSchemas.Implement, a.Stage.Id, a.Task.Id, round, ct, tools, history.Context).ConfigureAwait(false);
                    if (await WasCancelledAsync(run.Id, ct).ConfigureAwait(false))
                    {
                        return await reader.GetAsync(run.Id, ct).ConfigureAwait(false);
                    }

                    run = await AddCostAsync(run, reply, ct).ConfigureAwait(false);
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
                        var asked = await AskColleagueAsync(run, wf, spec, a, q, body, notes, root, round, ct).ConfigureAwait(false);
                        run = asked.Run;
                        if (run.Status != RunStatus.Running)
                        {
                            return run;
                        }

                        if (asked.Answered)
                        {
                            await ClosePhaseAsync(run, wf, a, round, PhaseStatus.Failed, started, Ellipsis($"soru → {asked.Target}: {q}", 200), ct, snapshot: before).ConfigureAwait(false);
                            break; // dagitici Failed → ayni adim; cevap notlar arasinda gider. Ust uste hata tavani asagida korur.
                        }

                        await ClosePhaseAsync(run, wf, a, round, PhaseStatus.Failed, started, Ellipsis("soru: " + q, 200), ct, snapshot: before).ConfigureAwait(false);
                        var context = asked.EscalateReason is null ? body : $"{body}\n\n{asked.Target}: {asked.EscalateReason}";
                        return await AskUserAsync(run, a.Agent, q, Options("Cevapla ve yeniden dene", "Cevabın developer'a not olarak gider; görev aynı adımdan sürer.", "Bu adımı geç", "Elle hallettim ya da gerekmiyor: görev bir sonraki adıma geçer.", retryNeedsNote: true), a, context, ct).ConfigureAwait(false);
                    }

                    await ClosePhaseAsync(run, wf, a, round, PhaseStatus.Done, started, Ellipsis(report.Summary, 200), ct, snapshot: before).ConfigureAwait(false);
                    PublishAgent(run, a.Agent, "done", Ellipsis(report.Summary, 40), a.Task.Id);
                    break;
                }

                case StageKind.Review:
                {
                    // Kapinin beklettiği uretici adim: hem istemi sekillendirir (kod mu, tasarim mi) hem de redde geri donus hedefi.
                    var producer = wf.ProducerBefore(a.Stage);
                    var history = await AgentTaskHistoryAsync(run, a, Prompts.ReviewTask(spec, a, root, notes, a.Stage, producer, round, wf.MaxReviewRounds), ct).ConfigureAwait(false);
                    var reply = await caller.CallAsync(run, a.Agent, history.Messages, StepSchemas.Review, a.Stage.Id, a.Task.Id, round, ct, tools, history.Context).ConfigureAwait(false);
                    if (await WasCancelledAsync(run.Id, ct).ConfigureAwait(false))
                    {
                        return await reader.GetAsync(run.Id, ct).ConfigureAwait(false);
                    }

                    run = await AddCostAsync(run, reply, ct).ConfigureAwait(false);
                    var report = StepSchemas.ParseReview(reply.StructuredJson, reply.Text);
                    var developer = producer.Role;
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
                        var options = Options("Bir tur daha", "Notunla birlikte developer'a geri gider; sonraki redde yine sorulur.", "Olduğu gibi kabul et", $"{a.Stage.Title} adımı geçilir; görev bir sonraki adıma geçer.", retryNeedsNote: false);
                        return await AskUserAsync(run, a.Agent, q, await WithRevertAsync(run, a.Task.Id, producer, options, ct).ConfigureAwait(false), a, feedback, ct).ConfigureAwait(false);
                    }

                    break;
                }

                case StageKind.Design:
                {
                    var history = await AgentTaskHistoryAsync(run, a, Prompts.DesignTask(spec, a, root, notes), ct).ConfigureAwait(false);
                    var reply = await caller.CallAsync(run, a.Agent, history.Messages, StepSchemas.Design, a.Stage.Id, a.Task.Id, round, ct, tools, history.Context).ConfigureAwait(false);
                    if (await WasCancelledAsync(run.Id, ct).ConfigureAwait(false))
                    {
                        return await reader.GetAsync(run.Id, ct).ConfigureAwait(false);
                    }

                    run = await AddCostAsync(run, reply, ct).ConfigureAwait(false);
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
                return await StopForBudgetAsync(run, a.Agent, null, ct).ConfigureAwait(false);
            }

            if (await ProjectOverBudgetAsync(run, ct).ConfigureAwait(false) is { } overStage)
            {
                return await StopForBudgetAsync(run, a.Agent, overStage, ct).ConfigureAwait(false);
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
        catch (RuntimeTimeoutException ex)
        {
            // Sistem kaynakli: tur sayilmaz, tavana girmez. "Yeniden dene" ayni adimi "devam et" notuyla surdurur (IsCutShort).
            await ClosePhaseAsync(run, wf, a, round, PhaseStatus.Failed, started, Ellipsis("zaman aşımı: " + ex.Message, 200), ct, PhaseCause.Timeout).ConfigureAwait(false);
            return await FailAsync(run, a.Agent, a.Stage.Id, a.Task.Id, $"{a.Stage.Title} ({a.Task.Id}) zaman aşımı — yazılanlar diskte, \"Yeniden dene\" kaldığı yerden sürdürür", ex, ct).ConfigureAwait(false);
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

    private async Task ClosePhaseAsync(Run run, Workflow wf, Assignment a, int round, PhaseStatus status, DateTimeOffset started, string? detail, CancellationToken ct, PhaseCause? cause = null, string? snapshot = null)
    {
        var phase = new Phase(DateTimeOffset.UtcNow, a.Task.Id, a.Stage.Id, a.Stage.Title, a.Stage.Kind.ToString().ToLowerInvariant(), a.Agent, round, status, (DateTimeOffset.UtcNow - started).TotalSeconds, detail, cause, snapshot);
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

    /// <summary>Turun maliyeti ve token'lari calismaya eklenir (proje butcesi bu toplami okur).</summary>
    private static Run Accrue(Run run, AgentReply reply) => run with
    {
        TotalCostUsd = run.TotalCostUsd + reply.CostUsd,
        InputTokens = run.InputTokens + reply.InputTokens,
        OutputTokens = run.OutputTokens + reply.OutputTokens,
    };

    private async Task<Run> AddCostAsync(Run run, AgentReply reply, CancellationToken ct)
    {
        run = Accrue(run, reply);
        await runs.UpdateAsync(run, ct).ConfigureAwait(false);
        return run;
    }

    // ------------------------------------------------------------------ takilma: soru ve cevap (docs/DOMAIN.md → Takilma)

    /// <summary>Ajan → ajan sorusunun sonucu: cevaplandi (not yazildi) · yukseltildi (sebep kullaniciya) · hedef yok (kullaniciya).</summary>
    private sealed record ColleagueAsk(Run Run, bool Answered, string? Target, string? EscalateReason);

    /// <summary>
    /// Takilan ajanin sorusu <c>can_ask</c> hedefine (ajan md'si; bugun manager): 1 LLM turu, yalniz okuma araci. Kayit
    /// Mesaj kaydinda <c>ask</c> (soran → hedef) + <c>answer</c> (hedef → soran, ayni ref); sonraki turda notlar arasinda gider.
    /// Kural (varsayimla): hedef ayni gorevde BIR kez sorulur — ikinci takilma kullaniciya gider. Yukseltme (escalate) ya da
    /// hedefin hatasi kullaniciya duser; limit dogrudan yukari cikar (adim limit fazi olur).
    /// </summary>
    private async Task<ColleagueAsk> AskColleagueAsync(Run run, Workflow wf, Spec spec, Assignment a, string question, string report, IReadOnlyList<Message> notes, string root, int round, CancellationToken ct)
    {
        var team = await agents.LoadTeamAsync(ct).ConfigureAwait(false);
        team.Agents.TryGetValue(a.Agent, out var asker);

        // Soruyu KIM cevaplar: akisin karari (acik karar #4). null = ajanin can_ask'i (eski davranis),
        // "user" = dogrudan kullaniciya, ajan anahtari = o ajan. Boylece manager'i olmayan bir akis
        // manager'i (ve maliyetini) ise sokmaz.
        var target = wf.AskRole switch
        {
            null => asker?.CanAsk,
            Workflow.UserRole => null,
            var role => role,
        };

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

            run = await AddCostAsync(run, reply, ct).ConfigureAwait(false);
            if (OverBudget(run))
            {
                return new ColleagueAsk(await StopForBudgetAsync(run, target, null, ct).ConfigureAwait(false), false, target, null);
            }

            if (await ProjectOverBudgetAsync(run, ct).ConfigureAwait(false) is { } overAsk)
            {
                return new ColleagueAsk(await StopForBudgetAsync(run, target, overAsk, ct).ConfigureAwait(false), false, target, null);
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

    /// <summary>Akis takildi: calisma <c>AwaitingInput</c>, soru calisma satirinda, kayit mesajlarda (ask, ref). Bildirim zili bunu gosterir.</summary>
    /// <summary>
    /// Geri alma secenegini yalniz DONULECEK BIR HAL varsa ekler: uretici adimin kaydedilmis anlik goruntusu yoksa
    /// (git kurulu degil, ilk tur, kayit basarisiz) kullaniciya tutmayacagi bir soz verilmez. Seceneğin yeri iptalden
    /// oncedir: "kapat" listenin sonunda kalir.
    /// </summary>
    private async Task<IReadOnlyList<QuestionOption>> WithRevertAsync(Run run, string taskId, Stage producer, IReadOnlyList<QuestionOption> options, CancellationToken ct)
    {
        var point = await LastSnapshotAsync(run.Id, taskId, producer.Id, ct).ConfigureAwait(false);
        if (point is null)
        {
            return options;
        }

        var revert = new QuestionOption("revert", "Son turu geri al ve yeniden dene",
            $"\"{producer.Title}\" adımının son turda yazdıkları SILINIR, dizin o turdan önceki haline döner; görev notunla birlikte o adımdan yeniden başlar.", NeedsNote: true);
        return [.. options.Where(o => o.Id != "cancel"), revert, .. options.Where(o => o.Id == "cancel")];
    }

    /// <summary>Bir gorevin bir adimindaki son kaydedilmis calisma alani hali; yoksa null.</summary>
    private async Task<string?> LastSnapshotAsync(string runId, string taskId, string stageId, CancellationToken ct)
        => (await runs.ReadPhasesAsync(runId, taskId, ct).ConfigureAwait(false))
            .LastOrDefault(p => p.Stage == stageId && !string.IsNullOrWhiteSpace(p.Snapshot))?.Snapshot;

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

            case "revert":
            {
                // YIKICI ve yalniz burada: kullanici acikca sectiginde calisir (bkz. IWorkspaceSnapshot).
                // Sonrasi "retry" ile ayni: faz Rejected kaldigi icin dagitici zaten uretici adima doner.
                var wf = await RequireWorkflowAsync(run.Id, ct).ConfigureAwait(false);
                var gate = wf.Stages.FirstOrDefault(s => s.Id == q.Stage);
                var producer = gate is null ? null : Workflow.ProducerBefore(wf.Stages, gate.Id);
                var point = q.Task is null || producer is null ? null : await LastSnapshotAsync(run.Id, q.Task, producer.Id, ct).ConfigureAwait(false);
                var root = await RootAsync(run, ct).ConfigureAwait(false);
                var done = point is not null && await (snapshots ?? new NoWorkspaceSnapshot()).RestoreAsync(root, point, ct).ConfigureAwait(false);

                // Basarisizsa calisma durmaz ama kullanici YANLIS bilgilenmesin: dizinin donmedigi ajana da yazilir.
                var note = done
                    ? $"Kullanıcı çalışma alanını \"{producer!.Title}\" adımının son turundan önceki haline döndürdü; o turda yazılanlar silindi."
                    : "Kullanıcı geri almak istedi ama çalışma alanı döndürülemedi: dizin son turun bıraktığı hâlde. Dosyaların şu anki durumunu kendin kontrol et.";
                await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, "user", producer?.Role ?? q.Agent, note, q.Task, producer?.Id ?? q.Stage, Subject: "revert"), ct).ConfigureAwait(false);
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

    /// <summary>
    /// Organizator devri: ajanlar arasi AKTARIM, LLM turu DEGIL (kullanici karari 2026-09-21).
    /// Not kodda uretilir (<see cref="Prompts.HandoffNote"/>), maliyet cikmaz, butce kontrolu gerekmez.
    /// Kayit ve sahne olayi aynen kalir: devrin kim tarafindan kime yapildigi gecmiste gorunur.
    /// </summary>
    private async Task<Run> HandoffAsync(Run run, Assignment a, string organizer, Team team, int round, IReadOnlyList<Message> notes, CancellationToken ct)
    {
        var toName = team.Agents.TryGetValue(a.Agent, out var toAgent) ? toAgent.Name : a.Agent;
        PublishAgent(run, organizer, "working", $"{a.Task.Id} → {toName}", a.Task.Id);

        var note = Prompts.HandoffNote(a, toName, round, notes);
        await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Handoff, organizer, a.Agent, note, a.Task.Id, a.Stage.Id, Subject: "handoff"), ct).ConfigureAwait(false);
        Publish(SceneEventTypes.Meet, new { from = organizer, to = a.Agent, kind = "handoff", run = run.Id });
        PublishAgent(run, organizer, "idle", null, null);
        return run;
    }

    // ------------------------------------------------------------------ butce / hata / kurtarma

    private static bool OverBudget(Run run) => run.MaxCostUsd is { } max && run.TotalCostUsd > max;

    /// <summary>
    /// Proje butcesi (2026-09-22 kullanici karari, docs/DOMAIN.md → Butce ve limit). Is butcesinden farki KAPSAMDIR:
    /// tek is degil, projenin butun calismalarinin toplami. Iki olcu bagimsizdir ($ ve token) -- abonelikte ucret
    /// kesilmedigi icin asil tukenen token'dir, ama esdeger maliyet de raporlanir; ONCE DOLAN durdurur.
    ///
    /// Is butcesi gibi tur SONRASI bakilir: bir turun tuketimi tur bitmeden bilinmez, dolayisiyla on kontrol de
    /// bir turluk asma payini kaldiramaz. Ayni karari iki yerde tutmamak icin tek kapi.
    /// Butce yoksa (ikisi de null) depo hic okunmaz: sinirsiz olan projede maliyet sifirdir.
    /// </summary>
    /// <returns>Asildiysa kullaniciya gosterilecek sebep; asilmadiysa <c>null</c>.</returns>
    private async Task<string?> ProjectOverBudgetAsync(Run run, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(run.Project))
        {
            return null;
        }

        var project = await projects.LoadAsync(run.Project, ct).ConfigureAwait(false);
        if (project.MaxCostUsd is null && project.MaxTokens is null)
        {
            return null;
        }

        // Elimizdeki `run` depodaki kopyasindan daha guncel olabilir (tur az once islendi): kendi satirini
        // depodan degil bellekten say, yoksa son tur toplamdan duser ve butce bir tur gec devreye girer.
        var others = (await reader.ListAsync(10_000, ct, project.Key).ConfigureAwait(false)).Where(r => r.Id != run.Id).ToList();
        var cost = others.Sum(r => r.TotalCostUsd) + run.TotalCostUsd;
        var tokens = others.Sum(r => r.TotalTokens) + run.TotalTokens;

        if (project.MaxCostUsd is { } maxCost && cost > maxCost)
        {
            return $"proje bütçesi aşıldı: ${cost:0.####} > ${maxCost:0.####} (proje '{project.Key}')";
        }

        if (project.MaxTokens is { } maxTokens && tokens > maxTokens)
        {
            return $"proje bütçesi aşıldı: {tokens:N0} token > {maxTokens:N0} (proje '{project.Key}')";
        }

        return null;
    }

    /// <param name="reason">Proje butcesi sebebi; <c>null</c> ise is butcesi asilmistir.</param>
    private async Task<Run> StopForBudgetAsync(Run run, string agent, string? reason, CancellationToken ct)
    {
        run = run with
        {
            Status = RunStatus.BudgetExceeded,
            FinishedAt = DateTimeOffset.UtcNow,
            Detail = reason ?? $"bütçe aşıldı: ${run.TotalCostUsd:0.####} > ${run.MaxCostUsd:0.####}",
        };
        await runs.UpdateAsync(run, ct).ConfigureAwait(false);
        await runs.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, agent, "user", run.Detail!, Subject: "error"), ct).ConfigureAwait(false);
        PublishAgent(run, agent, "blocked", "bütçe aşıldı", null);
        return run;
    }

    /// <summary>Hata gunluge de yazilir (mesaj kaydi, subject: error): "takildi" tek basina bilgi degildir (LESSONS).</summary>
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
