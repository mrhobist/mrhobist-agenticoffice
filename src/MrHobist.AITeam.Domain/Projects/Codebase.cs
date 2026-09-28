using System.Text.Json;
using System.Text.RegularExpressions;

namespace MrHobist.AITeam.Domain.Projects;

/// <summary>Taranan bir dosya: proje kokune gore yol (ileri bolu) ve bayt boyu.</summary>
public readonly record struct SourceFile(string Path, long Bytes);

/// <summary>Bir dilin projedeki payi (GitHub'daki dil seridi gibi): ad, linguist rengi, bayt ve yuzde.</summary>
public sealed record LanguageShare(string Name, string Color, long Bytes, double Percent);

/// <summary>
/// Proje dizininde zaten ne var (kullanici istegi 2026-09-26: "hep sifir proje dusunduk; baslamis projeyi iceri alip
/// devam ettirebilmeliyiz"). Saf hesap: dosya listesi Infrastructure'dan gelir, burada yalniz siniflanir.
///
/// Dil olcusu GitHub'in linguist'ini izler -- birebir degil, ozu: pay BAYTLA olculur; yalniz programlama ve isaretleme
/// dilleri sayilir (JSON/YAML/Markdown gibi veri ve duzyazi seride girmez); paket, derleme ciktisi, uretilmis ve
/// kucultulmus dosyalar ile belge klasorleri elenir. Uzanti catismasinda (<c>.h</c>) linguist gibi komsulara bakilir.
/// Renkler linguist'in <c>languages.yml</c>'inden: kullanici GitHub'da gordugu rengi gorsun.
/// </summary>
public sealed partial record Codebase(int Files, long Bytes, IReadOnlyList<LanguageShare> Languages, IReadOnlyList<string> Manifests)
{
    public static readonly Codebase Empty = new(0, 0, [], []);

    /// <summary>Dizinde sayilan kod yok (bos klasor ya da yalniz belge/veri): "sifirdan" is.</summary>
    public bool IsEmpty => Languages.Count == 0 && Manifests.Count == 0;

    private sealed record Lang(string Name, string Color);

    private static readonly Lang CSharp = new("C#", "#178600"), Cpp = new("C++", "#f34b7d"), C = new("C", "#555555"), ObjC = new("Objective-C", "#438eff");

    /// <summary>Uzanti (kucuk harf, noktali) → dil. Listede olmayan uzanti sayilmaz (veri, duzyazi, ikili).</summary>
    private static readonly Dictionary<string, Lang> ByExtension = Build(
        (CSharp, [".cs", ".csx"]),
        (new("TypeScript", "#3178c6"), [".ts", ".tsx", ".mts", ".cts"]),
        (new("JavaScript", "#f1e05a"), [".js", ".jsx", ".mjs", ".cjs"]),
        (new("Vue", "#41b883"), [".vue"]),
        (new("Svelte", "#ff3e00"), [".svelte"]),
        (new("Astro", "#ff5a03"), [".astro"]),
        (new("HTML", "#e34c26"), [".html", ".htm", ".xhtml"]),
        (new("HTML+Razor", "#512be4"), [".cshtml", ".razor"]),
        (new("CSS", "#663399"), [".css"]),
        (new("SCSS", "#c6538c"), [".scss"]),
        (new("Sass", "#a53b70"), [".sass"]),
        (new("Less", "#1d365d"), [".less"]),
        (new("Stylus", "#ff6347"), [".styl"]),
        (new("Python", "#3572A5"), [".py", ".pyw", ".pyi"]),
        (new("Jupyter Notebook", "#DA5B0B"), [".ipynb"]),
        (new("Java", "#b07219"), [".java"]),
        (new("Kotlin", "#A97BFF"), [".kt", ".kts"]),
        (new("Scala", "#c22d40"), [".scala", ".sc"]),
        (new("Groovy", "#4298b8"), [".groovy", ".gradle"]),
        (new("Go", "#00ADD8"), [".go"]),
        (new("Rust", "#dea584"), [".rs"]),
        (C, [".c"]),
        (Cpp, [".cpp", ".cc", ".cxx", ".c++", ".hpp", ".hh", ".hxx", ".h++", ".ino"]),
        (ObjC, [".m"]),
        (new("Objective-C++", "#6866fb"), [".mm"]),
        (new("Swift", "#F05138"), [".swift"]),
        (new("Dart", "#00B4AB"), [".dart"]),
        (new("PHP", "#4F5D95"), [".php", ".phtml"]),
        (new("Ruby", "#701516"), [".rb", ".rake", ".gemspec"]),
        (new("Perl", "#0298c3"), [".pl", ".pm"]),
        (new("Lua", "#000080"), [".lua"]),
        (new("Shell", "#89e051"), [".sh", ".bash", ".zsh"]),
        (new("PowerShell", "#012456"), [".ps1", ".psm1", ".psd1"]),
        (new("Batchfile", "#C1F12E"), [".bat", ".cmd"]),
        (new("TSQL", "#e38c00"), [".sql"]),
        (new("F#", "#b845fc"), [".fs", ".fsi", ".fsx"]),
        (new("Visual Basic .NET", "#945db7"), [".vb"]),
        (new("Elixir", "#6e4a7e"), [".ex", ".exs"]),
        (new("Erlang", "#B83998"), [".erl", ".hrl"]),
        (new("Haskell", "#5e5086"), [".hs"]),
        (new("Clojure", "#db5855"), [".clj", ".cljs", ".cljc"]),
        (new("R", "#198CE7"), [".r"]),
        (new("Julia", "#a270ba"), [".jl"]),
        (new("Zig", "#ec915c"), [".zig"]),
        (new("Solidity", "#AA6746"), [".sol"]),
        (new("GDScript", "#355570"), [".gd"]),
        (new("HCL", "#844FBA"), [".tf", ".hcl"]),
        (new("Nix", "#7e7eff"), [".nix"]),
        (new("Assembly", "#6E4C13"), [".asm", ".s"]),
        (new("GLSL", "#5686a5"), [".glsl", ".vert", ".frag"]),
        (new("HLSL", "#aace60"), [".hlsl", ".fx"]),
        (new("CMake", "#DA3434"), [".cmake"]),
        (new("Pug", "#a86454"), [".pug"]),
        (new("Handlebars", "#f7931e"), [".hbs", ".handlebars"]));

    /// <summary>Uzantisiz ya da adi belirleyici dosyalar (linguist <c>filenames</c>).</summary>
    private static readonly Dictionary<string, Lang> ByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = new("Dockerfile", "#384d54"),
        ["Containerfile"] = new("Dockerfile", "#384d54"),
        ["Makefile"] = new("Makefile", "#427819"),
        ["GNUmakefile"] = new("Makefile", "#427819"),
        ["CMakeLists.txt"] = new("CMake", "#DA3434"),
        ["Rakefile"] = new("Ruby", "#701516"),
        ["Gemfile"] = new("Ruby", "#701516"),
        ["Vagrantfile"] = new("Ruby", "#701516"),
        ["Jenkinsfile"] = new("Groovy", "#4298b8"),
    };

    /// <summary>
    /// Ucuncu taraf klasorleri (her derinlikte, ada gore; linguist <c>vendor.yml</c> ozu): git'te izleniyor olsalar bile
    /// projenin kodu degildir, dil olcusune girmez.
    /// </summary>
    public static readonly IReadOnlySet<string> VendoredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bower_components", "jspm_packages", "vendor", "vendors", "third_party",
        "third-party", "thirdparty", "Pods", "Carthage", "site-packages",
    };

    /// <summary>
    /// Git deposu OLMAYAN klasoru yururken budananlar: ucuncu taraf + derleme ciktisi, IDE durumu, sanal ortam. Git deposunda
    /// dosya listesi <c>git ls-files</c>'tan gelir ve bunlari zaten <c>.gitignore</c> eler (GitHub da izlenen dosyayi sayar);
    /// git olmayan klasorde tek sure koruyucusu budur (node_modules on binlerce dosya).
    /// </summary>
    public static readonly IReadOnlySet<string> SkippedDirs = new HashSet<string>(VendoredDirs.Concat(
    [
        ".vs", ".idea", ".vscode", ".gradle", ".cache", ".next", ".nuxt", ".output", ".svelte-kit", ".angular", ".turbo",
        ".parcel-cache", ".venv", "venv", "__pycache__", ".pytest_cache", ".mypy_cache", ".tox", "DerivedData",
        "bin", "obj", "dist", "build", "out", "target", "coverage", "TestResults",
    ]), StringComparer.OrdinalIgnoreCase);

    /// <summary>Linguist'in "documentation" kurali: kokteki docs/doc ve her derinlikte documentation/examples sayilmaz.</summary>
    [GeneratedRegex(@"^(docs?|documentation|examples?|samples?)/|/(documentation|examples?)/", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentationPath();

    /// <summary>Kucultulmus, paketlenmis, uretilmis ya da bilinen ucuncu taraf dosyalar (linguist vendor.yml + generated.rb ozu).</summary>
    [GeneratedRegex(@"(\.min\.(js|css)|-min\.js|\.bundle\.js|\.designer\.cs|\.g\.cs|\.g\.i\.cs|\.generated\.\w+|\.pb\.go|_pb2\.py)$|(^|/)(jquery[^/]*\.js|bootstrap[^/]*\.(js|css))$|(^|/)wwwroot/lib/|(^|/)Migrations/[^/]*\.Designer\.cs$", RegexOptions.IgnoreCase)]
    private static partial Regex VendoredFile();

    /// <summary>Derleme/paket dosyalari: ajan ve kullanici "bu proje ne ile kuruluyor"u buradan okur. Kok ve en cok iki alt klasor (src/Api/Api.csproj gibi).</summary>
    [GeneratedRegex(@"^(package\.json|[^/]+\.slnx?|[^/]+\.(cs|fs|vb)proj|pyproject\.toml|requirements\.txt|setup\.py|Pipfile|go\.mod|Cargo\.toml|pom\.xml|build\.gradle(\.kts)?|settings\.gradle(\.kts)?|composer\.json|Gemfile|pubspec\.yaml|CMakeLists\.txt|Makefile|Dockerfile|docker-compose\.ya?ml|compose\.ya?ml|nuxt\.config\.[jt]s|vite\.config\.[mc]?[jt]s|next\.config\.[mc]?[jt]s|angular\.json|deno\.jsonc?|global\.json|run\.cmd)$", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestName();

    /// <summary>Daha derindeki manifest gosterilmez; liste bu kadarla sinirli (monorepoda yuzlerce package.json olabilir).</summary>
    private const int MaxManifests = 12;

    /// <summary>Ucuncu taraf, belge ya da uretilmis/kucultulmus dosya mi: dil olcusune girmez.</summary>
    public static bool IsExcluded(string path)
    {
        var p = Normalize(path);
        return p.Split('/').SkipLast(1).Any(VendoredDirs.Contains) || DocumentationPath().IsMatch(p) || VendoredFile().IsMatch(p);
    }

    /// <summary>Dosya listesinden dil paylari ve manifestler. Bayt payi, buyukten kucuge; yuzde bir ondalik.</summary>
    public static Codebase Measure(IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var bytes = new Dictionary<Lang, long>();
        long headerBytes = 0;
        var count = 0;
        var manifests = new List<string>();
        foreach (var f in files)
        {
            var path = Normalize(f.Path);
            if (path.Length == 0 || IsExcluded(path))
            {
                continue;
            }

            count++;
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (path.Count(c => c == '/') <= 2 && ManifestName().IsMatch(name))
            {
                manifests.Add(path);
            }

            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext == ".h")
            {
                headerBytes += f.Bytes; // C mi C++ mi Objective-C mi: komsulara bakilarak sonda karar verilir
                continue;
            }

            var lang = ByFileName.GetValueOrDefault(name) ?? (name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase) ? ByFileName["Dockerfile"] : ByExtension.GetValueOrDefault(ext));
            if (lang is not null)
            {
                bytes[lang] = bytes.GetValueOrDefault(lang) + f.Bytes;
            }
        }

        if (headerBytes > 0)
        {
            // Linguist sezgisi, sade hali: .cpp agir basiyorsa C++, .m varsa Objective-C, yoksa C.
            var owner = bytes.GetValueOrDefault(Cpp) > bytes.GetValueOrDefault(C) ? Cpp : bytes.ContainsKey(ObjC) ? ObjC : C;
            bytes[owner] = bytes.GetValueOrDefault(owner) + headerBytes;
        }

        var total = bytes.Values.Sum();
        var shares = bytes.Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Name, StringComparer.Ordinal)
            .Select(kv => new LanguageShare(kv.Key.Name, kv.Key.Color, kv.Value, Math.Round(kv.Value * 100.0 / total, 1)))
            .ToList();
        var shown = manifests.OrderBy(m => m.Count(c => c == '/')).ThenBy(m => m, StringComparer.OrdinalIgnoreCase).Take(MaxManifests).ToList();
        return new Codebase(count, total, shares, shown);
    }

    /// <summary>Istemde ve kartta tek satir: <c>C# %62,1 · TypeScript %30</c>. Kalan paylar "diger" olarak toplanir.</summary>
    public string LanguageLine(int top = 5)
    {
        var head = Languages.Take(top).Select(l => $"{l.Name} %{Pct(l.Percent)}");
        var rest = Math.Round(Languages.Skip(top).Sum(l => l.Percent), 1);
        return string.Join(" · ", rest > 0 ? head.Append($"diğer %{Pct(rest)}") : head);
    }

    /// <summary>Turkce ondalik virgul. Kultur nesnesi kullanilmaz: derleme <c>InvariantGlobalization</c> ile, tr-TR olusturulamaz.</summary>
    private static string Pct(double v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');

    private static string Normalize(string path) => (path ?? "").Replace('\\', '/').TrimStart('.', '/');

    private static Dictionary<string, Lang> Build(params (Lang Lang, string[] Exts)[] rows)
    {
        var map = new Dictionary<string, Lang>(StringComparer.OrdinalIgnoreCase);
        foreach (var (lang, exts) in rows)
        {
            foreach (var e in exts)
            {
                map[e] = lang;
            }
        }

        return map;
    }
}

/// <summary>
/// Iceri alinan klasorden proje alanlari onerisi: anahtar, baslik, aciklama. Kaynak sirasi: <c>package.json</c>
/// (<c>name</c>/<c>description</c>), README'nin ilk basligi ve ilk paragrafi, en son klasor adi. Kullanici formda degistirir.
/// </summary>
public static partial class ProjectSuggestion
{
    public const int MaxDescription = 300;

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonKey();

    /// <summary>Metinden gecerli anahtar (<see cref="Identifiers.IsValidKey"/>): kucuk harf, Turkce harf sadelesir, en cok 40.</summary>
    public static string Key(string text)
    {
        var folded = (text ?? "").Replace('ı', 'i').Replace('İ', 'i').ToLowerInvariant()
            .Replace('ç', 'c').Replace('ğ', 'g').Replace('ö', 'o').Replace('ş', 's').Replace('ü', 'u');
        var key = NonKey().Replace(folded, "-").Trim('-');
        key = key.Length > 40 ? key[..40].TrimEnd('-') : key;
        return Identifiers.IsValidKey(key) ? key : "proje";
    }

    /// <summary>Baslik ve aciklama; <paramref name="packageJson"/> ve <paramref name="readme"/> dosyanin basi (yoksa null).</summary>
    public static (string Title, string Description) From(string folderName, string? packageJson, string? readme)
    {
        string? pkgName = null, pkgDesc = null;
        if (!string.IsNullOrWhiteSpace(packageJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(packageJson, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    pkgName = doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    pkgDesc = doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                }
            }
            catch (JsonException)
            {
                // Bozuk package.json oneriyi bozmaz: README ve klasor adi kalir.
            }
        }

        var (heading, paragraph) = ReadmeParts(readme);
        var title = FirstNonBlank(heading, Humanize(pkgName), Humanize(folderName)) ?? "Proje";
        var description = FirstNonBlank(pkgDesc, paragraph) ?? "";
        return (Clip(title, 80), Clip(description, MaxDescription));
    }

    /// <summary>README'nin ilk <c>#</c> basligi ve ondan sonraki ilk duzyazi paragrafi (rozet, resim, HTML satirlari atlanir).</summary>
    private static (string? Heading, string? Paragraph) ReadmeParts(string? readme)
    {
        if (string.IsNullOrWhiteSpace(readme))
        {
            return (null, null);
        }

        string? heading = null;
        var para = new List<string>();
        foreach (var raw in readme.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (heading is null && line.StartsWith("# ", StringComparison.Ordinal))
            {
                heading = line[2..].Trim();
                continue;
            }

            if (line.Length == 0)
            {
                if (para.Count > 0)
                {
                    break;
                }

                continue;
            }

            // Baslik, rozet/resim, HTML, tablo, kod citi, liste: aciklama paragrafi degil.
            if (line[0] is '#' or '!' or '<' or '|' or '`' or '[' or '>' or '-' or '*' or '=')
            {
                if (para.Count > 0)
                {
                    break;
                }

                continue;
            }

            para.Add(line);
        }

        return (heading, para.Count > 0 ? string.Join(' ', para) : null);
    }

    /// <summary><c>my-cool_app</c> → <c>My cool app</c>; kapsamli paket adindaki <c>@scope/</c> atilir.</summary>
    private static string? Humanize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var n = name.Contains('/', StringComparison.Ordinal) ? name[(name.LastIndexOf('/') + 1)..] : name;
        n = n.Replace('-', ' ').Replace('_', ' ').Trim();
        return n.Length == 0 ? null : char.ToUpperInvariant(n[0]) + n[1..];
    }

    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
}
