using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary>
/// <c>data/attachments/</c>: <c>_staging/{id}{uzanti}</c> + <c>{id}.json</c> (ozgun ad) yuklemede; is olusunca
/// <c>{runId}/{guvenli-ad}</c>. Veritabani yalniz ustveriyi tutar (<c>run.attachments</c>), dosyanin kendisi burada.
/// Kimlikler sunucuda uretilir (hex), dosya adina donusen kullanici girdisi yalniz <see cref="AttachmentRules.SafeFileName"/>'den gecer.
/// </summary>
public sealed partial class FileAttachmentStore(StoragePaths paths) : IAttachmentStore
{
    private static readonly TimeSpan StagingTtl = TimeSpan.FromDays(1);

    private sealed record StagedMeta(string Name, string MediaType, long Size, AttachmentKind Kind);

    private string Root => Path.Combine(paths.DataRoot, "attachments");

    private string Staging => Path.Combine(Root, "_staging");

    [GeneratedRegex("^[a-f0-9]{32}$")]
    private static partial Regex StagedId();

    [GeneratedRegex("^[a-zA-Z0-9-]+$")]
    private static partial Regex SafeRunId();

    public async Task<StagedAttachment> StageAsync(string fileName, long size, Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var (kind, mediaType) = AttachmentRules.Check(fileName, size);
        Directory.CreateDirectory(Staging);
        SweepStaging();

        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var target = Path.Combine(Staging, id + Path.GetExtension(fileName).ToLowerInvariant());
        long written;
        await using (var file = File.Create(target))
        {
            // Bildirilen boyuta guvenilmez: sinir yazarken de denetlenir (govde bildirilenden buyukse yarim dosya kalmaz).
            var buffer = new byte[81920];
            written = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                written += read;
                if (written > AttachmentRules.MaxBytes)
                {
                    break;
                }

                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }

        if (written > AttachmentRules.MaxBytes || written == 0)
        {
            File.Delete(target);
            AttachmentRules.Check(fileName, written);
        }

        var name = Path.GetFileName(fileName.Replace('\\', '/').Split('/')[^1]);
        var meta = new StagedMeta(name, mediaType, written, kind);
        await File.WriteAllTextAsync(Path.Combine(Staging, id + ".json"), JsonSerializer.Serialize(meta), ct).ConfigureAwait(false);
        return new StagedAttachment(id, name, mediaType, written, kind);
    }

    public async Task<IReadOnlyList<RunAttachment>> ClaimAsync(string runId, IReadOnlyList<string> stagedIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stagedIds);
        if (stagedIds.Count == 0)
        {
            return [];
        }

        var ids = stagedIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count > AttachmentRules.MaxPerRun)
        {
            throw new DomainException(ErrorCodes.AttachmentTooMany, $"En fazla {AttachmentRules.MaxPerRun} ek verilebilir ({ids.Count}).");
        }

        // Once hepsi denetlenir: biri eksikse hicbiri tasinmaz.
        var staged = new List<(string Id, string File, StagedMeta Meta)>();
        foreach (var id in ids)
        {
            var metaFile = StagedId().IsMatch(id) ? Path.Combine(Staging, id + ".json") : null;
            if (metaFile is null || !File.Exists(metaFile))
            {
                throw new DomainException(ErrorCodes.AttachmentNotFound, $"Ek bulunamadi ya da suresi doldu: '{id}'. Dosyayi yeniden yukleyin.");
            }

            var meta = JsonSerializer.Deserialize<StagedMeta>(await File.ReadAllTextAsync(metaFile, ct).ConfigureAwait(false))
                ?? throw new DomainException(ErrorCodes.AttachmentNotFound, $"Ek ustverisi bozuk: '{id}'.");
            var file = Directory.EnumerateFiles(Staging, id + ".*").FirstOrDefault(f => !f.EndsWith(".json", StringComparison.Ordinal))
                ?? throw new DomainException(ErrorCodes.AttachmentNotFound, $"Ek dosyasi yok: '{id}'.");
            staged.Add((id, file, meta));
        }

        var dir = DirectoryOf(runId);
        Directory.CreateDirectory(dir);
        var taken = new HashSet<string>(Directory.EnumerateFiles(dir).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
        var result = new List<RunAttachment>();
        foreach (var (id, file, meta) in staged)
        {
            var safe = AttachmentRules.SafeFileName(meta.Name, taken);
            File.Move(file, Path.Combine(dir, safe));
            File.Delete(Path.Combine(Staging, id + ".json"));

            string? textFile = null;
            if (meta.Kind == AttachmentKind.Word && ExtractDocxText(Path.Combine(dir, safe)) is { Length: > 0 } text)
            {
                textFile = AttachmentRules.SafeFileName(Path.GetFileNameWithoutExtension(safe) + ".docx.txt", taken);
                await File.WriteAllTextAsync(Path.Combine(dir, textFile), text, Encoding.UTF8, ct).ConfigureAwait(false);
            }

            result.Add(new RunAttachment(id, meta.Name, safe, meta.MediaType, meta.Size, meta.Kind, textFile));
        }

        return result;
    }

    public string DirectoryOf(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId) || !SafeRunId().IsMatch(runId))
        {
            throw new DomainException(ErrorCodes.RunNotFound, $"Gecersiz calisma kimligi: '{runId}'.");
        }

        return Path.Combine(Root, runId);
    }

    public string? PathOf(string runId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) || fileName.StartsWith('.'))
        {
            return null;
        }

        var dir = DirectoryOf(runId);
        var path = Path.Combine(dir, fileName);
        return File.Exists(path) && Path.GetDirectoryName(Path.GetFullPath(path)) == Path.GetFullPath(dir) ? path : null;
    }

    public void DeleteRun(string runId)
    {
        var dir = DirectoryOf(runId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Bir gunden eski gecici yuklemeler (is hic olusmadi) silinir. Hata yutulur: temizlik yuklemeyi engellemez.</summary>
    private void SweepStaging()
    {
        var cutoff = DateTime.UtcNow - StagingTtl;
        foreach (var f in Directory.EnumerateFiles(Staging))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(f) < cutoff)
                {
                    File.Delete(f);
                }
            }
            catch (IOException)
            {
                // kilitli dosya sonraki yuklemede silinir
            }
            catch (UnauthorizedAccessException)
            {
                // ayni
            }
        }
    }

    /// <summary>
    /// Word metni: <c>word/document.xml</c> icindeki paragraflar (<c>w:p</c>) satir, metin parcalari (<c>w:t</c>) birlesir, sekme/satir sonu korunur.
    /// Tablo hucreleri sekmeyle ayrilir. Bicim kaybolur; amac icerigi ajana okutmak. Bozuk dosya → null (ek yine verilir, metin yok).
    /// </summary>
    internal static string? ExtractDocxText(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml");
            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var sb = new StringBuilder();
            var inText = false;
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element when reader.LocalName == "t":
                        inText = !reader.IsEmptyElement;
                        break;
                    case XmlNodeType.Element when reader.LocalName == "tab":
                        sb.Append('\t');
                        break;
                    case XmlNodeType.Element when reader.LocalName is "br" or "cr":
                        sb.Append('\n');
                        break;
                    case XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace when inText:
                        sb.Append(reader.Value);
                        break;
                    case XmlNodeType.EndElement when reader.LocalName == "t":
                        inText = false;
                        break;
                    case XmlNodeType.EndElement when reader.LocalName == "p":
                        sb.Append('\n');
                        break;
                    case XmlNodeType.EndElement when reader.LocalName == "tc":
                        sb.Append('\t');
                        break;
                    default:
                        break;
                }
            }

            return sb.ToString().Trim();
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException)
        {
            return null;
        }
    }
}
