namespace MrHobist.AITeam.Domain.Runs;

/// <summary>Icerigin fiilen ulastigi yer. Her tur bununla kaydedilir (CLAUDE.md §4).</summary>
public enum Destination
{
    Local,
    Anthropic,
    Nvidia,
    Openai,
}

/// <summary>Bir calismanin hassasiyeti: icerik hangi hedeflere cikabilir.</summary>
public enum Sensitivity
{
    /// <summary>Yalniz bu makine; hicbir LLM'e cikmaz.</summary>
    Local,
    /// <summary>Anthropic'e cikabilir; NVIDIA'ya ve OpenAI'ye cikmaz.</summary>
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
    /// <summary>
    /// Akis takildi ve kullanicidan bir secim bekliyor (red turu bitti, ajan soru sordu, tekrarlanan hata). Soru ve
    /// secenekler <see cref="Run.Question"/>'da; cevap <c>POST /runs/{id}/answer</c> (docs/DOMAIN.md → Takilma).
    /// </summary>
    AwaitingInput,
}

public enum PhaseStatus
{
    Started,
    Done,
    Rejected,
    Failed,
    Skipped,
}

/// <summary>
/// Fazi kim kapatti (docs/DOMAIN.md → Yarim kalan adim). <see cref="Agent"/> ajanin kendi sonucu; digerleri SISTEM kaynaklidir
/// (limit beklemesi, kullanici iptali, surec yeniden basladi): tur sayilmaz, tavana girmez. Eski kayitlarda alan yok = <see cref="Agent"/>.
/// JSON'da adiyla tasinir; yeni uye SONA eklenir (CLAUDE.md §5).
/// </summary>
public enum PhaseCause
{
    Agent,
    Limit,
    Cancelled,
    Interrupted,
    /// <summary>Tur hareketsiz kaldi ya da ust sinira dayandi (2026-09-23). Yazilan dosyalar diskte: "devam et" ile surer.</summary>
    Timeout,
}

/// <summary>
/// Calismanin kaldigi adim: "yeniden dene" / limit surdurmesi buradan surer (docs/DOMAIN.md → Yasam dongusu).
/// <see cref="Analyze"/> analist turu · <see cref="Approval"/> plan onay bekliyor (kuyruga is girmez) · <see cref="Dispatch"/> dagitim.
/// Eski kayitlarda alan yok: plan yoksa analiz, varsa dagitim sayilir. JSON'da adiyla tasinir; yeni uye SONA eklenir.
/// </summary>
public enum RunStep
{
    Analyze,
    Approval,
    Dispatch,
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
/// <see cref="Step"/> kaldigi adim (durum bilgisi; <see cref="Detail"/> yalniz gorunum metnidir, karar ona bakmaz).
/// <see cref="WaitingSince"/> dolu ise calisma Running'dir ama hazir gorevin ajani baska calismada doludur: is kuyrukta degil,
/// bir adim kapaninca RunService onu yeniden dagitima koyar (docs/DOMAIN.md → Paralellik).
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
    int Retries = 0,
    string Project = "",
    string OwnerId = "local",
    UserQuestion? Question = null,
    DateTimeOffset? ResumeAt = null,
    RunStep? Step = null,
    DateTimeOffset? WaitingSince = null,
    /// <summary>
    /// Bu calismanin turlarinda harcanan girdi token'i toplami. Tur basina kirilim <c>run_turn</c>'de durur;
    /// buradaki toplam proje butcesinin (<c>Project.MaxTokens</c>) her istekte turlari taramasini onler --
    /// maliyet (<see cref="TotalCostUsd"/>) hangi yolla birikiyorsa token de ayni yoldan birikir.
    /// 2026-09-22 oncesi calismalarda 0: olculmedi demektir, sifir harcandi demek degil.
    /// </summary>
    long InputTokens = 0,
    /// <summary>Bu calismanin turlarinda uretilen cikti token'i toplami. Bkz. <see cref="InputTokens"/>.</summary>
    long OutputTokens = 0,
    /// <summary>Is verilirken eklenen dosyalar (docs/DOMAIN.md → Ekler). Sona eklendi (CLAUDE.md §5); null = ek yok.</summary>
    IReadOnlyList<RunAttachment>? Attachments = null)
{
    /// <summary>Proje butcesi icin tek olcu: girdi + cikti. Onbellek kirilimi rapordadir, tavanda degil.</summary>
    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>
    /// Kullanicinin durdurabilecegi ya da "kapat" diyebilecegi durumlar. Dusen calismalar (Failed/Interrupted/BudgetExceeded)
    /// da iptal edilir: yoksa "yeniden dene ya da vazgec" kararinin ikinci sikki olmaz ve is gelen kutusundan hic dusmez.
    /// </summary>
    public bool IsCancellable => Status is RunStatus.Running or RunStatus.AwaitingApproval or RunStatus.Paused
        or RunStatus.Failed or RunStatus.Interrupted or RunStatus.BudgetExceeded or RunStatus.AwaitingInput;

    /// <summary>"Yeniden dene" ile kaldigi adimdan devam edebilecek durumlar. Limit beklemesi (<see cref="ResumeAt"/>) de elle surdurulebilir.</summary>
    public bool IsRetryable => Status is RunStatus.Failed or RunStatus.Interrupted or RunStatus.BudgetExceeded or RunStatus.Cancelled
        || (Status == RunStatus.Paused && ResumeAt is not null);
}

/// <summary>
/// Akis takildiginda kullaniciya sorulan soru (docs/DOMAIN.md → Takilma). <see cref="Options"/> mudahale secenekleri;
/// hangisinin ne yaptigi <see cref="QuestionOption.Id"/> ile sabittir: <c>retry</c> (notla yeniden), <c>skip</c>
/// (adimi gec / elle halledildi / kabul), <c>cancel</c>. Cevap gelince <see cref="Run.Question"/> temizlenir; kayit messages.jsonl'de.
/// </summary>
public sealed record UserQuestion(
    DateTimeOffset Ts,
    string Agent,
    string Text,
    IReadOnlyList<QuestionOption> Options,
    string? Task = null,
    string? Stage = null,
    string? Context = null);

public sealed record QuestionOption(string Id, string Label, string Detail, bool NeedsNote = false);

/// <summary>Ajanin bir arac cagrisi (dosya yazma, komut...). Denetim kaydi: turun icinde sirali.</summary>
public sealed record ToolUse(string Tool, string? Target);

/// <summary>Analistin urettigi gorev; <see cref="DependsOn"/> gercek bagimliliklar.</summary>
public sealed record RunTask(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Acceptance,
    IReadOnlyList<string> DependsOn,
    /// <summary>
    /// Bu gorevi baglayan kurallarin <see cref="Spec.Rules"/> icindeki 0 tabanli sirasi. Bos/null = tum kurallar (eski planlar,
    /// analist emin degilse). 2026-09-23 maliyet kaldiraci: on yuz gorevine arka yuz kurallari her ic turda okunmasin. Sona eklendi.
    /// </summary>
    IReadOnlyList<int>? RuleRefs = null);

/// <summary>Analist ciktisi: <c>runs/{id}/spec.json</c>.</summary>
/// <remarks>
/// <see cref="Knowledge"/>: gorevlerin ihtiyac duydugu bilgi dosyalari (ajan md'sindeki <c>includes</c> anahtarlari). Bos/null = ajanin
/// tum bilgi dosyalari. 2026-09-23 maliyet kaldiraci: yalniz on yuz isinde arka yuz bilgisi sistem istemine girmesin. Sona eklendi.
/// </remarks>
public sealed record Spec(string Summary, string Architecture, IReadOnlyList<string> Rules, IReadOnlyList<RunTask> Tasks, IReadOnlyList<string>? Knowledge = null);

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
    string? Detail = null,
    PhaseCause? Cause = null,
    /// <summary>
    /// Bu adim KOSMADAN ONCEKI calisma alani hali (golge git taniticisi); alinamadiysa null. Uretici adimlarda
    /// doldurulur: red tavaninda kullanici "son turu geri al" derse donulecek nokta budur. Sutun YOK -- faz
    /// govdesiyle birlikte <c>run_phase.data</c> JSON'unda tasinir, sorgulanmaz.
    /// </summary>
    string? Snapshot = null)
{
    /// <summary>Sistemden dogan faz (limit, iptal, kesinti): ajanin hatasi degil; tur sayilmaz, tavana girmez.</summary>
    public bool IsSystemFailure => Status == PhaseStatus.Failed && Cause is PhaseCause.Limit or PhaseCause.Cancelled or PhaseCause.Interrupted or PhaseCause.Timeout;

    /// <summary>Onceki deneme yarida kesildi (yeniden baslatma, zaman asimi, cagri sirasinda limit): dizinde yarim is olabilir.</summary>
    public bool IsCutShort => Status == PhaseStatus.Failed && Cause is PhaseCause.Interrupted or PhaseCause.Timeout or PhaseCause.Limit;
}

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
    int? OutputTokens = null,
    IReadOnlyList<ToolUse>? ToolUses = null,
    int? Turns = null,
    /// <summary><see cref="InputTokens"/> icindeki onbellekten OKUNAN pay. Yeni alan SONA eklendi (CLAUDE.md §5).</summary>
    int? CacheReadTokens = null,
    /// <summary><see cref="InputTokens"/> icindeki onbellege YAZILAN pay.</summary>
    int? CacheWriteTokens = null,
    /// <summary>
    /// Arac tanimlari istemde miydi. Karakter/token kalibrasyonu yalniz araCsiz turlari ornek alir: arac tanimlari
    /// istemde gorunmeyen ~20k token ekler (runtime olcumu 2026-09-19: 23k → 4.5k). Eski satirlarda null. Sona eklendi (CLAUDE.md §5).
    /// </summary>
    bool? ToolsOffered = null,
    /// <summary>Tasinan gecmisin sikistirma oncesi/sonrasi olcusu; gecmis tasinmadiysa null. Sona eklendi (CLAUDE.md §5).</summary>
    ContextStats? Context = null,
    /// <summary>
    /// Tur yarida kesildi (zaman asimi, iptal, saglayici hatasi): kullanim runtime'in canli bildiriminden, maliyet fiyat
    /// tablosundan tahmin. Eski satirlarda null. Sona eklendi (CLAUDE.md §5).
    /// </summary>
    bool? CutShort = null,
    /// <summary>
    /// Bu turda ajana acilan MCP sunuculari (anahtarlar). MCP kullanim raporu "verildi ama kullanilmadi"yi bundan cikarir: her
    /// sunucunun arac semalari her ic turda baglama girer, kullanilmayan sunucu bosuna odenir. Eski satirlarda null. Sona eklendi.
    /// </summary>
    IReadOnlyList<string>? McpServers = null);

/// <summary>
/// Bir turda tasinan gecmisin olcusu (docs/DOMAIN.md → Baglam butcesi). Sikistirmanin neyi dusurdugunu ve hangi
/// kalibrasyonla karar verdigini tur kaydinda tutar; <c>scripts/context-report.py</c> bunu okur.
/// <see cref="CalibrationSamples"/> 0 ise <see cref="CharsPerToken"/> olculmemis varsayilandir.
/// </summary>
public sealed record ContextStats(
    int CarriedMessages,
    int CarriedChars,
    int KeptMessages,
    int KeptChars,
    double CharsPerToken,
    int CalibrationSamples);

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
