using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using MrHobist.AITeam.Application.Abstractions;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>Suren LLM turunun sahibi: hangi calisma, ajan, gorev. Ilerleme bildirimi bu baglama yazilir.</summary>
public sealed record ProgressContext(string RunId, string Agent, string? Task, string? Stage);

/// <summary>Runtime'in tur sirasinda bildirdigi bir arac cagrisi (<c>POST /progress/{token}</c> govdesi).</summary>
public sealed record ProgressEvent(string Tool, string? Target);

/// <summary>
/// Canli arac akisi (kullanici istegi 2026-09-20: developer calisirken ne yaptigi gorunsun). AgentCaller her tur icin tek
/// kullanimlik bir belirtec uretir ve runtime'a <c>progressUrl</c> ile verir; runtime her arac cagrisinda o adrese POST eder.
/// Belirtec yetkidir (JWT yok): tahmin edilemez, tur bitince duser. Api olayi <c>agent.tool</c> olarak sahneye yayimlar.
/// Runtime is kurali bilmez: yalniz "arac X hedef Y" der (CLAUDE.md §1).
/// </summary>
public sealed class ProgressRegistry(ISceneEventPublisher scene)
{
    private readonly ConcurrentDictionary<string, ProgressContext> _live = new(StringComparer.Ordinal);

    /// <summary>Api'nin runtime'a verecegi geri cagri koku (<c>http://127.0.0.1:5080/api/v1/progress</c>); Api acilista yazar. Bos = akis kapali.</summary>
    public string? BaseUrl { get; set; }

    public string Register(ProgressContext ctx)
    {
        var token = RandomNumberGenerator.GetHexString(32, lowercase: true);
        _live[token] = ctx;
        return token;
    }

    public void Release(string token) => _live.TryRemove(token, out _);

    /// <summary>Runtime bildirdi: baglam biliniyorsa sahneye <c>agent.tool</c> gider. Bilinmeyen belirtec sessizce yutulur (tur bitmis).</summary>
    public bool Report(string token, ProgressEvent e)
    {
        if (!_live.TryGetValue(token, out var ctx))
        {
            return false;
        }

        scene.Publish(SceneEventTypes.AgentTool, JsonSerializer.Serialize(new
        {
            agent = ctx.Agent,
            tool = e.Tool,
            target = Shorten(e.Target),
            run = ctx.RunId,
            task = ctx.Task,
            stage = ctx.Stage,
        }));
        return true;
    }

    /// <summary>
    /// Hedef: dosya yolunun son iki parcasi ya da komutun ilk 60 karakteri.
    /// Komutta bas taraftaki <c>cd "…" &amp;&amp;</c> on eki atilir (ajan her komutu proje koku ile baslatir; asil is ondan sonradir),
    /// satir sonlari tek bosluga iner.
    /// </summary>
    internal static string? Shorten(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        var t = CollapseWhitespace.Replace(target.Trim(), " ");
        if (t.Contains('\\') || t.Contains('/'))
        {
            var parts = t.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && !t.Contains(' '))
            {
                return string.Join('/', parts[^2..]);
            }
        }

        // `cd "<yol>" && ` / `cd <yol>; ` on ekleri (birden fazla olabilir) atilir.
        while (true)
        {
            var m = LeadingCd.Match(t);
            if (!m.Success || m.Length >= t.Length)
            {
                break;
            }

            t = t[m.Length..].TrimStart();
        }

        return t.Length <= 60 ? t : t[..59] + "…";
    }

    private static readonly System.Text.RegularExpressions.Regex CollapseWhitespace = new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex LeadingCd = new(@"^cd\s+(?:""[^""]*""|'[^']*'|\S+)\s*(?:&&|;)\s*", System.Text.RegularExpressions.RegexOptions.Compiled);
}
