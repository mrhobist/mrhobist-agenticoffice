using System.Text;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary>
/// <c>config/</c> yazimi atomiktir: gecici dosya + <c>File.Move(overwrite)</c>. Yarim dosya asla okunmaz (CLAUDE.md §2).
/// Calisma zamani durumu artik veritabaninda; JSONL append/okuma yardimcilari onunla birlikte kaldirildi.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteAsync(string path, string content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, content, Utf8NoBom, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
