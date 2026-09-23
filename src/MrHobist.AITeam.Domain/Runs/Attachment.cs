namespace MrHobist.AITeam.Domain.Runs;

/// <summary>Ekin ajan tarafindan nasil okunacagi. JSON'da adiyla tasinir; yeni uye sona eklenir (CLAUDE.md §5).</summary>
public enum AttachmentKind
{
    /// <summary>PDF: Claude'un Read araci sayfa sayfa okur (metin + gorsel).</summary>
    Document,
    /// <summary>Resim (png, jpg, gif, webp): Read araci goruntu olarak okur.</summary>
    Image,
    /// <summary>Duz metin (md, txt, csv, json, xml, yaml, log...).</summary>
    Text,
    /// <summary>Word (docx): Read araci okuyamaz; Api yuklerken metnini cikarip yanina <c>.txt</c> yazar, ajan onu okur.</summary>
    Word,
}

/// <summary>
/// Is verilirken brief'e eklenen dosya (docs/DOMAIN.md → Ekler). Dosya <c>data/attachments/{runId}/{FileName}</c> altindadir;
/// icerik isteme GOMULMEZ (her turda yeniden odenirdi): ajan yolunu gorur ve gerekirse Read ile okur.
/// <see cref="TextFile"/> dolu ise ajana o dosya gosterilir (Word'un cikarilmis metni).
/// </summary>
public sealed record RunAttachment(
    string Id,
    string Name,
    string FileName,
    string MediaType,
    long Size,
    AttachmentKind Kind,
    string? TextFile = null);

/// <summary>Ek kurallari: izinli turler, boyut ve adet siniri. Dosya I/O yok; Infrastructure dosyayi yazar, kural buradan.</summary>
public static class AttachmentRules
{
    /// <summary>Tek dosya ust siniri. Claude'un PDF okuma siniri ~32 MB / 100 sayfa; resimde ~5 MB'tan buyugu kucultulur.</summary>
    public const long MaxBytes = 20L * 1024 * 1024;

    /// <summary>Bir iste en fazla ek.</summary>
    public const int MaxPerRun = 10;

    private static readonly Dictionary<string, (AttachmentKind Kind, string MediaType)> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = (AttachmentKind.Document, "application/pdf"),
        [".png"] = (AttachmentKind.Image, "image/png"),
        [".jpg"] = (AttachmentKind.Image, "image/jpeg"),
        [".jpeg"] = (AttachmentKind.Image, "image/jpeg"),
        [".gif"] = (AttachmentKind.Image, "image/gif"),
        [".webp"] = (AttachmentKind.Image, "image/webp"),
        [".docx"] = (AttachmentKind.Word, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        [".txt"] = (AttachmentKind.Text, "text/plain"),
        [".md"] = (AttachmentKind.Text, "text/markdown"),
        [".csv"] = (AttachmentKind.Text, "text/csv"),
        [".json"] = (AttachmentKind.Text, "application/json"),
        [".xml"] = (AttachmentKind.Text, "application/xml"),
        [".yaml"] = (AttachmentKind.Text, "application/yaml"),
        [".yml"] = (AttachmentKind.Text, "application/yaml"),
        [".log"] = (AttachmentKind.Text, "text/plain"),
        [".html"] = (AttachmentKind.Text, "text/html"),
        [".sql"] = (AttachmentKind.Text, "text/plain"),
    };

    /// <summary>UI'in dosya secicisi icin (<c>accept</c>): izinli uzantilar, sirali.</summary>
    public static IReadOnlyList<string> Extensions => Types.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>Uzantidan tur ve ortam tipi; bilinmeyen tur, bos ya da buyuk dosya <see cref="DomainException"/>.</summary>
    public static (AttachmentKind Kind, string MediaType) Check(string fileName, long size)
    {
        var ext = Path.GetExtension(fileName ?? "");
        if (!Types.TryGetValue(ext, out var type))
        {
            throw new DomainException(ErrorCodes.AttachmentTypeUnsupported, $"'{fileName}': desteklenmeyen tur. Izinli: {string.Join(", ", Extensions)}.");
        }

        if (size <= 0)
        {
            throw new DomainException(ErrorCodes.AttachmentEmpty, $"'{fileName}': dosya bos.");
        }

        if (size > MaxBytes)
        {
            throw new DomainException(ErrorCodes.AttachmentTooLarge, $"'{fileName}': {size / 1024 / 1024} MB; ust sinir {MaxBytes / 1024 / 1024} MB.");
        }

        return type;
    }

    /// <summary>
    /// Diske yazilacak guvenli ad: yol ayiraclari ve denetim karakterleri atilir, uzunluk sinirlanir, uzanti korunur.
    /// Ayni adli ikinci ek <paramref name="taken"/>'a gore <c>ad-2.pdf</c> olur.
    /// </summary>
    public static string SafeFileName(string name, ISet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var raw = Path.GetFileName((name ?? "").Replace('\\', '/').Split('/')[^1]);
        var ext = Path.GetExtension(raw).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(raw);
        var invalid = Path.GetInvalidFileNameChars().Concat(['"', '\'', '`', '$', '%', '&', ';', '|', '<', '>']).ToHashSet();
        var clean = new string(stem.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (clean.Length == 0)
        {
            clean = "ek";
        }

        if (clean.Length > 80)
        {
            clean = clean[..80];
        }

        var candidate = clean + ext;
        for (var i = 2; taken.Contains(candidate); i++)
        {
            candidate = $"{clean}-{i}{ext}";
        }

        taken.Add(candidate);
        return candidate;
    }
}
