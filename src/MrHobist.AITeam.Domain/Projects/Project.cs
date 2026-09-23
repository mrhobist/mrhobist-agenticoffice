namespace MrHobist.AITeam.Domain.Projects;

/// <summary>
/// Proje: islerin yasadigi kap (<c>data/aiteam.db</c> → <c>project</c> tablosu). Bir is yalniz bir projenin
/// icinde baslar (docs/DOMAIN.md → Projeler). <see cref="Workflow"/> projenin varsayilan akisi (is formunda
/// degistirilebilir); <see cref="TargetDir"/> developer'in dosya yazacagi dizin (depo kokune gore).
/// <see cref="OwnerId"/> giris hazirligidir: bugun sabit <c>local</c>, JWT gelince claim'den dolar.
///
/// Butce 2026-09-22'ye kadar YALNIZ is basinaydi (<c>Run.MaxCostUsd</c>); o tarihte kullanici karariyla proje
/// duzeyi eklendi. Ikisi birlikte calisir: is butcesi tek bir isi, proje butcesi projenin TOPLAMINI sinirlar.
/// </summary>
public sealed record Project(
    string Key,
    string Title,
    string Description,
    string Workflow,
    string TargetDir,
    string OwnerId,
    DateTimeOffset CreatedAt,
    /// <summary>Projenin rengi (<c>#rrggbb</c>): ray karti, Kanban "Tumu" kartlari. Bos → paletten sira ile atanir.</summary>
    string Color = "",
    /// <summary>Ray ve Kanban sirasi (kucuk once). Kullanici degistirir (<c>POST /projects/reorder</c>).</summary>
    int Order = 0,
    /// <summary>
    /// Projenin TOPLAM $ tavani; <c>null</c> = sinirsiz (varsayilan). Abonelikte ucret kesilmedigi icin bu
    /// ESDEGER maliyettir (CLAUDE.md §4). Asilinca yeni tur baslamaz; suren is <c>BudgetExceeded</c> olur.
    /// </summary>
    decimal? MaxCostUsd = null,
    /// <summary>
    /// Projenin TOPLAM token tavani (girdi + cikti); <c>null</c> = sinirsiz (varsayilan). Abonelikte asil
    /// tukenen kaynak budur, bu yuzden $'dan bagimsiz verilebilir. Ikisi de doluysa ONCE DOLAN durdurur.
    /// </summary>
    long? MaxTokens = null)
{
    public const string LocalOwner = "local";

    /// <summary>Palet: ofis kagit/ahsap tonlariyla uyumlu, birbirinden ayrisan 8 renk. Yeni proje kullanilmayan ilk rengi alir.</summary>
    public static readonly IReadOnlyList<string> Palette =
        ["#4fa3e0", "#7cc46b", "#e0699a", "#a889e6", "#f3c34a", "#e0995c", "#35b98a", "#d23b3b"];

    public static bool IsValidColor(string? c) => string.IsNullOrEmpty(c) || System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$");

    /// <summary>Kendi basina tutarli mi: anahtar, baslik, akis anahtari, hedef dizin (depo icinde, ust dizine cikmaz).</summary>
    public void Validate()
    {
        Identifiers.Require(Key, ErrorCodes.ProjectInvalidKey, "proje");
        if (string.IsNullOrWhiteSpace(Title))
        {
            throw new DomainException(ErrorCodes.ProjectTitleEmpty, $"{Key}: 'title' bos.");
        }

        Identifiers.Require(Workflow, ErrorCodes.WorkflowInvalidStage, "akis");
        if (!IsValidColor(Color))
        {
            throw new DomainException(ErrorCodes.ProjectInvalidColor, $"{Key}: 'color' #rrggbb olmali ('{Color}').");
        }

        // Butce: verilmediyse (null) sinirsiz. Verildiyse pozitif olmali -- 0 "sinirsiz" degil "hicbir is kosmasin"
        // demek olurdu ve kullanici bunu kazara yazdiginda proje sessizce kilitlenirdi.
        if (MaxCostUsd is { } cost && cost <= 0m)
        {
            throw new DomainException(ErrorCodes.ProjectBudgetInvalid, $"{Key}: 'maxCostUsd' pozitif olmali; sinirsiz icin bos birak ({cost}).");
        }

        if (MaxTokens is { } tokens && tokens <= 0)
        {
            throw new DomainException(ErrorCodes.ProjectBudgetInvalid, $"{Key}: 'maxTokens' pozitif olmali; sinirsiz icin bos birak ({tokens}).");
        }

        var dir = (TargetDir ?? "").Replace('\\', '/').Trim();
        if (dir.Length == 0 || dir.StartsWith('/') || dir.Contains("..", StringComparison.Ordinal) || dir.Contains(':', StringComparison.Ordinal))
        {
            throw new DomainException(ErrorCodes.ProjectTargetDirInvalid, $"{Key}: 'targetDir' depo icinde goreli bir yol olmali ('{TargetDir}').");
        }
    }

    /// <summary>Varsayilan hedef dizin: <c>projects/{key}</c>.</summary>
    public static string DefaultTargetDir(string key) => $"projects/{key}";
}
