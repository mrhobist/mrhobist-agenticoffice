using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using MrHobist.AITeam.Application.Abstractions;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>Suren LLM turunun sahibi: hangi calisma, ajan, gorev. Ilerleme bildirimi bu baglama yazilir.</summary>
public sealed record ProgressContext(string RunId, string Agent, string? Task, string? Stage);

/// <summary>
/// Runtime'in tur sirasinda bildirdigi tek olay (<c>POST /progress/{token}</c> govdesi). <see cref="Kind"/>: <c>tool</c>
/// (arac cagrisi; eski govdelerde bos) · <c>text</c> (ajanin yazdigi) · <c>thinking</c> (dusunce ozeti) · <c>usage</c> (bir API
/// mesajinin kullanimi; ayni <see cref="MessageId"/> icin son deger gecerli). Yeni alan SONA eklenir (CLAUDE.md §5).
/// Hepsi istege bagli: Api JSON'u zorunlu kurucu parametresine uyar (<c>RespectRequiredConstructorParameters</c>); runtime
/// bos alani hic gondermez, zorunlu olsaydi metin/kullanim govdesi 400 alirdi (2026-09-23'te boyle oldu).
/// <see cref="Chars"/>: o API mesajinda o ana kadar uretilen icerigin (metin, dusunce, arac girdisi) karakter sayisi.
/// Akistaki <c>outputTokens</c> mesajin BASINDAKI degerdir (olculdu: 2 → gercek 464); kesilen turun ciktisi bundan tahmin edilir.
/// </summary>
public sealed record ProgressEvent(string? Tool = null, string? Target = null, string? Kind = null, string? Text = null, string? MessageId = null, RuntimeUsage? Usage = null, int? Chars = null);

/// <summary>Ajanin o anki baglaminin bir parcasi (sistem istemi, bilgi dosyasi, gorev istemi, tasinan gecmis).</summary>
public sealed record LiveContextPart(string Name, string Role, int Chars, string Text);

/// <summary>Canli akisin tek satiri: arac, metin ya da dusunce.</summary>
public sealed record LiveEntry(DateTimeOffset Ts, string Kind, string? Tool, string? Target, string? Text);

/// <summary>
/// Suren bir turun anlik gorunumu (<c>GET /runs/{id}/live</c>): canlilik (son hareket), akis, o ana kadarki kullanim ve
/// ajanin elindeki baglam. Tur bitince duser; kalici kayit <c>run_turn</c>'dedir.
/// </summary>
public sealed record LiveTurnView(
    string Agent,
    string? Task,
    string? Stage,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeenAt,
    int ToolCount,
    RuntimeUsage Usage,
    IReadOnlyList<LiveEntry> Stream,
    IReadOnlyList<LiveContextPart> Context,
    int IdleLimitS);

/// <summary>
/// Canli akis (kullanici istekleri 2026-09-20 ve 2026-09-23: developer calisirken ne yaptigi, ne dusundugu, elindeki
/// baglam ve gercekten calisip calismadigi gorunsun). AgentCaller her tur icin tek kullanimlik bir belirtec uretir ve
/// runtime'a <c>progressUrl</c> ile verir; runtime her arac cagrisinda, metinde, dusuncede ve mesaj kullaniminda o adrese
/// POST eder. Belirtec yetkidir (JWT yok): tahmin edilemez, tur bitince duser. Arac olayi <c>agent.tool</c> olarak sahneye
/// yayimlanir. Kullanim da burada birikir: tur yarida kesilirse maliyeti buradan kurtarilir (CLAUDE.md §4).
/// Runtime is kurali bilmez: yalniz "su oldu" der (CLAUDE.md §1).
/// </summary>
public sealed class ProgressRegistry(ISceneEventPublisher scene)
{
    /// <summary>Tur basina akista tutulan en fazla satir (eski satirlar duser; tam metin tur sonunda gunlukte).</summary>
    public const int StreamCap = 400;

    /// <summary>Cikti tahmini: karakter / token. Kod ve Turkce metinde ~3-3,5; dusuk tutuldu ki kesilen tur eksik sayilmasin.</summary>
    public const double OutputCharsPerToken = 3.0;

    private sealed class Live(ProgressContext ctx, IReadOnlyList<LiveContextPart> context)
    {
        public ProgressContext Ctx { get; } = ctx;

        public IReadOnlyList<LiveContextPart> Context { get; } = context;

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

        public int ToolCount { get; set; }

        public Queue<LiveEntry> Stream { get; } = new();

        public Dictionary<string, (RuntimeUsage Usage, int Chars)> Usage { get; } = new(StringComparer.Ordinal);

        public object Gate { get; } = new();
    }

    private readonly ConcurrentDictionary<string, Live> _live = new(StringComparer.Ordinal);

    /// <summary>Api'nin runtime'a verecegi geri cagri koku (<c>http://127.0.0.1:5080/api/v1/progress</c>); Api acilista yazar. Bos = akis kapali.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Hareketsizlik esigi (saniye): UI "son hareket" gostergesini buna gore uyarir.</summary>
    public int IdleLimitS { get; set; } = (int)TurnWatch.Default.Idle.TotalSeconds;

    public string Register(ProgressContext ctx, IReadOnlyList<LiveContextPart>? context = null)
    {
        var token = RandomNumberGenerator.GetHexString(32, lowercase: true);
        _live[token] = new Live(ctx, context ?? []);
        return token;
    }

    public void Release(string token) => _live.TryRemove(token, out _);

    /// <summary>Turun son hareketi (kayit ani ya da son bildirim); belirtec yoksa (tur bitti) null. Hareketsizlik bekcisi buna bakar.</summary>
    public DateTimeOffset? LastSeen(string token) => _live.TryGetValue(token, out var l) ? l.LastSeenAt : null;

    /// <summary>O ana kadar bildirilen kullanim (mesaj basina son deger, toplanmis); bildirim yoksa null.</summary>
    public RuntimeUsage? UsageOf(string token)
    {
        if (!_live.TryGetValue(token, out var l))
        {
            return null;
        }

        lock (l.Gate)
        {
            return l.Usage.Count == 0 ? null : Sum(l.Usage.Values);
        }
    }

    /// <summary>Calismanin suren turlari (ayni anda birden cok ajan olabilir).</summary>
    public IReadOnlyList<LiveTurnView> Snapshot(string runId)
        => _live.Values.Where(l => l.Ctx.RunId == runId).Select(l =>
        {
            lock (l.Gate)
            {
                return new LiveTurnView(l.Ctx.Agent, l.Ctx.Task, l.Ctx.Stage, l.StartedAt, l.LastSeenAt, l.ToolCount,
                    Sum(l.Usage.Values), l.Stream.ToList(), l.Context, IdleLimitS);
            }
        }).OrderBy(v => v.StartedAt).ToList();

    /// <summary>Runtime bildirdi. Bilinmeyen belirtec sessizce yutulur (tur bitmis). Arac olayi sahneye <c>agent.tool</c> olarak gider.</summary>
    public bool Report(string token, ProgressEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!_live.TryGetValue(token, out var l))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var kind = string.IsNullOrEmpty(e.Kind) ? "tool" : e.Kind;
        lock (l.Gate)
        {
            l.LastSeenAt = now;
            switch (kind)
            {
                case "usage" when e.Usage is not null && !string.IsNullOrEmpty(e.MessageId):
                    l.Usage[e.MessageId] = (e.Usage, e.Chars ?? 0);
                    return true;
                case "text" or "thinking" when !string.IsNullOrWhiteSpace(e.Text):
                    Push(l, new LiveEntry(now, kind, null, null, e.Text));
                    return true;
                case "tool" when !string.IsNullOrEmpty(e.Tool):
                    l.ToolCount++;
                    Push(l, new LiveEntry(now, "tool", e.Tool, Shorten(e.Target), null));
                    break;
                default:
                    return true;
            }
        }

        scene.Publish(SceneEventTypes.AgentTool, JsonSerializer.Serialize(new
        {
            agent = l.Ctx.Agent,
            tool = e.Tool,
            target = Shorten(e.Target),
            run = l.Ctx.RunId,
            task = l.Ctx.Task,
            stage = l.Ctx.Stage,
        }));
        return true;
    }

    private static void Push(Live l, LiveEntry entry)
    {
        l.Stream.Enqueue(entry);
        while (l.Stream.Count > StreamCap)
        {
            l.Stream.Dequeue();
        }
    }

    /// <summary>Mesajlarin toplami; cikti mesaj basina bildirilen ile icerikten tahmin edilenin buyugudur.</summary>
    private static RuntimeUsage Sum(IEnumerable<(RuntimeUsage Usage, int Chars)> all)
    {
        int input = 0, output = 0, read = 0, write = 0, shortWrite = 0, peak = 0;
        foreach (var (u, chars) in all)
        {
            input += u.InputTokens;
            output += Math.Max(u.OutputTokens, (int)Math.Ceiling(chars / OutputCharsPerToken));
            read += u.CacheReadTokens;
            write += u.CacheWriteTokens;
            shortWrite += u.CacheWrite5mTokens;
            // Tek mesajin girdisi o anki baglamdir; tepe toplam degil en buyuk mesajdir.
            peak = Math.Max(peak, u.InputTokens);
        }

        return new RuntimeUsage(input, output, 0, read, write, shortWrite, peak);
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
