using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.Domain.Mcp;

/// <summary>MCP sunucusuna nasil baglanilir. JSON'da adiyla tasinir; yeni uye sona eklenir (CLAUDE.md §5).</summary>
public enum McpTransport
{
    /// <summary>Yerel surec: <see cref="McpServer.Command"/> + <see cref="McpServer.Args"/>, ortam <see cref="McpServer.Env"/>.</summary>
    Stdio,
    /// <summary>Streamable HTTP: <see cref="McpServer.Url"/>, basliklar <see cref="McpServer.Headers"/>.</summary>
    Http,
    /// <summary>Server-Sent Events (eski HTTP tasimasi).</summary>
    Sse,
}

/// <summary>
/// Kayitli bir MCP sunucusu (docs/DOMAIN.md → MCP sunuculari). Tanim VERITABANINDADIR, <c>config/</c>'da degil: calistirilabilir
/// yol, yerel adres ve belirtecler makineye ozgudur ve git'e girmemelidir. Hangi ajanin kullanacagi ise ajanin md'sindedir
/// (<see cref="Agent.Mcp"/>) -- yetki ekibin tanimidir, ekip de <c>config/</c>'dadir.
/// <see cref="Env"/> ve <see cref="Headers"/> degerleri sir sayilir: hicbir yanita acik yazilmaz (Api maskeler).
/// </summary>
public sealed record McpServer(
    string Key,
    string Name,
    McpTransport Transport,
    string? Command,
    IReadOnlyList<string> Args,
    string? Url,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyDictionary<string, string> Headers,
    bool Enabled = true,
    string Description = "",
    DateTimeOffset? UpdatedAt = null)
{
    /// <summary>Ajan aracinin tam adi <c>mcp__{key}__{arac}</c>; anahtar dosya/arac adina donustugu icin ajan anahtariyla ayni kural.</summary>
    public void Validate()
    {
        Identifiers.Require(Key, ErrorCodes.McpInvalidKey, "MCP sunucusu");
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new DomainException(ErrorCodes.McpInvalid, $"{Key}: ad bos.");
        }

        if (!Enum.IsDefined(Transport))
        {
            throw new DomainException(ErrorCodes.McpInvalid, $"{Key}: bilinmeyen tasima '{Transport}' (stdio | http | sse).");
        }

        if (Transport == McpTransport.Stdio)
        {
            if (string.IsNullOrWhiteSpace(Command))
            {
                throw new DomainException(ErrorCodes.McpInvalid, $"{Key}: stdio sunucusu icin komut gerekli (ör. npx, uvx, node).");
            }
        }
        else if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new DomainException(ErrorCodes.McpInvalid, $"{Key}: {Transport} sunucusu icin mutlak http(s) adresi gerekli.");
        }

        if (Env.Keys.Any(string.IsNullOrWhiteSpace) || Headers.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new DomainException(ErrorCodes.McpInvalid, $"{Key}: ortam degiskeni / baslik adi bos olamaz.");
        }
    }

    /// <summary>Tel adi kucuk harf: <c>stdio | http | sse</c> (runtime ve SDK ayni adi bekler).</summary>
    public static string Wire(McpTransport transport) => transport.ToString().ToLowerInvariant();
}

/// <summary>Saglayici yetenekleri: MCP araclarini hangi saglayici calistirabilir. Karar .NET'te; runtime yalniz esler.</summary>
public static class McpSupport
{
    /// <summary>Bugun yalniz Claude Agent SDK (anthropic). Codex (openai) destekler ama baglanmadi -- varsayimla ilerlenir.</summary>
    public static bool Supports(Provider provider) => provider == Provider.Anthropic;
}
