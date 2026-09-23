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
    DateTimeOffset? UpdatedAt = null,
    /// <summary>
    /// Ajana acilacak araclar (izin listesi; kullanici istegi 2026-09-23). null = sunucunun tum araclari. Secilmeyen araclar
    /// SDK'ya <c>disallowed_tools</c> olarak gider (semasi baglama girmez) ve runtime'in izin denetimi de reddeder.
    /// </summary>
    IReadOnlyList<string>? Tools = null,
    /// <summary>Son basarili baglanti denemesinde gorulen araclar (secim ekrani bunu listeler). Durum, tanim degil.</summary>
    IReadOnlyList<McpKnownTool>? KnownTools = null,
    DateTimeOffset? ToolsCheckedAt = null,
    /// <summary>OAuth ile baglanan uzak sunucunun istemci kaydi ve belirtecleri (docs/DOMAIN.md → MCP → OAuth). Sir tasir.</summary>
    McpOAuth? OAuth = null)
{
    /// <summary>
    /// Izin listesi dogrulamasi: bilinen arac listesi varsa secim onun alt kumesi olmali (yazim hatasi sessizce "hicbir arac"
    /// demesin). Bos liste gecerlidir: sunucu acik ama hicbir araci verilmez.
    /// </summary>
    public void ValidateTools()
    {
        if (Tools is null || KnownTools is null)
        {
            return;
        }

        var known = KnownTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = Tools.Where(t => !known.Contains(t)).ToList();
        if (unknown.Count > 0)
        {
            throw new DomainException(ErrorCodes.McpInvalid, $"{Key}: sunucuda olmayan arac: {string.Join(", ", unknown)}. Once Baglantiyi dene ile listeyi yenile.");
        }
    }

    /// <summary>Ajan aracinin tam adi (Claude Agent SDK bicimi).</summary>
    public static string ToolName(string serverKey, string tool) => $"mcp__{serverKey}__{tool}";

    /// <summary>Secilmeyen bilinen araclar, SDK adiyla (<c>disallowed_tools</c>). Liste yoksa ya da secim yoksa bos.</summary>
    public IReadOnlyList<string> DisallowedToolNames()
        => Tools is null || KnownTools is null
            ? []
            : KnownTools.Select(t => t.Name).Where(n => !Tools.Contains(n, StringComparer.Ordinal)).Select(n => ToolName(Key, n)).ToList();

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

/// <summary>Sunucunun sundugu bir arac (baglanti denemesinden).</summary>
public sealed record McpKnownTool(string Name, string? Description = null);

/// <summary>
/// Uzak MCP sunucusunun OAuth durumu. <see cref="ClientId"/>/<see cref="ClientSecret"/>: dinamik kayitla (DCR) alinan ya da
/// kullanicinin girdigi istemci. <see cref="AccessToken"/> her turda <c>Authorization: Bearer</c> olarak gider; suresi dolmak
/// uzereyse <see cref="RefreshToken"/> ile tur ONCESI yenilenir. Belirtecler hicbir yanita yazilmaz.
/// </summary>
public sealed record McpOAuth(
    string ClientId,
    string? ClientSecret,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? Scope = null,
    string? AccessToken = null,
    string? RefreshToken = null,
    DateTimeOffset? ExpiresAt = null,
    /// <summary>Istemci dinamik kayitla mi alindi (kullanici girmediyse true).</summary>
    bool Registered = false)
{
    /// <summary>Gecerli bir erisim belirteci var mi (suresi bilinmiyorsa var sayilir).</summary>
    public bool HasToken(DateTimeOffset now) => !string.IsNullOrEmpty(AccessToken) && (ExpiresAt is null || ExpiresAt > now);

    /// <summary>Suresi <paramref name="margin"/> icinde doluyor ve yenilenebilir mi.</summary>
    public bool NeedsRefresh(DateTimeOffset now, TimeSpan margin) => !string.IsNullOrEmpty(RefreshToken) && ExpiresAt is { } e && e - now < margin;
}

/// <summary>Saglayici yetenekleri: MCP araclarini hangi saglayici calistirabilir. Karar .NET'te; runtime yalniz esler.</summary>
public static class McpSupport
{
    /// <summary>Bugun yalniz Claude Agent SDK (anthropic). Codex (openai) destekler ama baglanmadi -- varsayimla ilerlenir.</summary>
    public static bool Supports(Provider provider) => provider == Provider.Anthropic;
}
