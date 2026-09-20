using System.Text.Json;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Common;
using MrHobist.AITeam.Application.Runs;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Projects;
using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>
/// Faz 4a "biten sayilir": Python calismadan, sahte runtime ile analiz → onay/revize → dagitim → Paused akisi
/// ve runs/ dosyalari (docs/DOMAIN.md, docs/PHASES.md).
/// </summary>
public sealed class RunServiceTests : IDisposable
{
    private readonly StorageFixture _fx = new();
    private readonly FakeRuntime _runtime = new();
    private readonly FakeScene _scene = new();
    private readonly JsonlRunStore _store;
    private readonly RunReader _reader;
    private readonly JsonProjectStore _projects;
    private readonly RunService _svc;

    public RunServiceTests()
    {
        _store = new JsonlRunStore(_fx.Paths);
        var agents = new MarkdownAgentStore(_fx.Paths);
        var workflows = new JsonWorkflowStore(_fx.Paths);
        _reader = new RunReader(_store);
        _projects = new JsonProjectStore(_fx.Paths);
        _projects.SaveAsync(new Project("test", "Test", "", "default", "projects/test", Project.LocalOwner, DateTimeOffset.UtcNow), Ct).GetAwaiter().GetResult();
        _svc = new RunService(_store, workflows, agents, _projects, _reader, new AgentCaller(agents, _runtime, _store, _scene), _scene, new WorkspaceLocator(_fx.Paths));
    }

    public void Dispose() => _fx.Dispose();

    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Analiz_plani_uretir_ve_onay_bekler()
    {
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "Türkçe slugify fonksiyonu yaz"), Ct);
        Assert.Equal(RunStatus.Running, run.Status);
        Assert.Equal("default", run.Workflow);
        Assert.True(File.Exists(Path.Combine(_fx.Paths.RunsRoot, run.Id, "workflow.json")));

        run = await _svc.AnalyzeAsync(run.Id, Ct);

        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
        var spec = await _store.ReadSpecAsync(run.Id, Ct);
        Assert.NotNull(spec);
        Assert.Equal(["t1", "t2"], spec.Tasks.Select(t => t.Id));
        Assert.Empty(await _store.ListTasksAsync(run.Id, Ct)); // onaysiz panoya/faza is acilmaz

        // Varsayilanlar: analistin md'sinde model yok → claude-opus-5 + high (kullanici karari).
        var call = Assert.Single(_runtime.Calls);
        Assert.Equal(Provider.Anthropic, call.Provider);
        Assert.Equal("claude-opus-5", call.Model);
        Assert.Equal("high", call.ReasoningEffort);
        Assert.NotNull(call.SchemaJson);

        Assert.Contains(_scene.Events, e => e.Type == SceneEventTypes.WorkflowSet && e.Json.Contains("\"default\"", StringComparison.Ordinal));
        Assert.Contains(_scene.Events, e => e.Type == SceneEventTypes.AgentState && e.Json.Contains("\"analyst\"", StringComparison.Ordinal) && e.Json.Contains("\"done\"", StringComparison.Ordinal));
        Assert.DoesNotContain(_scene.Events, e => e.Type == SceneEventTypes.BoardSet);
    }

    [Fact]
    public async Task Revize_notu_gecmisle_analiste_gider_ve_plan_degisir()
    {
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        await _svc.AnalyzeAsync(run.Id, Ct);

        run = await _svc.BeginReviseAsync(run.Id, "t3 olarak dokümantasyon görevi ekle", Ct);
        Assert.Equal(RunStatus.Running, run.Status);
        run = await _svc.AnalyzeAsync(run.Id, Ct);

        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
        var spec = await _store.ReadSpecAsync(run.Id, Ct);
        Assert.Equal(3, spec!.Tasks.Count);

        // Ikinci cagri gecmisi tasir: [brief] → [onceki plan] → [not]; runtime hicbir sey hatirlamaz.
        var second = _runtime.Calls[1];
        Assert.Equal(["user", "assistant", "user"], second.Messages.Select(m => m.Role));
        Assert.Contains("dokümantasyon", second.Messages[2].Content, StringComparison.Ordinal);

        var note = Assert.Single(await _store.ReadMessagesAsync(run.Id, Ct));
        Assert.Equal("user", note.From);
        Assert.Equal("analyst", note.To);
        Assert.Equal("plan-revision", note.Subject);
    }

    [Fact]
    public async Task Onay_sonrasi_akis_ucuna_kadar_kosar_ve_tamamlanir()
    {
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        await _svc.AnalyzeAsync(run.Id, Ct);
        run = await _svc.BeginApproveAsync(run.Id, Ct);
        Assert.Equal(RunStatus.Running, run.Status);

        run = await _svc.DispatchAsync(run.Id, Ct);

        // t1 → t2, her biri gelistirme → test → karar; hepsi kabul → Completed.
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(["t1", "t2"], (await _store.ListTasksAsync(run.Id, Ct)).Order());
        var t1 = await _store.ReadPhasesAsync(run.Id, "t1", Ct);
        Assert.Equal(["gelistirme", "gelistirme", "test", "test", "karar", "karar"], t1.Select(p => p.Stage));
        Assert.Equal([PhaseStatus.Started, PhaseStatus.Done, PhaseStatus.Started, PhaseStatus.Done, PhaseStatus.Started, PhaseStatus.Done], t1.Select(p => p.Status));

        // Developer araclarla calisir: cagri arac listesi + proje dizini tasir. Organizator (devir) arac almaz.
        var dev = _runtime.Calls.First(c => c.Tools is { Count: > 0 } && c.SchemaJson!.Contains("filesChanged", StringComparison.Ordinal));
        Assert.Contains("Write", dev.Tools!);
        Assert.EndsWith(Path.Combine("projects", "test"), dev.Cwd!, StringComparison.Ordinal);
        Assert.True(Directory.Exists(dev.Cwd));
        var handoff = _runtime.Calls.First(c => c.Model == "claude-haiku-4-5-20251001");
        Assert.Null(handoff.Tools);

        // Rapor ve kabul notlari messages.jsonl'de; devir notu YALNIZ implement atamasinda (2 gorev → 2 not); arac kullanimi turda.
        var messages = await _store.ReadMessagesAsync(run.Id, Ct);
        Assert.Equal(2, messages.Count(m => m.Kind == MessageKind.Handoff));
        Assert.Equal(2, messages.Count(m => m.Subject == "implement-report"));
        Assert.Equal(4, messages.Count(m => m.Subject == "review-accept"));
        var devTurn = (await _store.ReadTurnsAsync(run.Id, "developer", Ct))[0];
        Assert.Equal("Write", Assert.Single(devTurn.ToolUses!).Tool);

        // Sahne: pano kuruldu, organizator developer'a yurudu, gorev sutun degistirdi; her faz kapanisinda not hedef sutuna gecer
        // (gelistirme bitti → test'te sirada; karar bitti → done). Pano canli kalir, yalniz atamada oynamaz.
        var types = _scene.Events.Select(e => e.Type).ToList();
        Assert.Contains(SceneEventTypes.BoardSet, types);
        var moves = _scene.Events.Where(e => e.Type == SceneEventTypes.BoardMove && e.Json.Contains("\"t1\"", StringComparison.Ordinal)).Select(e => e.Json).ToList();
        Assert.Contains(moves, j => j.Contains("\"stage\":\"test\"", StringComparison.Ordinal) && j.Contains("\"queued\"", StringComparison.Ordinal));
        Assert.Contains(moves, j => j.Contains("\"stage\":\"karar\"", StringComparison.Ordinal) && j.Contains("\"done\"", StringComparison.Ordinal));
        Assert.Contains(_scene.Events, e => e.Type == SceneEventTypes.Meet && e.Json.Contains("\"organizer\"", StringComparison.Ordinal) && e.Json.Contains("\"developer\"", StringComparison.Ordinal));
        Assert.True(types.IndexOf(SceneEventTypes.BoardSet) < types.IndexOf(SceneEventTypes.BoardMove));
    }

    [Fact]
    public async Task Red_developera_doner_tavan_asilinca_kullaniciya_sorulur_ve_cevap_akisi_surdurur()
    {
        _runtime.RejectTests = true; // testci hep reddeder
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        await _svc.AnalyzeAsync(run.Id, Ct);
        await _svc.BeginApproveAsync(run.Id, Ct);
        run = await _svc.DispatchAsync(run.Id, Ct);

        // default akis maxReviewRounds=3: 3 red → soru.
        Assert.Equal(RunStatus.AwaitingInput, run.Status);
        Assert.NotNull(run.Question);
        Assert.Equal(["retry", "skip", "cancel"], run.Question!.Options.Select(o => o.Id));
        Assert.Equal(("t1", "test", "tester"), (run.Question.Task, run.Question.Stage, run.Question.Agent));
        var t1 = await _store.ReadPhasesAsync(run.Id, "t1", Ct);
        Assert.Equal(3, t1.Count(p => p.Status == PhaseStatus.Rejected));
        Assert.Equal(3, t1.Count(p => p.Stage == "gelistirme" && p.Status == PhaseStatus.Done));
        var feedback = (await _store.ReadMessagesAsync(run.Id, Ct)).Where(m => m.Subject == "review-feedback").ToList();
        Assert.Equal(3, feedback.Count);
        // Red: not gelistirme sutununa "takildi" olarak doner (pano canli).
        Assert.Contains(_scene.Events, e => e.Type == SceneEventTypes.BoardMove && e.Json.Contains("\"stage\":\"gelistirme\"", StringComparison.Ordinal) && e.Json.Contains("\"blocked\"", StringComparison.Ordinal));
        Assert.All(feedback, m => Assert.Equal(("tester", "developer", "t1"), (m.From, m.To, m.Task)));
        // Ikinci developer turu geri bildirimi gordu.
        var devCalls = _runtime.Calls.Where(c => c.SchemaJson?.Contains("filesChanged", StringComparison.Ordinal) == true).ToList();
        Assert.Contains("review-feedback", devCalls[1].Messages[0].Content, StringComparison.Ordinal);

        // Gelen kutusu: soru. Gecersiz secim 400.
        var o = await _reader.GetOverviewAsync(Ct);
        var item = Assert.Single(o.Inbox);
        Assert.Equal(InboxKind.Question, item.Kind);
        var ex = await Assert.ThrowsAsync<DomainException>(() => _svc.BeginAnswerAsync(run.Id, new AnswerRequest("yok"), Ct));
        Assert.Equal(ErrorCodes.RunInvalidChoice, ex.ErrorCode);

        // "Oldugu gibi kabul et": test adimi Skipped, akis karara gecer; testci artik kabul etsin.
        _runtime.RejectTests = false;
        run = await _svc.BeginAnswerAsync(run.Id, new AnswerRequest("skip", "yeter"), Ct);
        Assert.Equal(RunStatus.Running, run.Status);
        Assert.Null(run.Question);
        Assert.Contains(await _store.ReadMessagesAsync(run.Id, Ct), m => m.Kind == MessageKind.Answer && m.From == "user" && m.To == "tester");
        run = await _svc.DispatchAsync(run.Id, Ct);
        Assert.Equal(RunStatus.Completed, run.Status);
        t1 = await _store.ReadPhasesAsync(run.Id, "t1", Ct);
        Assert.Contains(t1, p => p.Stage == "test" && p.Status == PhaseStatus.Skipped && p.Agent == "user");
        Assert.Equal(("karar", PhaseStatus.Done), (t1[^1].Stage, t1[^1].Status));
    }

    [Fact]
    public async Task Developer_engellenince_soru_gelir_cevap_developera_not_olur()
    {
        _runtime.BlockImplement = true;
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        await _svc.AnalyzeAsync(run.Id, Ct);
        await _svc.BeginApproveAsync(run.Id, Ct);
        run = await _svc.DispatchAsync(run.Id, Ct);

        Assert.Equal(RunStatus.AwaitingInput, run.Status);
        Assert.Equal("developer", run.Question!.Agent);
        Assert.Contains("i mi", run.Question.Text, StringComparison.Ordinal);
        var retry = run.Question.Options.Single(o => o.Id == "retry");
        Assert.True(retry.NeedsNote);
        var ex = await Assert.ThrowsAsync<DomainException>(() => _svc.BeginAnswerAsync(run.Id, new AnswerRequest("retry"), Ct));
        Assert.Equal(ErrorCodes.RunNoteEmpty, ex.ErrorCode);

        // can_ask: soru ONCE manager'a gitti (bir kez), developer manager'in cevabiyla yine takildi → kullaniciya.
        var messages = await _store.ReadMessagesAsync(run.Id, Ct);
        var ask = Assert.Single(messages, m => m.Kind == MessageKind.Ask && m.To == "manager");
        var managerAnswer = Assert.Single(messages, m => m.Kind == MessageKind.Answer && m.From == "manager");
        Assert.Equal(("developer", "developer", "t1"), (ask.From, managerAnswer.To, managerAnswer.Task));
        Assert.Equal(ask.Ref, managerAnswer.Ref);
        Assert.Contains(_scene.Events, e => e.Type == SceneEventTypes.Meet && e.Json.Contains("\"ask\"", StringComparison.Ordinal) && e.Json.Contains("\"manager\"", StringComparison.Ordinal));
        Assert.Single(_runtime.Calls, c => c.SchemaJson?.Contains("escalate", StringComparison.Ordinal) == true);

        _runtime.BlockImplement = false;
        run = await _svc.BeginAnswerAsync(run.Id, new AnswerRequest("retry", "küçük i kullan"), Ct);
        run = await _svc.DispatchAsync(run.Id, Ct);
        Assert.Equal(RunStatus.Completed, run.Status);
        // Manager cevabi developer'in 2. turunda, kullanici cevabi 3. turunda notlar arasinda; engel fazlari Failed, ayni adim.
        var devCalls = _runtime.Calls.Where(c => c.SchemaJson?.Contains("filesChanged", StringComparison.Ordinal) == true && c.Messages[0].Content.Contains("# Görev t1", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, devCalls.Count); // t1: engel, manager cevabiyla engel, kullanici cevabiyla bitti
        Assert.Contains("Küçük i kullan; Türkçe", devCalls[1].Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("küçük i kullan", devCalls[2].Messages[0].Content, StringComparison.Ordinal);
        var t1 = await _store.ReadPhasesAsync(run.Id, "t1", Ct);
        Assert.Equal([PhaseStatus.Started, PhaseStatus.Failed, PhaseStatus.Started, PhaseStatus.Failed, PhaseStatus.Started, PhaseStatus.Done], t1.Take(6).Select(p => p.Status));
        Assert.StartsWith("soru → manager", t1[1].Detail, StringComparison.Ordinal);
        Assert.Equal(2, t1[3].Round);
    }

    [Fact]
    public async Task Limit_dolunca_calisma_bekler_ve_yeniden_dene_surdurur()
    {
        var agents = new MarkdownAgentStore(_fx.Paths);
        var workflows = new JsonWorkflowStore(_fx.Paths);
        var settings = new JsonSettingsStore(_fx.Paths);
        var caller = new AgentCaller(agents, _runtime, _store, _scene, RetryPolicy.None, new LimitGuard(_runtime, settings));
        var svc = new RunService(_store, workflows, agents, _projects, _reader, caller, _scene, new WorkspaceLocator(_fx.Paths));

        _runtime.LimitPercent = 99.5; // esik varsayilan %99
        var run = await svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        run = await svc.AnalyzeAsync(run.Id, Ct);
        Assert.Equal(RunStatus.Paused, run.Status);
        Assert.NotNull(run.ResumeAt);
        Assert.StartsWith("analiz", run.Detail, StringComparison.Ordinal);
        Assert.Empty(_runtime.Calls); // cagri hic yapilmadi
        Assert.True(run.IsRetryable);
        var inbox = (await _reader.GetOverviewAsync(Ct)).Inbox;
        Assert.Contains(inbox, i => i.RunId == run.Id && i.Kind == InboxKind.Decision && i.Title.StartsWith("Limit", StringComparison.Ordinal));

        // Esik yukseltilirse (ayar) cagri gecer; otomatik surdurme (LimitResumer yolu) sayaci artirmaz, kullanici tekrari sayilmaz.
        await settings.SaveAsync(new Domain.Settings.AppSettings(new Dictionary<Provider, int> { [Provider.Anthropic] = 100 }), Ct);
        var retry = await svc.ResumeAsync(run.Id, Ct);
        Assert.Equal((RetryStep.Analyze, RunStatus.Running, 0), (retry.Step, retry.Run.Status, retry.Run.Retries));
        Assert.Null(retry.Run.ResumeAt);
        Assert.Contains(await _store.ReadMessagesAsync(run.Id, Ct), m => m.Subject == "limit-resume" && m.From == "organizer");
        Assert.DoesNotContain(await _store.ReadMessagesAsync(run.Id, Ct), m => m.Subject == "retry");
        run = await svc.AnalyzeAsync(run.Id, Ct);
        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
    }

    [Fact]
    public async Task Onay_ve_revize_yalniz_bekleyen_calismada()
    {
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        var ex = await Assert.ThrowsAsync<DomainException>(() => _svc.BeginApproveAsync(run.Id, Ct));
        Assert.Equal(ErrorCodes.RunNotAwaitingApproval, ex.ErrorCode);
        ex = await Assert.ThrowsAsync<DomainException>(() => _svc.BeginReviseAsync(run.Id, "not", Ct));
        Assert.Equal(ErrorCodes.RunNotAwaitingApproval, ex.ErrorCode);

        await _svc.AnalyzeAsync(run.Id, Ct);
        ex = await Assert.ThrowsAsync<DomainException>(() => _svc.BeginReviseAsync(run.Id, "  ", Ct));
        Assert.Equal(ErrorCodes.RunNoteEmpty, ex.ErrorCode);
    }

    [Fact]
    public async Task Is_projesiz_baslamaz_ve_projenin_akisini_devralir()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => _svc.CreateAsync(new RunRequest("brief"), Ct));
        Assert.Equal(ErrorCodes.RunProjectRequired, ex.ErrorCode);
        var nf = await Assert.ThrowsAsync<NotFoundException>(() => _svc.CreateAsync(new RunRequest(Project: "yok", Brief: "brief"), Ct));
        Assert.Equal(ErrorCodes.ProjectNotFound, nf.ErrorCode);

        await _projects.SaveAsync(new Project("tasarim-projesi", "T", "", "tasarimli", "projects/t", Project.LocalOwner, DateTimeOffset.UtcNow), Ct);
        var run = await _svc.CreateAsync(new RunRequest(Project: "tasarim-projesi", Brief: "brief"), Ct);
        Assert.Equal(("tasarimli", "tasarim-projesi", Project.LocalOwner), (run.Workflow, run.Project, run.OwnerId));
        var explicitWf = await _svc.CreateAsync(new RunRequest(Project: "tasarim-projesi", Brief: "brief", Workflow: "default"), Ct);
        Assert.Equal("default", explicitWf.Workflow);
    }

    [Fact]
    public async Task Bos_brief_ve_bilinmeyen_akis_reddedilir()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => _svc.CreateAsync(new RunRequest(Project: "test", Brief: "  "), Ct));
        Assert.Equal(ErrorCodes.RunBriefEmpty, ex.ErrorCode);
        ex = await Assert.ThrowsAsync<DomainException>(() => _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief", Workflow: "yok"), Ct));
        Assert.Equal(ErrorCodes.WorkflowNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task Hassasiyet_local_iken_anthropic_ajanlar_calismayi_baslatmaz()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief", Sensitivity: Sensitivity.Local), Ct));
        Assert.Equal(ErrorCodes.RunPolicyViolation, ex.ErrorCode);
        var run = Assert.Single(await _store.ListAsync(10, Ct));
        Assert.Equal(RunStatus.PolicyRejected, run.Status);
        Assert.Empty(_runtime.Calls);
    }

    [Fact]
    public async Task Analist_gecersiz_plan_donerse_calisma_failed()
    {
        _runtime.SpecJson = "{\"summary\":\"x\",\"architecture\":\"y\",\"rules\":[],\"tasks\":[]}";
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        run = await _svc.AnalyzeAsync(run.Id, Ct);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("gorev yok", run.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Butce_asilinca_calisma_durur_ve_tekrar_ile_surer()
    {
        // Sahte analiz turu $0.02: tavan $0.01 → analiz biter ama BudgetExceeded; plan yine de yazilmistir.
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief", MaxCostUsd: 0.01m), Ct);
        run = await _svc.AnalyzeAsync(run.Id, Ct);
        Assert.Equal(RunStatus.BudgetExceeded, run.Status);
        Assert.Contains("bütçe", run.Detail, StringComparison.Ordinal);
        Assert.NotNull(await _store.ReadSpecAsync(run.Id, Ct));
        Assert.Contains(await _store.ReadMessagesAsync(run.Id, Ct), m => m.Subject == "error");

        var ex = await Assert.ThrowsAsync<DomainException>(() => _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief", MaxCostUsd: 0m), Ct));
        Assert.Equal(ErrorCodes.RunBudgetInvalid, ex.ErrorCode);
    }

    [Fact]
    public async Task Gecici_hatada_otomatik_tekrar_sonra_basarir()
    {
        var agents = new MarkdownAgentStore(_fx.Paths);
        var workflows = new JsonWorkflowStore(_fx.Paths);
        var caller = new AgentCaller(agents, _runtime, _store, _scene, new RetryPolicy(3, TimeSpan.Zero));
        var svc = new RunService(_store, workflows, agents, _projects, _reader, caller, _scene, new WorkspaceLocator(_fx.Paths));

        _runtime.FailTransientTimes = 2; // ilk iki deneme 503, ucuncu gecer
        var run = await svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        run = await svc.AnalyzeAsync(run.Id, Ct);

        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
        Assert.Equal(3, _runtime.Calls.Count);
        Assert.Equal(2, (await _store.ReadMessagesAsync(run.Id, Ct)).Count(m => m.Subject == "retry"));

        // Denemeler bitince kalici hata: Failed + error notu.
        _runtime.FailTransientTimes = 5;
        var run2 = await svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief 2"), Ct);
        run2 = await svc.AnalyzeAsync(run2.Id, Ct);
        Assert.Equal(RunStatus.Failed, run2.Status);
        Assert.Contains(await _store.ReadMessagesAsync(run2.Id, Ct), m => m.Subject == "error");
        _runtime.FailTransientTimes = 0;
    }

    [Fact]
    public async Task Yeniden_baslatmada_running_calismalar_interrupted()
    {
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        Assert.Equal(1, await _svc.MarkInterruptedAsync(Ct));
        Assert.Equal(RunStatus.Interrupted, (await _reader.GetAsync(run.Id, Ct)).Status);
        Assert.Equal(0, await _svc.MarkInterruptedAsync(Ct));
    }

    [Fact]
    public async Task Yarim_kalan_adim_kesinti_ve_iptalde_kapanir_yeniden_dene_ayni_adimi_kosar()
    {
        // Onayli plan + elle Started faz: LLM cagrisi ortasinda surec olmus gibi.
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        await _svc.AnalyzeAsync(run.Id, Ct);
        await _svc.BeginApproveAsync(run.Id, Ct);
        await _store.AppendPhaseAsync(run.Id, new Phase(DateTimeOffset.UtcNow, "t1", "gelistirme", "Geliştirme", "implement", "developer", 1, PhaseStatus.Started), Ct);

        Assert.Equal(1, await _svc.MarkInterruptedAsync(Ct));
        var phases = await _store.ReadPhasesAsync(run.Id, "t1", Ct);
        Assert.Equal((PhaseStatus.Failed, "süreç yeniden başladı"), (phases[^1].Status, phases[^1].Detail));

        // Yeniden dene → dagitim ayni adimi (gelistirme) 1. tur olarak yeniden kosar; sistem fazi tur sayilmaz.
        var retry = await _svc.RetryAsync(run.Id, Ct);
        Assert.Equal(RetryStep.Dispatch, retry.Step);
        run = await _svc.DispatchAsync(run.Id, Ct);
        Assert.Equal(RunStatus.Completed, run.Status);
        phases = await _store.ReadPhasesAsync(run.Id, "t1", Ct);
        var rerun = phases.First(p => p.Stage == "gelistirme" && p.Status == PhaseStatus.Started && p.Ts > phases[1].Ts);
        Assert.Equal(1, rerun.Round);

        // Iptal de Started fazi Failed yapar (Skipped degil): kod yazilmadan test adimina gecilmez.
        var other = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief 2"), Ct);
        await _svc.AnalyzeAsync(other.Id, Ct);
        await _svc.BeginApproveAsync(other.Id, Ct);
        await _store.AppendPhaseAsync(other.Id, new Phase(DateTimeOffset.UtcNow, "t1", "gelistirme", "Geliştirme", "implement", "developer", 1, PhaseStatus.Started), Ct);
        await _svc.CancelAsync(other.Id, Ct);
        var cancelled = await _store.ReadPhasesAsync(other.Id, "t1", Ct);
        Assert.Equal(PhaseStatus.Failed, cancelled[^1].Status);
        Assert.StartsWith("iptal", cancelled[^1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detay_akis_kopyasini_plani_ve_sirayi_verir()
    {
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief", Label: "etiket"), Ct);
        await _svc.AnalyzeAsync(run.Id, Ct);
        var detail = await _reader.GetDetailAsync(run.Id, Ct);
        Assert.Equal("etiket", detail.Label);
        Assert.Equal("default", detail.WorkflowDef!.Key);
        Assert.Equal(4, detail.WorkflowDef.Stages.Count);
        Assert.Equal(["t1", "t2"], detail.Order);
        Assert.Empty(detail.Tasks);
    }

    [Fact]
    public async Task Gelen_kutusu_onay_bekleyeni_ve_duseni_sayar_iptal_ve_yeniden_dene_calisir()
    {
        var a = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief a", Label: "a"), Ct);
        await _svc.AnalyzeAsync(a.Id, Ct); // AwaitingApproval → soru

        _runtime.SpecJson = "{\"summary\":\"x\",\"architecture\":\"y\",\"rules\":[],\"tasks\":[]}";
        var b = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief b", Label: "b"), Ct);
        await _svc.AnalyzeAsync(b.Id, Ct); // Failed → karar
        _runtime.SpecJson = null;

        var o = await _reader.GetOverviewAsync(Ct);
        Assert.Equal((2, 1, 1, 0), (o.Total, o.AwaitingApproval, o.Failed, o.Running));
        Assert.Equal(2, o.Inbox.Count);
        var approval = Assert.Single(o.Inbox, i => i.RunId == a.Id);
        Assert.Equal((InboxKind.Approval, RunStatus.AwaitingApproval, "slugify"), (approval.Kind, approval.Status, approval.Detail));
        var decision = Assert.Single(o.Inbox, i => i.RunId == b.Id);
        Assert.Equal(InboxKind.Decision, decision.Kind);
        Assert.Contains("gorev yok", decision.Detail, StringComparison.Ordinal);

        // Iptal: hic baslamamis (PolicyRejected) ya da zaten iptal edilmis calisma iptal edilemez; onay bekleyen edilir.
        await Assert.ThrowsAsync<DomainException>(() => _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief c", Sensitivity: Sensitivity.Local), Ct));
        var c = (await _store.ListAsync(10, Ct)).Single(r => r.Status == RunStatus.PolicyRejected);
        var ex = await Assert.ThrowsAsync<DomainException>(() => _svc.CancelAsync(c.Id, Ct));
        Assert.Equal(ErrorCodes.RunNotCancellable, ex.ErrorCode);
        a = await _svc.CancelAsync(a.Id, Ct);
        Assert.Equal(RunStatus.Cancelled, a.Status);
        ex = await Assert.ThrowsAsync<DomainException>(() => _svc.CancelAsync(a.Id, Ct)); // ikinci kez iptal edilemez
        Assert.Equal(ErrorCodes.RunNotCancellable, ex.ErrorCode);

        // Dusen calisma "kapat" anlaminda iptal edilir ve gelen kutusundan duser; PolicyRejected zaten kutuda degildir.
        var d = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief d", Label: "d"), Ct);
        _runtime.SpecJson = "{\"summary\":\"x\",\"architecture\":\"y\",\"rules\":[],\"tasks\":[]}";
        await _svc.AnalyzeAsync(d.Id, Ct);
        _runtime.SpecJson = null;
        Assert.Equal(RunStatus.Cancelled, (await _svc.CancelAsync(d.Id, Ct)).Status);
        o = await _reader.GetOverviewAsync(Ct);
        Assert.DoesNotContain(o.Inbox, i => i.RunId == d.Id || i.RunId == c.Id);

        // Yeniden dene: dusen calisma analizden surer (Running); onay beklerken iptal edilen onaya doner, kuyruga is girmez.
        var retryB = await _svc.RetryAsync(b.Id, Ct);
        Assert.Equal((RetryStep.Analyze, RunStatus.Running), (retryB.Step, retryB.Run.Status));
        var retryA = await _svc.RetryAsync(a.Id, Ct);
        Assert.Equal(RunStatus.AwaitingApproval, retryA.Run.Status);

        o = await _reader.GetOverviewAsync(Ct);
        Assert.Equal((4, 1, 1, 1, 1), (o.Total, o.Running, o.AwaitingApproval, o.Failed, o.Cancelled)); // failed = c (PolicyRejected), cancelled = d
        var only = Assert.Single(o.Inbox); // b calisiyor, bir sey beklemiyor; a yine onay soruyor
        Assert.Equal((a.Id, InboxKind.Approval), (only.RunId, only.Kind));
    }

    [Fact]
    public async Task Developer_engellenince_manager_cevaplar_kullanici_gormez()
    {
        _runtime.BlockImplementTimes = 1;
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        await _svc.AnalyzeAsync(run.Id, Ct);
        await _svc.BeginApproveAsync(run.Id, Ct);
        run = await _svc.DispatchAsync(run.Id, Ct);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Null(run.Question);
        var messages = await _store.ReadMessagesAsync(run.Id, Ct);
        Assert.DoesNotContain(messages, m => m.Kind == MessageKind.Ask && m.To == "user");
        Assert.Single(messages, m => m.Kind == MessageKind.Ask && m.From == "developer" && m.To == "manager" && m.Subject == "ask");
        Assert.Single(messages, m => m.Kind == MessageKind.Answer && m.From == "manager" && m.To == "developer" && m.Subject == "answer");
        Assert.Empty((await _reader.GetOverviewAsync(Ct)).Inbox);

        // Manager yalniz okuma araciyla cagrildi; cevap developer'in 2. turunda; manager turu conversations'ta.
        var managerCall = Assert.Single(_runtime.Calls, c => c.SchemaJson?.Contains("escalate", StringComparison.Ordinal) == true);
        Assert.Equal(["Read", "Glob", "Grep"], managerCall.Tools!);
        Assert.Contains("i mi I mı", managerCall.Messages[0].Content, StringComparison.Ordinal);
        var devCalls = _runtime.Calls.Where(c => c.SchemaJson?.Contains("filesChanged", StringComparison.Ordinal) == true).ToList();
        Assert.Contains("manager → developer · answer", devCalls[1].Messages[0].Content, StringComparison.Ordinal);
        Assert.NotEmpty(await _store.ReadTurnsAsync(run.Id, "manager", Ct));
    }

    [Fact]
    public async Task Manager_yukseltince_soru_kullaniciya_sebebiyle_duser()
    {
        _runtime.BlockImplementTimes = 1;
        _runtime.ManagerEscalates = true;
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        await _svc.AnalyzeAsync(run.Id, Ct);
        await _svc.BeginApproveAsync(run.Id, Ct);
        run = await _svc.DispatchAsync(run.Id, Ct);

        Assert.Equal(RunStatus.AwaitingInput, run.Status);
        Assert.Equal("developer", run.Question!.Agent);
        Assert.Contains("müşteri tercihi", run.Question.Context, StringComparison.Ordinal);
        var messages = await _store.ReadMessagesAsync(run.Id, Ct);
        Assert.Single(messages, m => m.Subject == "escalate" && m.From == "manager");
        Assert.DoesNotContain(messages, m => m.Kind == MessageKind.Answer);

        run = await _svc.BeginAnswerAsync(run.Id, new AnswerRequest("retry", "büyük I olsun"), Ct);
        run = await _svc.DispatchAsync(run.Id, Ct);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Single(_runtime.Calls, c => c.SchemaJson?.Contains("escalate", StringComparison.Ordinal) == true); // manager'a bir daha sorulmadi
    }

    // ------------------------------------------------------------------ sahteler

    /// <summary>Python yerine: sema istenirse plan JSON'u, yoksa devir notu metni. Cagrilari kaydeder.</summary>
    private sealed class FakeRuntime : IAgentRuntimeService
    {
        public List<RuntimeTurnRequest> Calls { get; } = [];

        public string? SpecJson { get; set; }

        private const string Plan2 = """
            {"summary":"slugify","architecture":"tek modül","rules":["stdlib dışı bağımlılık yok"],
             "tasks":[
               {"id":"t1","title":"slugify fonksiyonu","description":"...","files":["slug.py"],"acceptance":["çalışır"],"dependsOn":[]},
               {"id":"t2","title":"testler","description":"...","files":["test_slug.py"],"acceptance":["geçer"],"dependsOn":["t1"]}]}
            """;

        private const string Plan3 = """
            {"summary":"slugify","architecture":"tek modül","rules":[],
             "tasks":[
               {"id":"t1","title":"slugify fonksiyonu","description":"...","files":["slug.py"],"acceptance":["çalışır"],"dependsOn":[]},
               {"id":"t2","title":"testler","description":"...","files":["test_slug.py"],"acceptance":["geçer"],"dependsOn":["t1"]},
               {"id":"t3","title":"dokümantasyon","description":"...","files":["README.md"],"acceptance":["var"],"dependsOn":["t1"]}]}
            """;

        /// <summary>Kac cagri "gecici hata" (runtime kapali) ile dussun; AgentCaller'in otomatik tekrari icin.</summary>
        public int FailTransientTimes { get; set; }

        /// <summary>Testci (review) hep reddetsin.</summary>
        public bool RejectTests { get; set; }

        /// <summary>Developer engellensin (blocked + soru).</summary>
        public bool BlockImplement { get; set; }

        /// <summary>Developer yalniz ilk N implement cagrisinda engellensin (manager cevabi sonrasi devam etsin).</summary>
        public int BlockImplementTimes { get; set; }

        /// <summary>Manager (can_ask hedefi) cevap vermesin, kullaniciya yukseltsin.</summary>
        public bool ManagerEscalates { get; set; }

        /// <summary>Kota penceresi yuzdesi (limit korumasi testi); null = kota bilgisi yok.</summary>
        public double? LimitPercent { get; set; }

        public Task<RuntimeTurnResponse> TurnAsync(RuntimeTurnRequest request, CancellationToken ct)
        {
            Calls.Add(request);
            if (FailTransientTimes > 0)
            {
                FailTransientTimes--;
                throw new RuntimeUnavailableException("runtime'a ulasilamadi: sahte 503");
            }

            var destination = RunDefaults.DestinationOf(request.Provider);
            if (request.SchemaJson is null)
            {
                return Task.FromResult(new RuntimeTurnResponse("Devir notu: t1 developer'a.", null, request.Provider, request.Model, destination, new RuntimeUsage(10, 5, 0), 0.001m, 0.2, 1));
            }

            if (request.SchemaJson.Contains("escalate", StringComparison.Ordinal))
            {
                var ask = ManagerEscalates
                    ? """{"answer":null,"escalate":true,"reason":"müşteri tercihi, ben karar veremem"}"""
                    : """{"answer":"Küçük i kullan; Türkçe kurala göre I→ı, İ→i.","escalate":false,"reason":null}""";
                return Task.FromResult(new RuntimeTurnResponse("", ask, request.Provider, request.Model, destination, new RuntimeUsage(30, 10, 0), 0.004m, 1.0, 1));
            }

            if (request.SchemaJson.Contains("filesChanged", StringComparison.Ordinal))
            {
                var blockNow = BlockImplement || BlockImplementTimes > 0;
                if (BlockImplementTimes > 0)
                {
                    BlockImplementTimes--;
                }

                var report = blockNow
                    ? """{"summary":"takildim","filesChanged":[],"commandsRun":[],"blocked":true,"question":"Türkçe karakter: i mi I mı?"}"""
                    : """{"summary":"slug.py yazildi","filesChanged":["slug.py"],"commandsRun":["python -m pytest: 3 passed"],"blocked":false,"question":null}""";
                return Task.FromResult(new RuntimeTurnResponse("", report, request.Provider, request.Model, destination, new RuntimeUsage(50, 20, 0), 0.01m, 2.0, 1, [new RuntimeToolUse("Write", "slug.py")], 5));
            }

            if (request.SchemaJson.Contains("verdict", StringComparison.Ordinal))
            {
                var isTester = request.SystemPrompt.Contains("TESTÇİ", StringComparison.Ordinal);
                var review = RejectTests && isTester
                    ? """{"verdict":"reject","testsRun":true,"findings":["kural 1 ihlal"],"feedback":"kural 1'i düzelt","commandsRun":["pytest: 1 failed"]}"""
                    : """{"verdict":"accept","testsRun":true,"findings":[],"feedback":"temiz","commandsRun":["pytest: 3 passed"]}""";
                return Task.FromResult(new RuntimeTurnResponse("", review, request.Provider, request.Model, destination, new RuntimeUsage(50, 20, 0), 0.01m, 2.0, 1, [new RuntimeToolUse("Bash", "pytest")], 4));
            }

            if (request.SchemaJson.Contains("guidance", StringComparison.Ordinal))
            {
                return Task.FromResult(new RuntimeTurnResponse("", """{"guidance":"basit tut","decisions":["tek modül"]}""", request.Provider, request.Model, destination, new RuntimeUsage(20, 10, 0), 0.005m, 1.0, 1));
            }

            var json = SpecJson ?? (request.Messages.Count > 1 ? Plan3 : Plan2);
            // Structured alan JSON olarak gider; runtime gibi bir kez dogrulanir.
            using var _ = JsonDocument.Parse(json);
            return Task.FromResult(new RuntimeTurnResponse("", json, request.Provider, request.Model, destination, new RuntimeUsage(100, 50, 0), 0.02m, 1.5, 1));
        }

        public Task<IReadOnlyList<RuntimeModelInfo>> ListModelsAsync(Provider? provider, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RuntimeModelInfo>>([]);

        public Task<IReadOnlyList<RuntimeAuthStatus>> ListAuthAsync(Provider? provider, bool refresh, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RuntimeAuthStatus>>([]);

        public Task<RuntimeLoginStarted> LoginAsync(Provider provider, string mode, string? email, string? apiKey, CancellationToken ct)
            => Task.FromResult(new RuntimeLoginStarted(provider, false, "sahte"));

        public Task<RuntimeAuthStatus> LogoutAsync(Provider provider, CancellationToken ct)
            => Task.FromResult(new RuntimeAuthStatus(provider, false, null, "sahte"));

        public Task<IReadOnlyList<RuntimeProviderLimits>> ListLimitsAsync(Provider? provider, bool refresh, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RuntimeProviderLimits>>(LimitPercent is { } p
                ? [new RuntimeProviderLimits(Provider.Anthropic, true, "sahte", "max", DateTimeOffset.UtcNow, [new RuntimeUsageLimit("five_hour", null, p, "warning", DateTimeOffset.UtcNow.AddHours(1), null, true)])]
                : []);
    }

    private sealed class FakeScene : ISceneEventPublisher
    {
        public List<(string Type, string Json)> Events { get; } = [];

        public void Publish(string type, string json) => Events.Add((type, json));
    }
}
