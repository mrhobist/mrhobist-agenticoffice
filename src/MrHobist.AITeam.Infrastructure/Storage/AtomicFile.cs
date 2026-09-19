using System.Text;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary>Yazma atomiktir: gecici dosya + <c>File.Move(overwrite)</c>. Yarim dosya asla okunmaz (CLAUDE.md §2).</summary>
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

    /// <summary>
    /// Tek satir ekler ve diske iter. Calisma yarida kesilse bile o ana kadarki satirlar okunur kalir.
    /// </summary>
    public static async Task AppendLineAsync(string path, string line, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            var bytes = Utf8NoBom.GetBytes(line + "\n");
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>Satirlari okur; bozuk son satir (yarim yazim) yok sayilir, geri kalani kurtarilir.</summary>
    public static async Task<IReadOnlyList<T>> ReadJsonLinesAsync<T>(string path, Func<string, T?> parse, CancellationToken ct)
        where T : class
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, useAsync: true);
        var result = new List<T>();
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Utf8NoBom);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var item = parse(line);
                if (item is not null)
                {
                    result.Add(item);
                }
            }
        }

        return result;
    }
}
