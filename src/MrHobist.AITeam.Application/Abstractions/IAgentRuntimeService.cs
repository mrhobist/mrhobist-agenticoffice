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
    string SystemPromptMode = SystemPromptModes.Replace);

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
/// </summary>
public sealed record RuntimeUsage(int InputTokens, int OutputTokens, int ReasoningChars, int CacheReadTokens = 0, int CacheWriteTokens = 0);

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
}

/// <summary>Runtime'a ulasilamiyor: Api 503 <c>runtime.unavailable</c> doner.</summary>
public sealed class RuntimeUnavailableException(string message) : Exception(message);

/// <summary>Runtime cevap verdi ama hata dondu (5xx): kapali degil, ucu bozuk. Api 502 <c>runtime.error</c> doner.</summary>
public sealed class RuntimeErrorException(string message) : Exception(message);

/// <summary>
/// Saglayici turu kota penceresi doldugu icin reddetti (<c>runtime.provider_limit</c>). Hata degil BEKLEMEDIR:
/// <see cref="Runs.LimitGuard"/> cagri oncesi bakar ama yuzdeler 90 s onbelleklidir, pencere tam o aralikta
/// dolabilir. <c>AgentCaller</c> bunu <c>LimitReachedException</c>'a cevirir ki calisma Failed degil Paused olsun
/// ve pencere sifirlaninca <c>RunResumer</c> kaldigi adimdan surdursun (2026-09-22).
/// </summary>
public sealed class RuntimeLimitReachedException(string message) : Exception(message);
