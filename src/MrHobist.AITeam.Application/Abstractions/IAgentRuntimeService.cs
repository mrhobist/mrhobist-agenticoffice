using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Application.Abstractions;

/// <summary>Bir LLM cagrisinin tamami. Gecmis <see cref="Messages"/> ile gider; runtime hicbir sey hatirlamaz.</summary>
public sealed record RuntimeTurnRequest(
    string SystemPrompt,
    IReadOnlyList<RuntimeMessage> Messages,
    Provider Provider,
    string Model,
    string? SchemaJson = null,
    int MaxTokens = 8192,
    string? ReasoningEffort = "low",
    /// <summary>Ajanin kullanabilecegi araclar (Read, Write, Edit, Bash, Glob, Grep). Bos = arac yok, tek tur.</summary>
    IReadOnlyList<string>? Tools = null,
    /// <summary>Araclarin calisacagi dizin: projenin hedef dizini (mutlak). Yazma bunun disina cikamaz.</summary>
    string? Cwd = null,
    int? MaxTurns = null,
    /// <summary>Canli arac akisi geri cagrisi (tek kullanimlik belirtecli loopback adres); null = akis yok.</summary>
    string? ProgressUrl = null,
    /// <summary>
    /// Sistem promptunun SDK'ya nasil verildigi (<see cref="SystemPromptModes"/>). Karar burada, .NET'te;
    /// runtime yalniz esler. Yeni alan SONA eklendi (CLAUDE.md §5); yoksa eski davranis (<c>replace</c>).
    /// </summary>
    string SystemPromptMode = SystemPromptModes.Replace,
    /// <summary>
    /// Ajana acilan MCP sunuculari (anahtar → baglanti). Yalniz aracli turda ve MCP destekleyen saglayicida dolu; hangi ajanin
    /// hangisini aldigi .NET'in karari (ajan md'si <c>mcp</c>). Sona eklendi (CLAUDE.md §5); null = yok.
    /// </summary>
    IReadOnlyDictionary<string, RuntimeMcpServer>? McpServers = null,
    /// <summary>
    /// Cwd DISINDA okunabilecek dizinler (is ekleri). Yazma araclari yine yalniz <see cref="Cwd"/> altinda; Bash bu dizinlerdeki yollari
    /// kullanabilir (ör. bir resmi projeye kopyalamak). Sona eklendi; null = yok.
    /// </summary>
    IReadOnlyList<string>? ReadDirs = null,
    /// <summary>
    /// Modele HIC sunulmayacak araclar (SDK <c>disallowed_tools</c>): MCP sunucusunda secilmemis araclar, <c>mcp__{key}__{arac}</c>.
    /// Semalari baglama girmez. Sona eklendi; null = yok.
    /// </summary>
    IReadOnlyList<string>? DisallowedTools = null,
    /// <summary>
    /// Istem onbelleginin omru (<see cref="Domain.Settings.CacheTtls"/>): Ayarlar'dan, yalniz Anthropic'e. null = CLI varsayilani (2026-09-24
    /// olcumu: 1 sa). Runtime yalniz CLI degiskenine esler. Sona eklendi (CLAUDE.md §5).
    /// </summary>
    string? CacheTtl = null);

/// <summary>Runtime'a giden MCP baglantisi (SDK bicimi): <c>stdio</c> komut/args/env · <c>http</c>|<c>sse</c> url/basliklar.</summary>
public sealed record RuntimeMcpServer(
    string Type,
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string>? Env = null,
    string? Url = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    /// <summary>Izin listesi (arac adlari, oneksiz); null = hepsi. Runtime'in izin denetimi disindakileri reddeder. Sona eklendi.</summary>
    IReadOnlyList<string>? Tools = null);

/// <summary>MCP baglanti denemesinin sonucu: sunucu acildi mi, hangi araclari sunuyor. Durum degil, anlik olcum.</summary>
public sealed record RuntimeMcpProbe(bool Ok, string Detail, IReadOnlyList<RuntimeMcpTool> Tools, string? ServerName = null, string? ServerVersion = null);

public sealed record RuntimeMcpTool(string Name, string? Description);

/// <summary>
/// Claude Code'un KENDI sistem promptu korunsun mu. Olculdu 2026-09-21 (ayni brief/model/efor):
/// <list type="bullet">
/// <item><c>replace</c>: SDK'ya duz string -> Claude Code'un "nasil verimli calisirim" kilavuzu SILINIR. Yurutme
/// adiminda ajan ayni motorla ama kilavuzsuz kosar: tek kisilik akis 55 ic tur, Claude Code 11-16.</item>
/// <item><c>claude_code</c>: kilavuz korunur, bizimki sonuna eklenir. Ama planlama turunda (buyuk, ic ice Spec
/// semasi) model semayi dolduramadi: 5 denemede 'rules'/'tasks' eksik, bir kez de "rule1 / a.cs" taslagi.</item>
/// </list>
/// Dolayisiyla mod ADIM BASINA secilir: dosya yazan/komut kosan adimlar kilavuzu alir, plan ureten adimlar almaz.
/// </summary>
public static class SystemPromptModes
{
    public const string Replace = "replace";

    public const string ClaudeCode = "claude_code";
}

public sealed record RuntimeMessage(string Role, string Content);

/// <summary>
/// Tur kullanimi. <paramref name="InputTokens"/> TOPLAMDIR: dogrudan + onbellege yazilan + onbellekten okunan.
/// <paramref name="CacheReadTokens"/> ve <paramref name="CacheWriteTokens"/> o toplamin icindeki paylardir
/// (ikisi de 0 = saglayici onbellek bildirmiyor). Yeni alanlar SONA eklendi (CLAUDE.md §5).
/// <paramref name="CacheWrite5mTokens"/>: yazmanin 5 dakikalik payi (ucuz omur); 0 = hepsi 1 saatlik ya da bildirilmedi.
/// <paramref name="PeakContextTokens"/>: turdaki en buyuk tek API cagrisinin girdisi, yani baglamin tepesi (0 = olculemedi).
/// </summary>
public sealed record RuntimeUsage(int InputTokens, int OutputTokens, int ReasoningChars, int CacheReadTokens = 0, int CacheWriteTokens = 0, int CacheWrite5mTokens = 0, int PeakContextTokens = 0);

public sealed record RuntimeToolUse(string Tool, string? Target);

public sealed record RuntimeTurnResponse(
    string Text,
    string? StructuredJson,
    Provider Provider,
    string Model,
    Destination Destination,
    RuntimeUsage Usage,
    decimal? CostUsd,
    double DurationS,
    int Attempts,
    IReadOnlyList<RuntimeToolUse>? ToolUses = null,
    int Turns = 1);

/// <summary>Katalogda gorunmek erisilebilir olmak DEGILDIR; <see cref="Reachable"/> fiilen cagirarak dogrulanir.</summary>
public sealed record RuntimeModelInfo(Provider Provider, string Model, bool Reachable, string Detail);

/// <summary>
/// Saglayici kimligi (docs/DOMAIN.md → Model, efor ve kimlik). <see cref="Method"/>: <c>session</c> (CLI oturumu: Claude Code / Codex)
/// | <c>apikey</c> (kayitli API anahtari; <see cref="Account"/> maskeli son) | null (giris yok).
/// </summary>
public sealed record RuntimeAuthStatus(Provider Provider, bool LoggedIn, string? Account, string Detail, string? Method = null);

/// <summary>Giris akisi kullanicinin makinesinde basladi (konsol + tarayici); tamamlanmasi kullanicida. Kimlik bilgisi tasinmaz.</summary>
public sealed record RuntimeLoginStarted(Provider Provider, bool Started, string Detail);

/// <summary>Saglayicinin bildirdigi tek kota penceresi. <see cref="Percent"/> = kullanilan yuzde; kalan = 100 - Percent.</summary>
public sealed record RuntimeUsageLimit(string Kind, string? Group, double Percent, string? Severity, DateTimeOffset? ResetsAt, string? Scope, bool IsActive);

/// <summary>Bir saglayicinin kalan kullanimi (ust bar). Saglayici vermiyorsa <see cref="Available"/> false + neden.</summary>
public sealed record RuntimeProviderLimits(Provider Provider, bool Available, string Detail, string? Subscription, DateTimeOffset? FetchedAt, IReadOnlyList<RuntimeUsageLimit> Limits);

/// <summary>
/// Python runtime sozlesmesi (CLAUDE.md §1). Tek isi LLM cagrisi; is kurali burada yoktur.
/// Sahte adaptorle <c>ServiceTests</c> Python olmadan gecer.
/// </summary>
public interface IAgentRuntimeService
{
    Task<RuntimeTurnResponse> TurnAsync(RuntimeTurnRequest request, CancellationToken ct);

    Task<IReadOnlyList<RuntimeModelInfo>> ListModelsAsync(Provider? provider, CancellationToken ct);

    /// <summary>Saglayici basina giris durumu; UI ilk yuklemede bakar. <paramref name="refresh"/> runtime'in kimlik onbellegini atlatir ("Yeniden kontrol et").</summary>
    Task<IReadOnlyList<RuntimeAuthStatus>> ListAuthAsync(Provider? provider, bool refresh, CancellationToken ct);

    /// <summary>
    /// Saglayicinin kendi giris akisini baslatir (Anthropic: <c>claude auth login</c>, OpenAI: <c>codex login</c>; yeni konsol).
    /// <paramref name="mode"/>: <c>claudeai | console | chatgpt | apikey</c>. <paramref name="apiKey"/> yalniz <c>apikey</c> modunda: runtime
    /// dogrular ve kullanici profiline yazar; Api ne saklar ne gunlukler.
    /// </summary>
    Task<RuntimeLoginStarted> LoginAsync(Provider provider, string mode, string? email, string? apiKey, CancellationToken ct);

    Task<RuntimeAuthStatus> LogoutAsync(Provider provider, CancellationToken ct);

    /// <summary>Kalan kullanim (kota pencereleri). Runtime 30 s onbellekler; <paramref name="refresh"/> atlar.</summary>
    Task<IReadOnlyList<RuntimeProviderLimits>> ListLimitsAsync(Provider? provider, bool refresh, CancellationToken ct);

    /// <summary>
    /// Makinedeki CLI oturum kayitlarindan kaynak/klasor/model basina token (kim ne harcadi). Kaynak CLI'nin giris noktasidir:
    /// ofis ajani <c>sdk-py</c>, etkilesimli oturumlar <c>cli</c> / <c>claude-desktop</c>... Fiyat ve pay burada degil, .NET'te.
    /// </summary>
    Task<IReadOnlyList<RuntimeLocalUsage>> ListLocalUsageAsync(DateTimeOffset since, DateTimeOffset? until, CancellationToken ct);

    /// <summary>
    /// MCP sunucusuna baglanip araclarini listeler (yonetim ekrani "Baglantiyi dene"). Durumsuz: runtime sunucuyu acar, listeler,
    /// kapatir. Baglanamazsa hata degil <see cref="RuntimeMcpProbe.Ok"/> = false + neden.
    /// </summary>
    Task<RuntimeMcpProbe> ProbeMcpAsync(RuntimeMcpServer server, CancellationToken ct);
}

public sealed record RuntimeLocalUsage(string Source, string Project, string Model, int Messages, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, long CacheWrite5mTokens = 0);

/// <summary>Runtime'a ulasilamiyor: Api 503 <c>runtime.unavailable</c> doner.</summary>
public sealed class RuntimeUnavailableException(string message) : Exception(message);

/// <summary>Runtime cevap verdi ama hata dondu (5xx): kapali degil, ucu bozuk. Api 502 <c>runtime.error</c> doner.</summary>
public sealed class RuntimeErrorException(string message) : Exception(message);

/// <summary>
/// Tur zamaninda bitmedi: hareketsiz kaldi ya da ust sinira dayandi (2026-09-23). Kullanici iptali DEGIL -- o
/// <see cref="OperationCanceledException"/> olarak kalir. Faz <c>Timeout</c> nedeniyle kapanir; yazilan dosyalar diskte
/// oldugu icin "Yeniden dene" gorevi "devam et" notuyla surdurur. Otomatik tekrar YOK: ayni uzun turu bastan kostururdu.
/// </summary>
public sealed class RuntimeTimeoutException(string message) : Exception(message);

/// <summary>
/// Saglayici turu kota penceresi doldugu icin reddetti (<c>runtime.provider_limit</c>). Hata degil BEKLEMEDIR:
/// <see cref="Runs.LimitGuard"/> cagri oncesi bakar ama yuzdeler 90 s onbelleklidir, pencere tam o aralikta
/// dolabilir. <c>AgentCaller</c> bunu <c>LimitReachedException</c>'a cevirir ki calisma Failed degil Paused olsun
/// ve pencere sifirlaninca <c>RunResumer</c> kaldigi adimdan surdursun (2026-09-22).
/// </summary>
public sealed class RuntimeLimitReachedException(string message) : Exception(message);
