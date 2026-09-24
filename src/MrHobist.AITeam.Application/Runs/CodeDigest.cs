using System.Text.RegularExpressions;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>Bu iste diske yazilmis bir dosya ve kodla cikarilmis imzalari. <see cref="Status"/>: <c>A</c> yeni · <c>M</c> degisti · <c>D</c> silindi.</summary>
public sealed record WrittenFile(string Path, char Status, int Added, int Deleted, IReadOnlyList<string> Signatures);

/// <summary>
/// Gorev basinda "bu iste simdiye kadar yazilan kod": calismanin ilk anlik goruntusu ile simdiki hal arasindaki dosya farki +
/// degisen kod dosyalarinin imzalari. <b>LLM CAGRILMAZ</b> (2026-09-24 kullanici karari: once kodla). Kaynak ajanin raporu degil,
/// golge depodaki gercek farktir: Bash ile yazilan dosya da girer, raporda unutulan dosya da.
///
/// Gerekce (olculdu 2026-09-24, 4 is / 14 gorev): ilk gorevden sonraki gorevlerdeki 57 Read'in 18'i onceki gorevin yazdigi
/// dosyaya, bunlarin 12'si duzenlenmeden -- yalniz ANLAMAK icin. Okunan dosya sonraki her ic turda baglamda yeniden odenir.
/// Imza bu okumayi keser; duzenlenecek dosya yine acilir (Edit once Read ister).
///
/// Ozet her ic turda yeniden okunur: <see cref="MaxChars"/>'ta kesilir (kod haritasiyla ayni tavan).
/// </summary>
public static partial class CodeDigest
{
    /// <summary>Istemdeki bolumun ust siniri (karakter).</summary>
    public const int MaxChars = 3000;

    /// <summary>Imzasi okunan en fazla dosya: her okuma bir git cagrisi.</summary>
    public const int MaxFilesRead = 20;

    public const int MaxSignaturesPerFile = 12;

    private const int MaxSignatureChars = 160;

    /// <summary>Bundan buyuk metin taranmaz (uretilmis ya da paketlenmis dosya).</summary>
    private const int MaxFileChars = 200_000;

    /// <summary>Farka giren dosyalar: kaynak ve metin yapilandirmasi. Ikili ve uretilmis dosya listeyi sisirmesin.</summary>
    public static readonly IReadOnlyList<string> Include =
    [
        "**/*.cs", "**/*.csproj", "**/*.props", "**/*.slnx", "**/*.sln", "**/*.razor", "**/*.cshtml",
        "**/*.ts", "**/*.tsx", "**/*.js", "**/*.jsx", "**/*.mjs", "**/*.cjs", "**/*.vue",
        "**/*.py", "**/*.sql", "**/*.json", "**/*.css", "**/*.scss", "**/*.html",
        "**/*.md", "**/*.yml", "**/*.yaml", "**/*.toml", "**/*.ps1", "**/*.cmd", "**/*.sh",
    ];

    /// <summary>Hic yuklenmeyenler: paket/derleme dizinleri ve kilit dosyalari (golge depo bunlari da izler).</summary>
    public static readonly IReadOnlyList<string> Exclude =
    [
        "**/node_modules/**", "**/bin/**", "**/obj/**", "**/.nuxt/**", "**/.output/**", "**/dist/**", "**/.venv/**",
        "**/__pycache__/**", "**/wwwroot/lib/**", "**/.vs/**", "**/.idea/**",
        "**/package-lock.json", "**/pnpm-lock.yaml", "**/yarn.lock", "**/*.min.js", "**/*.min.css",
    ];

    /// <summary>
    /// <paramref name="from"/> → <paramref name="to"/> farki; gorevin kendi dosyalari once, silinenler sonda. Imza yalniz ilk
    /// <see cref="MaxFilesRead"/> kod dosyasinda cikarilir. Git yoksa ya da fark alinamazsa bos.
    /// </summary>
    public static async Task<IReadOnlyList<WrittenFile>> CollectAsync(IWorkspaceSnapshot snapshots, string root, string from, string to, RunTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(task);
        var changes = await snapshots.DiffAsync(root, from, to, Include, Exclude, ct).ConfigureAwait(false);
        if (changes.Count == 0)
        {
            return [];
        }

        var own = new HashSet<string>(task.Files.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var ordered = changes
            .OrderBy(c => own.Contains(Normalize(c.Path)) ? 0 : 1)
            .ThenBy(c => c.Status == 'D' ? 1 : 0)
            .ThenBy(c => c.Path, StringComparer.Ordinal);

        var list = new List<WrittenFile>(changes.Count);
        var read = 0;
        foreach (var c in ordered)
        {
            IReadOnlyList<string> signatures = [];
            if (c.Status != 'D' && HasSignatures(c.Path) && read < MaxFilesRead)
            {
                read++;
                var text = await snapshots.ReadAsync(root, to, c.Path, ct).ConfigureAwait(false);
                if (text is { Length: > 0 and <= MaxFileChars })
                {
                    signatures = Signatures(c.Path, text);
                }
            }

            list.Add(new WrittenFile(c.Path, c.Status, c.Added, c.Deleted, signatures));
        }

        return list;
    }

    /// <summary>Imza cikarilan uzantilar. Digerleri yalniz yol ve satir sayisiyla listelenir.</summary>
    public static bool HasSignatures(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".vue" or ".py";

    /// <summary>
    /// Satir bazli, dile gore kaba imza cikarimi: govde atilir, tek satira iner. Amac derleyici dogrulugu degil "bu dosyada
    /// ne var" sorusuna dosyayi acmadan cevap: tur ve genel uye bildirimleri, HTTP uclari, disa aktarilanlar, Vue makrolari.
    /// </summary>
    public static IReadOnlyList<string> Signatures(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var result = new List<string>();
        var inClass = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var sig = ext switch
            {
                ".cs" => CSharp(line),
                ".py" => Python(line, ref inClass),
                ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".vue" => Script(line),
                _ => null,
            };

            if (sig is null)
            {
                continue;
            }

            sig = Whitespace().Replace(sig, " ").Trim();
            if (sig.Length > MaxSignatureChars)
            {
                sig = sig[..(MaxSignatureChars - 1)] + "…";
            }

            if (sig.Length > 0 && !result.Contains(sig, StringComparer.Ordinal))
            {
                result.Add(sig);
                if (result.Count == MaxSignaturesPerFile)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static string? CSharp(string line)
    {
        if (CsEndpoint().Match(line) is { Success: true } endpoint)
        {
            var value = endpoint.Value.Trim();
            return value.EndsWith('"') ? value + ")" : value; // app.MapGet("/x" → app.MapGet("/x"); oznitelik zaten kapali
        }

        if (CsType().IsMatch(line) || (CsMember().IsMatch(line) && (line.Contains('(', StringComparison.Ordinal) || line.Contains('{', StringComparison.Ordinal))))
        {
            return CutBody(line);
        }

        return null;
    }

    /// <summary>Girintili <c>def</c> yalniz bir sinifin icindeyse metottur; ust duzey fonksiyonun icindeyse yerel yardimcidir, alinmaz.</summary>
    private static string? Python(string line, ref bool inClass)
    {
        if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] is not '#' and not '@')
        {
            inClass = line.StartsWith("class ", StringComparison.Ordinal);
        }

        if (PyRoute().IsMatch(line))
        {
            return line.Trim();
        }

        return PyTopLevel().IsMatch(line) || (inClass && PyMethod().IsMatch(line)) ? line.Trim().TrimEnd(':') : null;
    }

    private static string? Script(string line)
        => ScriptExport().IsMatch(line) || VueMacro().IsMatch(line) ? CutBody(line) : null;

    /// <summary>
    /// Govdeyi atar: ilk <c>{</c> ya da <c>=&gt;</c>'dan sonrasi. Kesim bildirimi yarim birakacaksa (ör. <c>type A = {</c>,
    /// <c>defineProps&lt;{</c>, acik parantez: <c>defineEventHandler(async (e) =&gt; {</c>) satir oldugu gibi kalir.
    /// </summary>
    private static string CutBody(string line)
    {
        var cut = line.Length;
        var brace = line.IndexOf('{', StringComparison.Ordinal);
        var arrow = line.IndexOf("=>", StringComparison.Ordinal);
        if (brace >= 0)
        {
            cut = brace;
        }

        if (arrow >= 0 && arrow < cut)
        {
            cut = arrow;
        }

        var head = line[..cut].TrimEnd();
        var open = head.Count(c => c == '(') - head.Count(c => c == ')');
        return head.Length == 0 || head[^1] is '=' or '<' or ',' || open > 0 ? line.Trim() : head.Trim();
    }

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimStart('.', '/');

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // private/nested yardimcilar disarida: niteleyici listesinde private yok.
    [GeneratedRegex(@"^\s*(?:(?:public|internal|protected|static|sealed|abstract|partial|readonly|file|ref|unsafe|new)\s+)*(?:class|record(?:\s+(?:class|struct))?|struct|interface|enum)\s+\w")]
    private static partial Regex CsType();

    [GeneratedRegex(@"^\s*(?:public|internal|protected)\s+(?!(?:(?:static|sealed|abstract|partial|readonly|file|ref|unsafe|new)\s+)*(?:class|record|struct|interface|enum)\b)")]
    private static partial Regex CsMember();

    // Minimal API (app.MapGet("/x", ...)) ve denetleyici oznitelikleri ([HttpPost("x")], [Route("api/x")]).
    [GeneratedRegex(@"(?:\b\w+\.Map(?:Get|Post|Put|Delete|Patch|Group|Methods)\s*\(\s*""[^""]*""|\[(?:Http(?:Get|Post|Put|Delete|Patch)|Route)\s*\(\s*""[^""]*""\s*\)\])")]
    private static partial Regex CsEndpoint();

    [GeneratedRegex(@"^(?:(?:async\s+)?def\s+\w+|class\s+\w+)")]
    private static partial Regex PyTopLevel();

    [GeneratedRegex(@"^\s{4}(?:async\s+)?def\s+(?!_)\w+")]
    private static partial Regex PyMethod();

    [GeneratedRegex(@"^@\w+(?:\.\w+)*\.(?:get|post|put|delete|patch|route|api_route)\(")]
    private static partial Regex PyRoute();

    [GeneratedRegex(@"^\s*export\s+(?:default\b|(?:declare\s+)?(?:async\s+)?(?:function\*?|class|interface|type|const|let|var|enum|abstract\s+class)\b)")]
    private static partial Regex ScriptExport();

    [GeneratedRegex(@"\bdefine(?:Props|Emits|Model|Expose|PageMeta)\s*[<(]")]
    private static partial Regex VueMacro();
}
