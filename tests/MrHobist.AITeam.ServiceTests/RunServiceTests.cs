using System.Text.Json;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Common;
using MrHobist.AITeam.Application.Runs;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Projects;
using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Infrastructure.Compaction;
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
    private readonly FakeScheduler _scheduler = new();
    private readonly IRunStore _store;
    private readonly RunReader _reader;
    private readonly IProjectStore _projects;
    private readonly RunService _svc;

    public RunServiceTests()
    {
        _store = _fx.Runs;
        var agents = new MarkdownAgentStore(_fx.Paths);
        var workflows = new JsonWorkflowStore(_fx.Paths);
        _reader = new RunReader(_store);
        _projects = _fx.Projects;
        // Testlerin akisi URUNUN varsayilanina bagli DEGILDIR: burada kurulur. Varsayilan akis
        // (2026-09-21'de tek kisilik oldu) degistiginde bu testlerin kirilmamasi icin.
        workflows.SaveAsync(KlasikAkis, Ct).GetAwaiter().GetResult();
        _projects.SaveAsync(new Project("test", "Test", "", KlasikKey, "projects/test", Project.LocalOwner, DateTimeOffset.UtcNow), Ct).GetAwaiter().GetResult();
        // Gercek sikistirici: tasinan gecmis butceyi asarsa kirpilir; testler bu yolu da kossun.
        _svc = new RunService(_store, workflows, agents, _projects, _reader, new AgentCaller(agents, _runtime, _store, _scene), _scene, new WorkspaceLocator(_fx.Paths), _scheduler, new MafHistoryCompactor());
    }

    public void Dispose() => _fx.Dispose();

    /// <summary>Testlerin dayandigi tam kadro akis: analiz → gelistirme → test → karar, devir organizatorde, plani kullanici onaylar.</summary>
    private const string KlasikKey = "klasik";

    private static Domain.Workflows.Workflow KlasikAkis => new(
        KlasikKey, "Klasik", 3, "organizer",
        [
            new("analiz", "Analiz", Domain.Workflows.StageKind.Analyze, "analyst", "pm", "Brief çözümlenir."),
            new("gelistirme", "Geliştirme", Domain.Workflows.StageKind.Implement, "developer", "dev", "Görev kodlanır."),
            new("test", "Test", Domain.Workflows.StageKind.Review, "tester", "qa", "Testler koşulur."),
            new("karar", "Karar", Domain.Workflows.StageKind.Review, "manager", "gate", "Son onay."),
        ]);

    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>
    /// Onay kapisi olmayan akis (planApprover: auto): plan uretilir uretilmez DAGITIM KUYRUGA GIRER.
    /// 2026-09-21'de bu kacirildi ve uc calisma birden "Running" gorunup sonsuza kadar bekledi:
    /// durum yazilmisti ama is kanali bostu. Durum gecisi TEK BASINA yetmez, isi kuyruga koyan da olmali.
    /// </summary>
    /// <summary>
    /// Ayni ajan ardisik adimlarda kosunca kendi ciktisini GORUR (baglam tasima, 2026-09-21).
    /// Olculdu: tasima yokken test adimi, gelistirme adiminin az once yazdigi dosyalari sifirdan
    /// okuyup dogruluyordu -- tek kisilik akista 19 ic tur. Tasinan sey ajanin CIKTISIDIR, onceki
    /// istemi degil: istem zaten gorev baglamini tasiyor, tekrari her turda bedel oduretirdi.
    /// </summary>
    [Fact]
    public async Task Ayni_ajan_kendi_onceki_adiminin_ciktisini_gorur()
    {
        var workflows = new JsonWorkflowStore(_fx.Paths);
        await workflows.SaveAsync(new Domain.Workflows.Workflow(
            "tek", "Tek kişi", 3, null,
            [
                new("analiz", "Analiz", Domain.Workflows.StageKind.Analyze, "developer", "dev", ""),
                new("gelistirme", "Geliştirme", Domain.Workflows.StageKind.Implement, "developer", "dev", ""),
                new("test", "Test", Domain.Workflows.StageKind.Review, "developer", "qa", ""),
            ],
            AskRole: Domain.Workflows.Workflow.UserRole,
            PlanApprover: Domain.Workflows.Workflow.AutoApprove), Ct);

        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Workflow: "tek", Brief: "brief"), Ct);
        await _svc.AnalyzeAsync(run.Id, Ct);
        await _svc.DispatchAsync(run.Id, Ct);

        // Test adimi cagrisi: istem SON mesaj, onunde gelistirme adiminin ciktisi asistan rolunde durur.
        var review = _runtime.Calls.Last(c => c.SchemaJson?.Contains("verdict", StringComparison.Ordinal) == true);
        Assert.True(review.Messages.Count > 1, "test adimina gecmis tasinmadi");
        Assert.Equal("user", review.Messages[^1].Role);
        Assert.Contains(review.Messages, m => m.Role == "assistant" && m.Content.Contains("filesChanged", StringComparison.Ordinal));
        Assert.Contains(review.Messages, m => m.Role == "user" && m.Content.Contains("Bu adımda verdiğin çıktı", StringComparison.Ordinal));

        // Ilk adimda tasinacak gecmis YOK: tek mesaj.
        var implement = _runtime.Calls.First(c => c.SchemaJson?.Contains("filesChanged", StringComparison.Ordinal) == true);
        Assert.Single(implement.Messages);
    }

    /// <summary>
    /// Sistem promptu modu ADIM TURUNE gore secilir ve karar .NET'tedir (CLAUDE.md §1). Olculdu 2026-09-21:
    /// Claude Code'un kendi kilavuzu yurutme adiminda 55 → ~16 ic tur kazandiriyor ama plan ureten adimda
    /// buyuk Spec semasini dolduramiyor (5 denemede 'rules'/'tasks' eksik). Ikisi birden gerekli.
    /// </summary>
    [Fact]
    public async Task Sistem_promptu_modu_yurutme_adiminda_claude_code_planlamada_replace()
    {
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        await _svc.AnalyzeAsync(run.Id, Ct);
        await _svc.BeginApproveAsync(run.Id, Ct);
        await _svc.DispatchAsync(run.Id, Ct);

        var analyze = _runtime.Calls.First(c => c.SchemaJson?.Contains("\"tasks\"", StringComparison.Ordinal) == true);
        var implement = _runtime.Calls.First(c => c.SchemaJson?.Contains("filesChanged", StringComparison.Ordinal) == true);
        var review = _runtime.Calls.First(c => c.SchemaJson?.Contains("verdict", StringComparison.Ordinal) == true);

        Assert.Equal(SystemPromptModes.Replace, analyze.SystemPromptMode);      // plan uretir: kilavuz semayi bozuyor
        Assert.Equal(SystemPromptModes.ClaudeCode, implement.SystemPromptMode); // dosya yazar: kilavuz korunur
        Assert.Equal(SystemPromptModes.ClaudeCode, review.SystemPromptMode);    // komut kosar: kilavuz korunur
    }

    /// <summary>
    /// Red turlari tasinan gecmisi buyutur (her implement turu oncekilerin ciktisini tasir). Butce
    /// (<see cref="CompactionBudget.TaskHistory"/>: 4 mesaj) asilinca en eski turlar duser; yeni istem daima
    /// son mesaj ve tam. 6 red turu → developer 6. turda 5 onceki cikti (10 mesaj) tasiyacakti; 4'e iner.
    /// </summary>
    [Fact]
    public async Task Red_turlari_buyuyen_gecmisi_butce_sinirlar()
    {
        var workflows = new JsonWorkflowStore(_fx.Paths);
        await workflows.SaveAsync(KlasikAkis with { Key = "uzun-red", MaxReviewRounds = 6 }, Ct);
        _runtime.RejectTests = true;

        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Workflow: "uzun-red", Brief: "brief"), Ct);
        await _svc.AnalyzeAsync(run.Id, Ct);
        await _svc.BeginApproveAsync(run.Id, Ct);
        run = await _svc.DispatchAsync(run.Id, Ct);
        Assert.Equal(RunStatus.AwaitingInput, run.Status); // 6 red → kullaniciya

        var implementCalls = _runtime.Calls.Where(c => c.SchemaJson?.Contains("filesChanged", StringComparison.Ordinal) == true && c.Messages[^1].Content.Contains("# Görev t1", StringComparison.Ordinal)).ToList();
        Assert.Equal(6, implementCalls.Count);

        Assert.Single(implementCalls[0].Messages);                       // ilk tur: tasinan gecmis yok
        Assert.Equal(5, implementCalls[2].Messages.Count);               // 3. tur: 2 cikti (4 mesaj) + istem, butce icinde
        Assert.True(implementCalls[5].Messages.Count <= 5, $"6. tur {implementCalls[5].Messages.Count} mesaj; butce 4 + istem"); // sikistirildi
        Assert.Equal("user", implementCalls[5].Messages[^1].Role);      // istem daima sonda ve tam
        Assert.Contains("# Görev t1", implementCalls[5].Messages[^1].Content, StringComparison.Ordinal);
        for (var i = 1; i < implementCalls[5].Messages.Count; i++)
        {
            Assert.NotEqual(implementCalls[5].Messages[i - 1].Role, implementCalls[5].Messages[i].Role); // almasik
        }

        // Tur kaydi olcuyu tasir: ilk turda gecmis yok, 6. turda 5 cikti (10 mesaj) tasinacakti, 4'e indi.
        var devTurns = (await _store.ReadTurnsAsync(run.Id, "developer", Ct)).Where(t => t.Task == "t1" && t.Stage == "gelistirme").ToList();
        Assert.Equal(6, devTurns.Count);
        Assert.All(devTurns, t => Assert.True(t.ToolsOffered)); // yurutme adimi: arac tanimli, kalibrasyona girmez
        Assert.Null(devTurns[0].Context);
        var last = Assert.IsType<ContextStats>(devTurns[5].Context);
        Assert.Equal(10, last.CarriedMessages);
        Assert.True(last.KeptMessages <= 4 && last.KeptChars < last.CarriedChars, $"{last}");
        Assert.Equal((TokenCalibration.Fallback, 0), (last.CharsPerToken, last.CalibrationSamples)); // olcum yok: varsayilan
    }

    [Fact]
    public async Task Otomatik_onayda_dagitim_kuyruga_girer()
    {
        var workflows = new JsonWorkflowStore(_fx.Paths);
        var wf = await workflows.LoadAsync(KlasikKey, Ct);
        await workflows.SaveAsync(wf with { Key = "otomatik", PlanApprover = Domain.Workflows.Workflow.AutoApprove, AskRole = Domain.Workflows.Workflow.UserRole }, Ct);

        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Workflow: "otomatik", Brief: "brief"), Ct);
        _scheduler.Scheduled.Clear();

        run = await _svc.AnalyzeAsync(run.Id, Ct);

        Assert.Equal(RunStatus.Running, run.Status);          // kullaniciya SORULMAZ
        Assert.Equal(RunStep.Dispatch, run.Step);
        Assert.Contains((run.Id, RunStep.Dispatch), _scheduler.Scheduled); // ve is kuyruga KONDU
    }

    /// <summary>Plani ajan onaylar: kabul → dagitim kuyrukta, red → analiz kuyrukta. Ikisi de kullaniciya sormaz.</summary>
    [Theory]
    [InlineData(false, RunStep.Dispatch)]
    [InlineData(true, RunStep.Analyze)]
    public async Task Ajan_plan_onayinda_sonraki_adim_kuyruga_girer(bool reddet, RunStep beklenen)
    {
        var workflows = new JsonWorkflowStore(_fx.Paths);
        var wf = await workflows.LoadAsync(KlasikKey, Ct);
        await workflows.SaveAsync(wf with { Key = "ajan-onay", PlanApprover = "manager" }, Ct);

        _runtime.RejectPlan = reddet;
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Workflow: "ajan-onay", Brief: "brief"), Ct);
        _scheduler.Scheduled.Clear();

        run = await _svc.AnalyzeAsync(run.Id, Ct);

        Assert.Equal(RunStatus.Running, run.Status);
        Assert.Equal(beklenen, run.Step);
        Assert.Contains((run.Id, beklenen), _scheduler.Scheduled);
    }

    [Fact]
    public async Task Analiz_plani_uretir_ve_onay_bekler()
    {
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "Türkçe slugify fonksiyonu yaz"), Ct);
        Assert.Equal(RunStatus.Running, run.Status);
        Assert.Equal(KlasikKey, run.Workflow);
        Assert.NotNull(await _store.ReadWorkflowAsync(run.Id, Ct)); // akis kopyasi calismayla birlikte donduruldu

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

        Assert.Contains(_scene.Events, e => e.Type == SceneEventTypes.WorkflowSet && e.Json.Contains(KlasikKey, StringComparison.Ordinal));
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
        // Organizator MODEL CAGIRMAZ (2026-09-21): devir notu kodda uretilir. Ucuz model onun icindi;
        // baska hicbir adim haiku kullanmadigi icin tek bir haiku cagrisi bile olmamali.
        Assert.DoesNotContain(_runtime.Calls, c => c.Model == "claude-haiku-4-5-20251001");

        // Rapor ve kabul notlari kayitta; devir notu YALNIZ implement atamasinda (2 gorev → 2 not); arac kullanimi turda.
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
        Assert.Contains("review-feedback", devCalls[1].Messages[^1].Content, StringComparison.Ordinal);

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
        var devCalls = _runtime.Calls.Where(c => c.SchemaJson?.Contains("filesChanged", StringComparison.Ordinal) == true && c.Messages[^1].Content.Contains("# Görev t1", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, devCalls.Count); // t1: engel, manager cevabiyla engel, kullanici cevabiyla bitti
        Assert.Contains("Küçük i kullan; Türkçe", devCalls[1].Messages[^1].Content, StringComparison.Ordinal);
        Assert.Contains("küçük i kullan", devCalls[2].Messages[^1].Content, StringComparison.Ordinal);
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
        var settings = _fx.Settings;
        var caller = new AgentCaller(agents, _runtime, _store, _scene, RetryPolicy.None, new LimitGuard(_runtime, settings));
        var svc = new RunService(_store, workflows, agents, _projects, _reader, caller, _scene, new WorkspaceLocator(_fx.Paths), _scheduler);

        _runtime.LimitPercent = 99.5; // esik varsayilan %99
        var run = await svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        run = await svc.AnalyzeAsync(run.Id, Ct);
        Assert.Equal(RunStatus.Paused, run.Status);
        Assert.NotNull(run.ResumeAt);
        Assert.Equal(RunStep.Analyze, run.Step); // kaldigi adim kayitta; Detail yalniz gorunum
        Assert.StartsWith("analiz", run.Detail, StringComparison.Ordinal);
        Assert.Empty(_runtime.Calls); // cagri hic yapilmadi
        Assert.True(run.IsRetryable);
        var inbox = (await _reader.GetOverviewAsync(Ct)).Inbox;
        Assert.Contains(inbox, i => i.RunId == run.Id && i.Kind == InboxKind.Decision && i.Title.StartsWith("Limit", StringComparison.Ordinal));

        // Esik yukseltilirse (ayar) cagri gecer; otomatik surdurme (LimitResumer yolu) sayaci artirmaz, kullanici tekrari sayilmaz.
        await settings.SaveAsync(new Domain.Settings.AppSettings(new Dictionary<Provider, int> { [Provider.Anthropic] = 100 }), Ct);
        var retry = await svc.ResumeAsync(run.Id, Ct);
        Assert.Equal((RunStep.Analyze, RunStatus.Running, 0), (retry.Step, retry.Status, retry.Retries));
        Assert.Null(retry.ResumeAt);
        Assert.Contains(await _store.ReadMessagesAsync(run.Id, Ct), m => m.Subject == "limit-resume" && m.From == "organizer");
        Assert.DoesNotContain(await _store.ReadMessagesAsync(run.Id, Ct), m => m.Subject == "retry");
        run = await svc.AnalyzeAsync(run.Id, Ct);
        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
    }

    /// <summary>
    /// Limit CAGRI SIRASINDA gelirse (LimitGuard yuzdeleri 90 s onbellekliyor, pencere tam o aralikta dolabilir)
    /// calisma Failed DEGIL Paused olmali: yoksa pencere sifirlandiginda kendiliginden surmez, kullanici elle
    /// "yeniden dene" demek zorunda kalir (2026-09-22). Tekrar denenmez: sifirlanma dakikalar/gunler sonradir.
    /// </summary>
    [Fact]
    public async Task Cagri_sirasinda_limit_gelirse_calisma_beklemeye_duser_ve_surdurulur()
    {
        _runtime.LimitMidCallTimes = 1;
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        run = await _svc.AnalyzeAsync(run.Id, Ct);

        Assert.Equal(RunStatus.Paused, run.Status);
        Assert.NotNull(run.ResumeAt);                       // RunResumer bunu gorup surdurur
        Assert.Equal(RunStep.Analyze, run.Step);            // kaldigi adim kayitta
        Assert.Single(_runtime.Calls);                      // tek deneme: tekrar ANLAMSIZ olurdu
        Assert.Contains("limit", run.Detail, StringComparison.OrdinalIgnoreCase);

        // Faz sistem kaynakli kapanir: tur sayilmaz, red tavanina girmez.
        var phases = await _store.ReadPhasesAsync(run.Id, "run", Ct);
        Assert.All(phases.Where(p => p.Status == PhaseStatus.Failed), p => Assert.True(p.IsSystemFailure));

        // Pencere sifirlandi: ayni adimdan surer, sayac artmaz, sifirdan baslamaz.
        var resumed = await _svc.ResumeAsync(run.Id, Ct);
        Assert.Equal((RunStep.Analyze, RunStatus.Running, 0), (resumed.Step, resumed.Status, resumed.Retries));
        run = await _svc.AnalyzeAsync(run.Id, Ct);
        Assert.Equal(RunStatus.AwaitingApproval, run.Status);
    }

    /// <summary>
    /// Proje butcesi (2026-09-22 kullanici karari): is butcesi tek isi, proje butcesi projenin TOPLAMINI sinirlar.
    /// Dolmussa yeni is HIC baslamaz -- baslayip ilk turdan sonra durmak bir tur token'i bosa harcardi.
    /// </summary>
    [Fact]
    public async Task Proje_butcesi_dolunca_calisma_durur_ve_yeni_is_baslamaz()
    {
        // Token tavani bir turun tuketiminin altinda: ilk tur kapaninca toplam tavani gecer.
        await _projects.SaveAsync(new Project("test", "Test", "", KlasikKey, "projects/test", Project.LocalOwner, DateTimeOffset.UtcNow, MaxTokens: 5), Ct);

        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        run = await _svc.AnalyzeAsync(run.Id, Ct);
        Assert.Equal(RunStatus.BudgetExceeded, run.Status);
        Assert.Contains("proje bütçesi", run.Detail, StringComparison.Ordinal);
        Assert.True(run.TotalTokens > 5);

        // Tavan dolu: ikinci is hic kurulmaz, kuyruga olu bir kayit birakilmaz.
        var ex = await Assert.ThrowsAsync<DomainException>(() => _svc.CreateAsync(new RunRequest(Project: "test", Brief: "ikinci"), Ct));
        Assert.Equal(ErrorCodes.ProjectBudgetExceeded, ex.ErrorCode);

        // Sinirsiza donulunce (butce alanlari bos) is yeniden baslar: varsayilan davranis budur.
        await _projects.SaveAsync(new Project("test", "Test", "", KlasikKey, "projects/test", Project.LocalOwner, DateTimeOffset.UtcNow), Ct);
        var sonraki = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "ucuncu"), Ct);
        Assert.Equal(RunStatus.Running, sonraki.Status);
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
        var explicitWf = await _svc.CreateAsync(new RunRequest(Project: "tasarim-projesi", Brief: "brief", Workflow: KlasikKey), Ct);
        Assert.Equal(KlasikKey, explicitWf.Workflow);
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
        var svc = new RunService(_store, workflows, agents, _projects, _reader, caller, _scene, new WorkspaceLocator(_fx.Paths), _scheduler);

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

    /// <summary>
    /// 2026-09-23: sabit 12 dk HTTP suresi calisan Opus turunu kesti, zaman asimi "kullanici iptali" sanilip yutuldu ve calisma
    /// Running'de asili kaldi. Artik: bekci hareketsiz turu keser → faz Timeout (sistem), calisma Failed; "Yeniden dene" ayni
    /// adimi DEVAM notuyla kosar (ajan diskteki yarim isi bastan yazmaz).
    /// </summary>
    [Fact]
    public async Task Hareketsiz_tur_zaman_asimina_duser_yeniden_dene_devam_notuyla_surer()
    {
        var agents = new MarkdownAgentStore(_fx.Paths);
        var workflows = new JsonWorkflowStore(_fx.Paths);
        var caller = new AgentCaller(agents, _runtime, _store, _scene, RetryPolicy.None, watch: new TurnWatch(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50)));
        var svc = new RunService(_store, workflows, agents, _projects, _reader, caller, _scene, new WorkspaceLocator(_fx.Paths), _scheduler);

        var run = await svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        await svc.AnalyzeAsync(run.Id, Ct);
        await svc.BeginApproveAsync(run.Id, Ct);
        _runtime.HangImplementTimes = 1;
        run = await svc.DispatchAsync(run.Id, Ct);

        Assert.Equal(RunStatus.Failed, run.Status); // asili Running degil
        Assert.True(run.IsRetryable);
        Assert.Contains("zaman aşımı", run.Detail, StringComparison.Ordinal);
        var t1 = await _store.ReadPhasesAsync(run.Id, "t1", Ct);
        Assert.Equal((PhaseStatus.Failed, PhaseCause.Timeout), (t1[^1].Status, t1[^1].Cause));
        Assert.True(t1[^1].IsSystemFailure && t1[^1].IsCutShort);

        var before = _runtime.Calls.Count;
        await svc.RetryAsync(run.Id, Ct);
        run = await svc.DispatchAsync(run.Id, Ct);
        Assert.Equal(RunStatus.Completed, run.Status);
        var resumed = _runtime.Calls.Skip(before).First(c => c.SchemaJson?.Contains("filesChanged", StringComparison.Ordinal) == true);
        Assert.Contains("DEVAM", resumed.Messages[^1].Content, StringComparison.Ordinal);
        // Yalniz kesilen gorev devam notu alir; t2 temiz baslar.
        var fresh = _runtime.Calls.Skip(before).Where(c => c.SchemaJson?.Contains("filesChanged", StringComparison.Ordinal) == true).Skip(1).First();
        Assert.DoesNotContain("DEVAM", fresh.Messages[^1].Content, StringComparison.Ordinal);
    }

    private sealed class FixedPrices(string model, ModelPrice price) : IModelCatalog
    {
        public Task<IReadOnlyList<CatalogModel>> LoadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CatalogModel>>([]);

        public Task<IReadOnlyDictionary<string, ModelPrice>> LoadPricesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, ModelPrice>>(new Dictionary<string, ModelPrice> { [model] = price });
    }

    /// <summary>
    /// 2026-09-23: kesilen ilk t1 denemesi ~3 $ harcadi, run_turn'e hic yazilmadi (CLAUDE.md §4 deliniyordu). Artik runtime'in
    /// mesaj basina canli kullanim bildirimi birikir; tur kesilince kullanim + fiyattan tahmin edilen maliyet kayda ve calismanin
    /// toplamina girer.
    /// </summary>
    [Fact]
    public async Task Kesilen_turun_harcamasi_kaydedilir_ve_calismanin_toplamina_girer()
    {
        var agents = new MarkdownAgentStore(_fx.Paths);
        var workflows = new JsonWorkflowStore(_fx.Paths);
        var progress = new ProgressRegistry(_scene) { BaseUrl = "http://127.0.0.1:5080/api/v1/progress" };
        var caller = new AgentCaller(agents, _runtime, _store, _scene, RetryPolicy.None, progress: progress,
            watch: new TurnWatch(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50)),
            catalog: new FixedPrices(RunDefaults.Model, new ModelPrice(4m, 20m, 0.2m, 8m)));
        var svc = new RunService(_store, workflows, agents, _projects, _reader, caller, _scene, new WorkspaceLocator(_fx.Paths), _scheduler);

        var run = await svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        await svc.AnalyzeAsync(run.Id, Ct);
        await svc.BeginApproveAsync(run.Id, Ct);
        var before = (await _reader.GetAsync(run.Id, Ct)).TotalCostUsd;
        _runtime.HangImplementTimes = 1;
        _runtime.HangReports = (progress, new RuntimeUsage(1_110_000, 50_000, 0, 1_000_000, 100_000));
        run = await svc.DispatchAsync(run.Id, Ct);

        Assert.Equal(RunStatus.Failed, run.Status);
        var cut = Assert.Single(await _store.ReadTurnsAsync(run.Id, "developer", Ct), t => t.CutShort == true);
        Assert.Equal(("t1", 2.04m, 1_110_000, 50_000, 1_000_000, 100_000), (cut.Task, cut.CostUsd, cut.InputTokens, cut.OutputTokens, cut.CacheReadTokens, cut.CacheWriteTokens));
        Assert.Contains("yarıda kesildi", cut.Output, StringComparison.Ordinal);
        Assert.Equal(before + 2.04m, run.TotalCostUsd);
        Assert.Empty(progress.Snapshot(run.Id)); // tur bitti, canli kayit dustu
    }

    [Fact]
    public async Task Bagimli_gorev_onceki_gorevin_raporunu_alir_kurallar_goreve_bilgi_dosyalari_ise_daraltilir()
    {
        _runtime.SpecJson = """
            {"summary":"s","architecture":"a","rules":["genel kural","arka yuz kurali","on yuz kurali"],"knowledge":["yok-boyle-dosya"],
             "tasks":[
               {"id":"t1","title":"api","description":"...","files":["a.cs"],"acceptance":["build"],"dependsOn":[],"ruleRefs":[0,1]},
               {"id":"t2","title":"ekran","description":"...","files":["b.vue"],"acceptance":["build"],"dependsOn":["t1"],"ruleRefs":[0,2]}]}
            """;
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief"), Ct);
        await _svc.AnalyzeAsync(run.Id, Ct);
        Assert.Contains("ruleRefs", _runtime.Calls[0].Messages[0].Content, StringComparison.Ordinal);
        await _svc.BeginApproveAsync(run.Id, Ct);
        run = await _svc.DispatchAsync(run.Id, Ct);
        Assert.Equal(RunStatus.Completed, run.Status);

        var impl = _runtime.Calls.Where(c => c.SchemaJson?.Contains("filesChanged", StringComparison.Ordinal) == true).Select(c => c.Messages[^1].Content).ToList();
        var t1 = impl.First(m => m.Contains("# Görev t1", StringComparison.Ordinal));
        var t2 = impl.First(m => m.Contains("# Görev t2", StringComparison.Ordinal));
        Assert.Contains("arka yuz kurali", t1, StringComparison.Ordinal);
        Assert.DoesNotContain("on yuz kurali", t1, StringComparison.Ordinal);
        Assert.Contains("on yuz kurali", t2, StringComparison.Ordinal);
        Assert.DoesNotContain("arka yuz kurali", t2, StringComparison.Ordinal);
        Assert.Contains("diğer 1 kural", t2, StringComparison.Ordinal);
        Assert.DoesNotContain("Bağımlı olduğun biten görevler", t1, StringComparison.Ordinal);
        Assert.Contains("Bağımlı olduğun biten görevler", t2, StringComparison.Ordinal);
        Assert.Contains("## t1 — api", t2, StringComparison.Ordinal);
        _runtime.SpecJson = null;
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
        Assert.Equal((PhaseStatus.Failed, PhaseCause.Interrupted, "süreç yeniden başladı"), (phases[^1].Status, phases[^1].Cause, phases[^1].Detail));
        Assert.True(phases[^1].IsSystemFailure);

        // Yeniden dene → dagitim ayni adimi (gelistirme) 1. tur olarak yeniden kosar; sistem fazi tur sayilmaz.
        var retry = await _svc.RetryAsync(run.Id, Ct);
        Assert.Equal(RunStep.Dispatch, retry.Step);
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
        Assert.Equal((PhaseStatus.Failed, PhaseCause.Cancelled), (cancelled[^1].Status, cancelled[^1].Cause));
        Assert.StartsWith("iptal", cancelled[^1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ajani_dolu_calisma_bekler_ajan_bosalinca_yeniden_dagitima_konur()
    {
        // A: onayli plan + developer'da Started faz (LLM cagrisi suruyor gibi). B: ayni ekip, dagitima girer.
        var a = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief a"), Ct);
        await _svc.AnalyzeAsync(a.Id, Ct);
        await _svc.BeginApproveAsync(a.Id, Ct);
        await _store.AppendPhaseAsync(a.Id, new Phase(DateTimeOffset.UtcNow, "t1", "gelistirme", "Geliştirme", "implement", "developer", 1, PhaseStatus.Started), Ct);

        var b = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief b"), Ct);
        await _svc.AnalyzeAsync(b.Id, Ct);
        await _svc.BeginApproveAsync(b.Id, Ct);
        var implementCalls = _runtime.Calls.Count(c => c.SchemaJson?.Contains("filesChanged", StringComparison.Ordinal) == true);
        b = await _svc.DispatchAsync(b.Id, Ct);

        // B Running kalir ama isi kuyrukta degil: WaitingSince isli, developer'a cagri yok; A'nin adimi kapanmadi, kimse uyandirilmadi.
        Assert.Equal(RunStatus.Running, b.Status);
        Assert.NotNull(b.WaitingSince);
        Assert.Contains("ajan bekleniyor", b.Detail, StringComparison.Ordinal);
        Assert.Equal(implementCalls, _runtime.Calls.Count(c => c.SchemaJson?.Contains("filesChanged", StringComparison.Ordinal) == true));
        Assert.Empty(_scheduler.Scheduled);

        // Yeniden baslatma: bekleyen calisma Interrupted OLMAZ, dagitim kuyruga geri girer (ortada yarim LLM cagrisi yok).
        Assert.Equal(1, await _svc.MarkInterruptedAsync(Ct)); // yalniz A (Started fazi vardi)
        Assert.Equal(RunStatus.Running, (await _reader.GetAsync(b.Id, Ct)).Status);
        Assert.Equal((b.Id, RunStep.Dispatch), Assert.Single(_scheduler.Scheduled));
        _scheduler.Scheduled.Clear();

        // A'yi yeniden baslat ve iptal et: developer bosaldi → B dagitima kondu (uyandirma noktasi RunService).
        var retryA = await _svc.RetryAsync(a.Id, Ct);
        Assert.Equal(RunStatus.Running, retryA.Status);
        await _svc.CancelAsync(a.Id, Ct);
        Assert.Equal((b.Id, RunStep.Dispatch), Assert.Single(_scheduler.Scheduled));

        b = await _svc.DispatchAsync(b.Id, Ct);
        Assert.Equal(RunStatus.Completed, b.Status);
        Assert.Null(b.WaitingSince);
    }

    [Fact]
    public async Task Detay_akis_kopyasini_plani_ve_sirayi_verir()
    {
        var run = await _svc.CreateAsync(new RunRequest(Project: "test", Brief: "brief", Label: "etiket"), Ct);
        await _svc.AnalyzeAsync(run.Id, Ct);
        var detail = await _reader.GetDetailAsync(run.Id, Ct);
        Assert.Equal("etiket", detail.Label);
        Assert.Equal(KlasikKey, detail.WorkflowDef!.Key);
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
        Assert.Equal((RunStep.Analyze, RunStatus.Running), (retryB.Step, retryB.Status));
        var retryA = await _svc.RetryAsync(a.Id, Ct);
        Assert.Equal((RunStep.Approval, RunStatus.AwaitingApproval), (retryA.Step, retryA.Status));

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
        Assert.Contains("i mi I mı", managerCall.Messages[^1].Content, StringComparison.Ordinal);
        var devCalls = _runtime.Calls.Where(c => c.SchemaJson?.Contains("filesChanged", StringComparison.Ordinal) == true).ToList();
        Assert.Contains("manager → developer · answer", devCalls[1].Messages[^1].Content, StringComparison.Ordinal);
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

        /// <summary>Plani onaylayan ajan (manager) reddetsin. Testciden AYRI: ikisi de ayni verdict semasini kullanir.</summary>
        public bool RejectPlan { get; set; }

        /// <summary>Developer engellensin (blocked + soru).</summary>
        public bool BlockImplement { get; set; }

        /// <summary>Developer yalniz ilk N implement cagrisinda engellensin (manager cevabi sonrasi devam etsin).</summary>
        public int BlockImplementTimes { get; set; }

        /// <summary>Manager (can_ask hedefi) cevap vermesin, kullaniciya yukseltsin.</summary>
        public bool ManagerEscalates { get; set; }

        /// <summary>Kota penceresi yuzdesi (limit korumasi testi); null = kota bilgisi yok.</summary>
        public double? LimitPercent { get; set; }

        /// <summary>Saglayici CAGRI SIRASINDA kota reddi versin (LimitGuard'in onbellegi kacirdiginda olan).</summary>
        public int LimitMidCallTimes { get; set; }

        /// <summary>Ilk N gelistirme turu hic donmesin (iptal edilene kadar asili): tur bekcisi testi.</summary>
        public int HangImplementTimes { get; set; }

        /// <summary>Asili tur, asilmadan once bu kullanimi canli akisa bildirsin (kesilen turun maliyeti testi).</summary>
        public (ProgressRegistry Registry, RuntimeUsage Usage)? HangReports { get; set; }

        private static async Task<RuntimeTurnResponse> HangAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("ulasilmaz");
        }

        public Task<RuntimeTurnResponse> TurnAsync(RuntimeTurnRequest request, CancellationToken ct)
        {
            Calls.Add(request);
            if (HangImplementTimes > 0 && request.SchemaJson?.Contains("filesChanged", StringComparison.Ordinal) == true)
            {
                HangImplementTimes--;
                if (HangReports is { } h && request.ProgressUrl is { } url)
                {
                    h.Registry.Report(url[(url.LastIndexOf('/') + 1)..], new ProgressEvent(null, null, "usage", MessageId: "m1", Usage: h.Usage));
                }

                return HangAsync(ct);
            }

            if (LimitMidCallTimes > 0)
            {
                LimitMidCallTimes--;
                throw new RuntimeLimitReachedException("runtime runtime.provider_limit: usage limit reached");
            }

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
                var isPlanGate = request.SystemPrompt.Contains("MANAGER", StringComparison.Ordinal);
                var review = (RejectTests && isTester) || (RejectPlan && isPlanGate)
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

        public Task<IReadOnlyList<RuntimeLocalUsage>> ListLocalUsageAsync(DateTimeOffset since, DateTimeOffset? until, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RuntimeLocalUsage>>([]);

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

    /// <summary>Is kanali yerine: RunService'in kuyruga koydugu (calisma, adim) ciftlerini kaydeder.</summary>
    private sealed class FakeScheduler : IRunScheduler
    {
        public List<(string RunId, RunStep Step)> Scheduled { get; } = [];

        public void Schedule(string runId, RunStep runStep) => Scheduled.Add((runId, runStep));
    }
}
