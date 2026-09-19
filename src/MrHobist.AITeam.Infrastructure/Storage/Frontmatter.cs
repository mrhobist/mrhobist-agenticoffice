using System.Text;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary>
/// Ajan ve bilgi md'lerinin YAML frontmatter'i. Kullanilan alt kume: <c>anahtar: deger</c>,
/// <c>anahtar: [a, b]</c>, tirnakli dizeler. Baska bir sey yazilmaz, yazilmayan okunmaz;
/// bagimlilik eklemektense sozlesmeyi dar tutmak tercih edildi.
/// </summary>
public static class Frontmatter
{
    public sealed record Document(IReadOnlyDictionary<string, object> Meta, string Body);

    public static Document Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return new Document(new Dictionary<string, object>(StringComparer.Ordinal), normalized.Trim());
        }

        var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return new Document(new Dictionary<string, object>(StringComparer.Ordinal), normalized.Trim());
        }

        var head = normalized[4..end];
        var bodyStart = normalized.IndexOf('\n', end + 1);
        var body = bodyStart < 0 ? "" : normalized[(bodyStart + 1)..];

        var meta = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var raw in head.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            meta[key] = ParseValue(value);
        }

        return new Document(meta, body.Trim());
    }

    public static string Render(IEnumerable<KeyValuePair<string, object?>> meta, string body)
    {
        ArgumentNullException.ThrowIfNull(meta);
        var sb = new StringBuilder("---\n");
        foreach (var (key, value) in meta)
        {
            if (value is null)
            {
                continue;
            }

            sb.Append(key).Append(": ").Append(RenderValue(value)).Append('\n');
        }

        sb.Append("---\n\n").Append((body ?? "").Trim()).Append('\n');
        return sb.ToString();
    }

    public static string? GetString(IReadOnlyDictionary<string, object> meta, string key)
    {
        ArgumentNullException.ThrowIfNull(meta);
        return meta.TryGetValue(key, out var v) ? v switch { string s => s, IReadOnlyList<string> l => string.Join(", ", l), _ => null } : null;
    }

    public static IReadOnlyList<string> GetList(IReadOnlyDictionary<string, object> meta, string key)
    {
        ArgumentNullException.ThrowIfNull(meta);
        if (!meta.TryGetValue(key, out var v))
        {
            return [];
        }

        return v switch
        {
            IReadOnlyList<string> l => l,
            string s when s.Length > 0 => [s],
            _ => [],
        };
    }

    private static object ParseValue(string value)
    {
        if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
        {
            var inner = value[1..^1].Trim();
            if (inner.Length == 0)
            {
                return Array.Empty<string>();
            }

            return inner.Split(',').Select(s => Unquote(s.Trim())).Where(s => s.Length > 0).ToArray();
        }

        return Unquote(value);
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            return s[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'')
        {
            return s[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return s;
    }

    private static string RenderValue(object value)
    {
        if (value is IEnumerable<string> list && value is not string)
        {
            return "[" + string.Join(", ", list.Select(Scalar)) + "]";
        }

        return Scalar(value.ToString() ?? "");
    }

    /// <summary>YAML okuyucularin yanlis yorumlayacagi dizeler cift tirnakla yazilir.</summary>
    private static string Scalar(string s)
    {
        if (s.Length == 0)
        {
            return "\"\"";
        }

        var risky = s.Contains(": ", StringComparison.Ordinal) || s.Contains(" #", StringComparison.Ordinal)
            || s.Contains(',', StringComparison.Ordinal) || "[]{}\"'*&!|>%@`".Contains(s[0], StringComparison.Ordinal)
            || s[0] == ' ' || s[^1] == ' ' || s.Equals("null", StringComparison.OrdinalIgnoreCase)
            || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("false", StringComparison.OrdinalIgnoreCase);
        return risky ? "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : s;
    }
}
