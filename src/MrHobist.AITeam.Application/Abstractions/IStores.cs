using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Projects;
using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Domain.Workflows;

namespace MrHobist.AITeam.Application.Abstractions;

/// <summary><c>config/agents/*.md</c> + <c>config/knowledge/*.md</c>. Her okuma diski yeniden okur; config degisince Api yeniden yukler.</summary>
public interface IAgentStore
{
    Task<Team> LoadTeamAsync(CancellationToken ct);

    /// <summary>Olusturur ya da uzerine yazar; atomik. Cagiran once <see cref="Team.Validate"/> ile dogrulamis olmali.</summary>
    Task SaveAgentAsync(Agent agent, CancellationToken ct);

    /// <summary>Md dosyasini siler. Referans denetimi (akislar, can_ask) cagiranin isidir.</summary>
    Task DeleteAgentAsync(string key, CancellationToken ct);

    /// <summary><c>config/knowledge/{key}.md</c> olusturur ya da uzerine yazar (frontmatter <c>title</c> + govde); atomik.</summary>
    Task SaveKnowledgeAsync(Knowledge knowledge, CancellationToken ct);

    /// <summary>Bilgi md'sini siler. Referans denetimi (includes) cagiranin isidir.</summary>
    Task DeleteKnowledgeAsync(string key, CancellationToken ct);

    /// <summary>Kullanicinin yukledigi ajan md'sini (frontmatter + prompt) cozer; bicim hatasi <c>agent.markdown_invalid</c>. Dosya yazmaz.</summary>
    Agent ParseAgentMarkdown(string key, string markdown);

    /// <summary>Kullanicinin yukledigi bilgi md'sini cozer (<c>title</c> yoksa anahtar). Dosya yazmaz.</summary>
    Knowledge ParseKnowledgeMarkdown(string key, string markdown);
}

/// <summary>Projeler; is yalniz bir projenin icinde baslar (docs/DOMAIN.md → Projeler).</summary>
public interface IProjectStore
{
    Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct);

    /// <summary>Yoksa <c>project.not_found</c>.</summary>
    Task<Project> LoadAsync(string key, CancellationToken ct);

    Task SaveAsync(Project project, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);
}

/// <summary><c>config/workflows/{key}.json</c>; <c>default</c> her zaman vardir.</summary>
public interface IWorkflowStore
{
    /// <summary>Dosya adlarindan anahtarlar, sirali. Gecersiz adli dosyalar yok sayilir.</summary>
    Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct);

    /// <summary>Yoksa <c>workflow.not_found</c>.</summary>
    Task<Workflow> LoadAsync(string key, CancellationToken ct);

    Task SaveAsync(Workflow workflow, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);
}

/// <summary>
/// Calisma gecmisi: <c>run</c> ve alt tablolari (tur, mesaj, faz). Append-only satir duzeyinde -- satirlar eklenir,
/// guncellenmez; sira ekleme sirasidir (monoton kimlik), ayri bir sayac hesaplanmaz. Yazan tek yazici Api icindeki is kanalidir; uclar IRunReader ile okur.
/// Bozuk govde (JSON) olan satir yok sayilir, geri kalani kurtarilir.
/// </summary>
public interface IRunStore
{
    Task CreateAsync(Run run, CancellationToken ct);

    Task UpdateAsync(Run run, CancellationToken ct);

    Task WriteSpecAsync(string runId, Spec spec, CancellationToken ct);

    /// <summary>Calisma baslarken secilen akisin kopyasi. Config sonradan degisse de calisma bunu okur.</summary>
    Task WriteWorkflowAsync(string runId, Workflow workflow, CancellationToken ct);

    Task<Workflow?> ReadWorkflowAsync(string runId, CancellationToken ct);

    Task AppendTurnAsync(string runId, Turn turn, CancellationToken ct);

    Task AppendMessageAsync(string runId, Message message, CancellationToken ct);

    Task AppendPhaseAsync(string runId, Phase phase, CancellationToken ct);

    Task<Run?> GetAsync(string runId, CancellationToken ct);

    /// <summary>Yeni → eski. <paramref name="project"/> verilirse yalniz o projenin calismalari.</summary>
    Task<IReadOnlyList<Run>> ListAsync(int limit, CancellationToken ct, string? project = null);

    Task<Spec?> ReadSpecAsync(string runId, CancellationToken ct);

    Task<IReadOnlyList<Turn>> ReadTurnsAsync(string runId, string agent, CancellationToken ct);

    /// <summary>
    /// Son <paramref name="runLimit"/> calismanin turlarinin YALNIZ toplama alanlari (saglayici, model, token, maliyet).
    /// Kullanim ozeti icin: prompt/cikti metinleri okunmaz, calisma basina ayri sorgu atilmaz.
    /// </summary>
    Task<IReadOnlyList<TurnUsage>> ReadUsageAsync(int runLimit, CancellationToken ct);

    /// <summary>
    /// <c>[since, until)</c> araligindaki turlarin toplama alanlari (zaman araligi sorguda; calisma sayisi siniri yok).
    /// Harcama raporu icin: "son N calisma" + bellekte suzme hem gereksiz satir okur hem de aralik N'i asarsa eksik sayar.
    /// </summary>
    Task<IReadOnlyList<TurnUsage>> ReadUsageBetweenAsync(DateTimeOffset since, DateTimeOffset? until, CancellationToken ct);

    /// <summary>
    /// Karakter/token kalibrasyon ornekleri (<see cref="Runs.TokenCalibration"/>): saglayici+model icin en yeni
    /// <paramref name="limit"/> ARACSIZ tur (<see cref="Turn.ToolsOffered"/> = false), yeniden eskiye. Prompt/cikti metni okunmaz.
    /// </summary>
    Task<IReadOnlyList<CalibrationSample>> ReadCalibrationSamplesAsync(string provider, string model, int limit, CancellationToken ct);

    /// <summary>
    /// MCP kullanim raporu icin son <paramref name="runLimit"/> calismanin MCP'li turlari: ajana acilan sunucular ve arac
    /// cagrilari. Prompt/cikti metni okunmaz. MCP'siz turlar donmez.
    /// </summary>
    Task<IReadOnlyList<McpTurnUsage>> ReadMcpUsageAsync(int runLimit, CancellationToken ct);

    /// <summary>Bu calismada LLM cagirmis ajanlar (silinmis ajanlar dahil).</summary>
    Task<IReadOnlyList<string>> ListConversationsAsync(string runId, CancellationToken ct);

    Task<IReadOnlyList<Message>> ReadMessagesAsync(string runId, CancellationToken ct);

    Task<IReadOnlyList<Phase>> ReadPhasesAsync(string runId, string task, CancellationToken ct);

    /// <summary>Calismanin TUM fazlari (gorevler karisik), ekleme sirasiyla: gorev basina ayri sorgu atmamak icin.</summary>
    Task<IReadOnlyList<Phase>> ReadRunPhasesAsync(string runId, CancellationToken ct);

    Task<IReadOnlyList<string>> ListTasksAsync(string runId, CancellationToken ct);

    /// <summary>
    /// Calismayi butunuyle siler; alt satirlar (tur, mesaj, faz) yabanci anahtar cascade'i ile duser. Append-only kural
    /// satir duzeyindedir; calisma silme yalniz proje silinirken, bitmis calismalar icin cagrilir
    /// (docs/DOMAIN.md → Projeler → Silme). Yoksa sessiz.
    /// </summary>
    Task DeleteAsync(string runId, CancellationToken ct);
}

/// <summary>Bir turun kullanim ozeti satiri: <see cref="IRunStore.ReadUsageAsync"/>. Tam <see cref="Turn"/> degil, yalniz toplanan alanlar.</summary>
public sealed record TurnUsage(string RunId, string Provider, string Model, int? InputTokens, int? OutputTokens, decimal? CostUsd, DateTimeOffset Ts);

/// <summary>Bir turun MCP ozeti: acilan sunucular (eski kayitlarda null) ve araclari (tam SDK adi, ör. <c>mcp__gh__create_issue</c>).</summary>
public sealed record McpTurnUsage(string RunId, string Agent, DateTimeOffset Ts, IReadOnlyList<string>? McpServers, IReadOnlyList<string> Tools);

/// <summary>Kalibrasyon ornegi: turun istemi (sistem + mesajlar, karakter), girdi tokeni (ic turlarin toplami) ve ic tur sayisi.</summary>
public sealed record CalibrationSample(int PromptChars, int InputTokens, int Turns);

/// <summary>Calisma alani ayarlari (limit korumasi). Kayit yoksa varsayilan.</summary>
public interface ISettingsStore
{
    Task<Domain.Settings.AppSettings> LoadAsync(CancellationToken ct);

    Task SaveAsync(Domain.Settings.AppSettings settings, CancellationToken ct);
}

/// <summary>
/// Projenin hedef dizininin diskteki mutlak yolu (depo koku + <c>targetDir</c>). Developer/testci araclari bu dizinde
/// calisir, yazma disina cikamaz. Dizin yoksa OLUSTURULUR (SDK cwd'nin var olmasini ister); dosya I/O burada, Application'da degil.
/// </summary>
public interface IWorkspaceLocator
{
    string RootOf(Project project);

    /// <summary>
    /// Depo icindeki klasorler (klasor secici icin; kullanici karari 2026-09-20: hedef dizin serbest metin degil).
    /// <paramref name="relativePath"/> depo kokune gore (<c>""</c> = kok); gizli ve uretilen klasorler (<c>.git</c>,
    /// <c>node_modules</c>, <c>bin</c>, <c>obj</c>, <c>.venv</c>…) listelenmez. Disari cikan yol <c>project.target_dir_invalid</c>.
    /// 2026-09-26'dan beri suruculu tam yol da gezilir (surucu koku <c>C:/</c> dahil): iceri alinacak proje depo disindadir.
    /// Erisilemeyen klasorler ve sistem/gizli klasorler atlanir.
    /// </summary>
    IReadOnlyList<WorkspaceDirectory> ListDirectories(string? relativePath);

    /// <summary>Projenin hedef dizinini icerigiyle siler; yoksa false. Depo disina cikamaz (RootOf ile ayni kural).</summary>
    bool DeleteRoot(Project project);

    /// <summary>
    /// Hedef dizin degerinin (depo icinde goreli ya da suruculu tam yol) mutlak yolu; dizini OLUSTURMAZ. Iki projenin ayni
    /// klasore baglanip baglanmadigi ve iceri alinacak klasorun varligi buna bakilarak anlasilir (2026-09-26).
    /// </summary>
    string PathOf(string targetDir);

    /// <summary>Hazir surucu kokleri (<c>C:/</c>, <c>D:/</c>): klasor secicinin depo disi gezinmesi baslangici (2026-09-26).</summary>
    IReadOnlyList<string> Drives();
}

/// <summary>
/// Bir dizinde zaten ne var (kullanici istegi 2026-09-26: baslamis projeyi iceri alip devam ettirmek). Yalniz OKUR:
/// dosya listesi (git deposuysa <c>git ls-files</c>, degilse <see cref="Codebase.SkippedDirs"/> budanarak yurunur), dal ve
/// uzak depo, oneri icin <c>package.json</c> ve README'nin basi. Siniflama <see cref="Codebase"/>'de (saf).
/// Hata firlatmaz: okunamayan kisim bos gelir -- tarama istemi zenginlestirir, calismayi durduramaz.
/// </summary>
public interface IWorkspaceInspector
{
    Task<WorkspaceScan> ScanAsync(string root, CancellationToken ct);
}

/// <summary>
/// Tarama sonucu: dosya listesi degil, OLCULMUS hali (<see cref="Codebase"/>) ve oneri. Ham liste (monorepoda 50 bin yol)
/// tarayicidan cikmaz; onbellekte de yalniz bu kucuk ozet durur. <see cref="Truncated"/>: dosya siniri asildi, olcu ilk
/// parcaya gore. <see cref="GitRemote"/> kimlik bilgisi AYIKLANMIS adres (<c>https://kullanici:belirtec@</c> kismi hic
/// tasinmaz). <see cref="GitBranch"/> kopuk HEAD'de kisa hash. <c>Suggested*</c>: <see cref="ProjectSuggestion.From"/>.
/// </summary>
public sealed record WorkspaceScan(
    bool Exists,
    Codebase Code,
    bool Truncated,
    bool IsGit,
    string? GitBranch,
    string? GitRemote,
    string SuggestedTitle,
    string SuggestedDescription)
{
    public static readonly WorkspaceScan Missing = new(false, Codebase.Empty, false, false, null, null, "", "");
}

/// <summary>Klasor secicinin bir satiri: ad ve yol (depo icinde goreli, depo disinda suruculu tam yol; ileri bolu).</summary>
public sealed record WorkspaceDirectory(string Name, string Path);

/// <summary>
/// Projeyi baslatma sozlesmesi (kullanici istegi 2026-09-20): proje kokundeki <c>run.cmd</c> yeni bir konsol
/// penceresinde kosulur; ne baslatilacagini developer o dosyaya yazar. Surec baglanmaz, cikti okunmaz: uygulama kullanicinin.
/// </summary>
public interface IProjectLauncher
{
    /// <summary>Kokte baslatici var mi (<c>run.cmd</c>).</summary>
    bool CanLaunch(string projectRoot);

    /// <summary>Baslaticiyi yeni pencerede kosar; surec kimligini doner. Yoksa <c>project.launch_missing</c>, kosamazsa <c>project.launch_failed</c>.</summary>
    int Launch(string projectRoot);
}
