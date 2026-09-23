using System.Text;
using System.Text.RegularExpressions;

namespace MrHobist.AITeam.Domain.Mcp;

/// <summary>Katalog alaninin nereye yazildigi. JSON'da adiyla tasinir; yeni uye sona eklenir (CLAUDE.md §5).</summary>
public enum McpFieldTarget
{
    /// <summary>Yalniz sablonlarda kullanilir (<c>{AD}</c>): komut argumani, adres ya da baska bir alanin bicimi.</summary>
    Input,
    /// <summary>Ortam degiskeni (stdio). Ad = <see cref="McpCatalogField.Name"/>.</summary>
    Env,
    /// <summary>HTTP basligi (http/sse). Ad = <see cref="McpCatalogField.Header"/> ya da <see cref="McpCatalogField.Name"/>.</summary>
    Header,
}

/// <summary>
/// Kurulum formundaki tek alan. <see cref="Format"/> verilirse yazilan deger sablondan uretilir: <c>{value}</c> alanin kendi degeri,
/// <c>{AD}</c> baska bir alanin degeri, <c>{base64:A:B}</c> "A degeri:B degeri"nin base64'u (HTTP Basic). Istege bagli alan
/// bos birakilirsa hic yazilmaz; ona dayanan arguman da atilir.
/// </summary>
public sealed record McpCatalogField(
    string Name,
    string Label,
    McpFieldTarget Target = McpFieldTarget.Env,
    bool Secret = false,
    bool Required = true,
    string? Placeholder = null,
    string? Help = null,
    string? Format = null,
    string? Default = null,
    IReadOnlyList<string>? Choices = null,
    string? Header = null);

/// <summary>
/// Bir sunucuya baglanmanin bir yolu (ör. "Jira Cloud: e-posta + API token", "Server/DC: kisisel erisim belirteci").
/// <see cref="Args"/> ve <see cref="Url"/> <c>{AD}</c> sablonu tasiyabilir. <see cref="Supported"/> false = bu ofiste henuz
/// kurulamaz (ör. etkilesimli OAuth); secenek bilgi olarak listelenir, nedeni <see cref="Notes"/>'ta. <see cref="Requires"/>: makinede
/// kurulu olmasi gerekenler (Node.js, uv, Docker, Figma masaustu); ekranda gosterilir, denetlenmez.
/// </summary>
public sealed record McpAuthOption(
    string Id,
    string Label,
    McpTransport Transport,
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    string? Url = null,
    IReadOnlyList<McpCatalogField>? Fields = null,
    bool Supported = true,
    string? Description = null,
    string? Notes = null,
    IReadOnlyList<string>? Requires = null);

/// <summary>Hazir MCP sunucusu (<c>config/mcp-catalog.json</c>): kurulum formunun kaynagi. Kurulan sunucu normal kayittir (<see cref="McpServer"/>).</summary>
public sealed record McpCatalogEntry(
    string Key,
    string Name,
    string Vendor,
    string Description,
    IReadOnlyList<McpAuthOption> Options,
    bool Official = false,
    string? DocsUrl = null,
    string? Notes = null);

/// <summary>Katalog secenegini doldurulmus degerlerle bir <see cref="McpServer"/>'a cevirir. Dosya I/O yok; kural burada.</summary>
public static partial class McpCatalogBuilder
{
    [GeneratedRegex(@"\{(?<expr>[A-Za-z0-9_:\-]+)\}")]
    private static partial Regex Placeholder();

    /// <summary>
    /// Zorunlu alan bossa, secenek desteklenmiyorsa ya da secimli alana listede olmayan deger geldiyse <c>mcp.invalid</c>.
    /// Uretilen sunucu yine <see cref="McpServer.Validate"/>'ten gecer.
    /// </summary>
    public static McpServer Build(McpCatalogEntry entry, McpAuthOption option, string key, string? name, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(values);
        if (!option.Supported)
        {
            throw new DomainException(ErrorCodes.McpInvalid, $"{entry.Name} · {option.Label}: bu yontem bu ofiste henuz kurulamiyor. {option.Notes}".Trim());
        }

        var fields = option.Fields ?? [];
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in fields)
        {
            var v = values.TryGetValue(f.Name, out var given) && !string.IsNullOrWhiteSpace(given) ? given.Trim() : f.Default?.Trim();
            if (string.IsNullOrEmpty(v))
            {
                if (f.Required)
                {
                    throw new DomainException(ErrorCodes.McpInvalid, $"'{f.Label}' zorunlu.");
                }

                continue;
            }

            if (f.Choices is { Count: > 0 } choices && !choices.Contains(v, StringComparer.Ordinal))
            {
                throw new DomainException(ErrorCodes.McpInvalid, $"'{f.Label}': gecersiz secim '{v}' ({string.Join(" | ", choices)}).");
            }

            resolved[f.Name] = v;
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields.Where(f => resolved.ContainsKey(f.Name)))
        {
            var text = f.Format is null ? resolved[f.Name] : Fill(f.Format, resolved, resolved[f.Name]);
            if (text is null)
            {
                continue;
            }

            switch (f.Target)
            {
                case McpFieldTarget.Env:
                    env[f.Name] = text;
                    break;
                case McpFieldTarget.Header:
                    headers[f.Header ?? f.Name] = text;
                    break;
                default:
                    break;
            }
        }

        // Argumanda sablon bos istege bagli alana dayaniyorsa arguman atilir (ör. "--api-key={KEY}" anahtar verilmediyse).
        var args = (option.Args ?? []).Select(a => Fill(a, resolved, null)).Where(a => a is not null).Select(a => a!).ToList();
        var url = option.Url is null ? null : Fill(option.Url, resolved, null)
            ?? throw new DomainException(ErrorCodes.McpInvalid, $"{entry.Name}: adres icin gereken alan bos.");

        var server = new McpServer(
            key,
            string.IsNullOrWhiteSpace(name) ? entry.Name : name.Trim(),
            option.Transport,
            option.Command,
            args,
            url,
            env,
            headers,
            Enabled: true,
            Description: $"{entry.Description} · {option.Label}");
        server.Validate();
        return server;
    }

    /// <summary>Sablonu doldurur; basvurdugu alan bossa null (cagiran o parcayi atar).</summary>
    private static string? Fill(string template, Dictionary<string, string> values, string? self)
    {
        var missing = false;
        var text = Placeholder().Replace(template, m =>
        {
            var expr = m.Groups["expr"].Value;
            if (expr == "value")
            {
                missing |= self is null;
                return self ?? "";
            }

            if (expr.StartsWith("base64:", StringComparison.Ordinal))
            {
                var parts = expr["base64:".Length..].Split(':');
                var joined = new List<string>();
                foreach (var p in parts)
                {
                    if (!values.TryGetValue(p, out var pv))
                    {
                        missing = true;
                        return "";
                    }

                    joined.Add(pv);
                }

                return Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(':', joined)));
            }

            if (values.TryGetValue(expr, out var v))
            {
                return v;
            }

            missing = true;
            return "";
        });
        return missing ? null : text;
    }

    /// <summary>Katalog tutarliligi: anahtarlar, secenek kimlikleri benzersiz, sablonlar bilinen alanlara basvuruyor.</summary>
    public static void Validate(IReadOnlyList<McpCatalogEntry> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in catalog)
        {
            Identifiers.Require(e.Key, ErrorCodes.ConfigFileInvalid, "MCP katalog");
            if (!keys.Add(e.Key) || e.Options.Count == 0)
            {
                throw new DomainException(ErrorCodes.ConfigFileInvalid, $"mcp-catalog: '{e.Key}' yinelenmis ya da seceneksiz.");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var o in e.Options)
            {
                if (!ids.Add(o.Id))
                {
                    throw new DomainException(ErrorCodes.ConfigFileInvalid, $"mcp-catalog: {e.Key}/{o.Id} yinelenmis.");
                }

                var names = (o.Fields ?? []).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

                // Arguman ve adres yanitta acik doner (McpServerView): sir oraya yazilirsa maskeleme delinir.
                var secrets = (o.Fields ?? []).Where(f => f.Secret).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
                var visible = (o.Args ?? []).Append(o.Url ?? "").SelectMany(t => Placeholder().Matches(t)).Select(m => m.Groups["expr"].Value);
                if (visible.FirstOrDefault(secrets.Contains) is { } leaked)
                {
                    throw new DomainException(ErrorCodes.ConfigFileInvalid, $"mcp-catalog: {e.Key}/{o.Id} sir alani '{leaked}' arguman ya da adrese yaziliyor; ortam degiskeni ya da baslik kullan.");
                }
                var templates = (o.Args ?? []).Append(o.Url ?? "").Concat((o.Fields ?? []).Select(f => f.Format ?? ""));
                foreach (Match m in templates.SelectMany(t => Placeholder().Matches(t)))
                {
                    var expr = m.Groups["expr"].Value;
                    string[] refs = expr == "value" ? [] : expr.StartsWith("base64:", StringComparison.Ordinal) ? expr[7..].Split(':') : [expr];
                    foreach (var r in refs.Where(r => !names.Contains(r)))
                    {
                        throw new DomainException(ErrorCodes.ConfigFileInvalid, $"mcp-catalog: {e.Key}/{o.Id} sablonu bilinmeyen alana basvuruyor: {{{r}}}.");
                    }
                }
            }
        }
    }
}
