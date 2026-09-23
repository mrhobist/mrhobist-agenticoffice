using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Common;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Mcp;

namespace MrHobist.AITeam.Application.Mcp;

/// <summary>Yetki sunucusu bilgileri (keşif sonucu). <see cref="Resource"/>: belirtecin baglanacagi MCP adresi (RFC 8707).</summary>
public sealed record McpOAuthMetadata(string AuthorizationEndpoint, string TokenEndpoint, string? RegistrationEndpoint, IReadOnlyList<string>? ScopesSupported, string Resource);

public sealed record McpOAuthClientRegistration(string ClientId, string? ClientSecret);

public sealed record McpOAuthTokens(string AccessToken, string? RefreshToken, int? ExpiresIn, string? Scope);

/// <summary>OAuth HTTP islemleri (Infrastructure). Hatalar <c>mcp.oauth_*</c> kodlu <see cref="DomainException"/>.</summary>
public interface IMcpOAuthClient
{
    /// <summary>MCP adresinden yetki sunucusunu bulur: 401 <c>WWW-Authenticate: resource_metadata</c> → korunan kaynak bilgisi → yetki sunucusu bilgisi.</summary>
    Task<McpOAuthMetadata> DiscoverAsync(string mcpUrl, CancellationToken ct);

    /// <summary>Dinamik istemci kaydi (RFC 7591).</summary>
    Task<McpOAuthClientRegistration> RegisterAsync(string registrationEndpoint, string redirectUri, string clientName, CancellationToken ct);

    /// <summary>Belirtec ucu (kod takasi ya da yenileme); form alanlari cagirandan.</summary>
    Task<McpOAuthTokens> TokenAsync(string tokenEndpoint, IReadOnlyDictionary<string, string> form, CancellationToken ct);
}

/// <summary><c>POST /mcp/{key}/oauth/start</c> govdesi. Istemci verilmezse sunucu destekliyorsa dinamik kayit yapilir.</summary>
public sealed record McpOAuthStartRequest(string? ClientId = null, string? ClientSecret = null, string? Scope = null);

/// <summary>Tarayicida acilacak yetkilendirme adresi.</summary>
public sealed record McpOAuthStart(string AuthorizationUrl, bool Registered);

/// <summary>
/// Suren OAuth girisleri (bellekte; surec yeniden baslarsa giris yeniden baslatilir). <c>state</c> tek kullanimlik ve 15 dk
/// gecerlidir: JWT'siz gelen tarayici donusunde kimligin yerine gecer.
/// </summary>
public sealed class McpOAuthPending
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Entry> _items = new(StringComparer.Ordinal);

    public sealed record Entry(string ServerKey, string Verifier, string ClientId, string? ClientSecret, string TokenEndpoint, string RedirectUri, string Resource, DateTimeOffset CreatedAt);

    public string Add(Entry entry)
    {
        foreach (var old in _items.Where(kv => DateTimeOffset.UtcNow - kv.Value.CreatedAt > Ttl).Select(kv => kv.Key).ToList())
        {
            _items.TryRemove(old, out _);
        }

        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        _items[state] = entry;
        return state;
    }

    /// <summary>Durumu tuketir; bilinmeyen/suresi dolmus → null.</summary>
    public Entry? Take(string state)
        => _items.TryRemove(state ?? "", out var e) && DateTimeOffset.UtcNow - e.CreatedAt <= Ttl ? e : null;

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Uzak MCP sunuculari icin OAuth 2.1 girisi (docs/DOMAIN.md → MCP → OAuth; kullanici istegi 2026-09-23). Akis .NET'tedir,
/// runtime durumsuz kalir: belirtec her turda <c>Authorization: Bearer</c> basligi olarak gider (<see cref="McpService.ToRuntime"/>).
/// Istemci: kullanici verdiyse o, vermediyse ve sunucu destekliyorsa dinamik kayit; kayit sunucuya saklanir, yeniden giriste kullanilir.
/// </summary>
public sealed class McpOAuthService(IMcpStore store, IMcpOAuthClient client, McpOAuthPending pending) : IMcpTokenRefresher
{
    public const string ClientName = "MrHobist AI Ofis";

    /// <summary>Bu kadar sure icinde dolacak belirtec tur oncesi yenilenir.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    public async Task<McpOAuthStart> StartAsync(string key, McpOAuthStartRequest request, string redirectUri, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var server = await store.GetAsync(key, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(ErrorCodes.McpNotFound, $"MCP sunucusu yok: '{key}'.");
        if (server.Transport == McpTransport.Stdio || string.IsNullOrWhiteSpace(server.Url))
        {
            throw new DomainException(ErrorCodes.McpOAuthUnsupported, $"{key}: OAuth yalniz uzak (http/sse) sunucularda; yerel sunucu kimligi ortam degiskeniyle alir.");
        }

        var meta = await client.DiscoverAsync(server.Url, ct).ConfigureAwait(false);

        // Istemci: once kullanicinin verdigi, sonra ayni yetki sunucusuna daha once alinmis kayit, en son dinamik kayit.
        string clientId;
        string? secret;
        var registered = false;
        if (!string.IsNullOrWhiteSpace(request.ClientId))
        {
            clientId = request.ClientId.Trim();
            secret = string.IsNullOrWhiteSpace(request.ClientSecret) ? null : request.ClientSecret.Trim();
        }
        else if (server.OAuth is { Registered: true } prev && prev.TokenEndpoint == meta.TokenEndpoint)
        {
            (clientId, secret, registered) = (prev.ClientId, prev.ClientSecret, true);
        }
        else if (meta.RegistrationEndpoint is { } reg)
        {
            var r = await client.RegisterAsync(reg, redirectUri, ClientName, ct).ConfigureAwait(false);
            (clientId, secret, registered) = (r.ClientId, r.ClientSecret, true);
        }
        else
        {
            throw new DomainException(ErrorCodes.McpOAuthClientRequired, $"{key}: sunucu dinamik istemci kaydini desteklemiyor; kendi OAuth uygulamanin istemci kimligini (ve gizli anahtarini) gir. Donus adresi: {redirectUri}");
        }

        var scope = string.IsNullOrWhiteSpace(request.Scope)
            ? meta.ScopesSupported is { Count: > 0 } s ? string.Join(' ', s) : null
            : request.Scope.Trim();

        // Istemci kaydi ve uclar hemen saklanir (belirtec yok): giris yarim kalsa da yeniden denemede kayit tekrarlanmaz.
        await store.SaveAsync(server with
        {
            OAuth = new McpOAuth(clientId, secret, meta.AuthorizationEndpoint, meta.TokenEndpoint, scope,
                server.OAuth?.TokenEndpoint == meta.TokenEndpoint ? server.OAuth.AccessToken : null,
                server.OAuth?.TokenEndpoint == meta.TokenEndpoint ? server.OAuth.RefreshToken : null,
                server.OAuth?.TokenEndpoint == meta.TokenEndpoint ? server.OAuth.ExpiresAt : null,
                registered),
        }, ct).ConfigureAwait(false);

        var verifier = McpOAuthPending.Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = McpOAuthPending.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = pending.Add(new McpOAuthPending.Entry(key, verifier, clientId, secret, meta.TokenEndpoint, redirectUri, meta.Resource, DateTimeOffset.UtcNow));

        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", clientId),
            new("redirect_uri", redirectUri),
            new("code_challenge", challenge),
            new("code_challenge_method", "S256"),
            new("state", state),
            new("resource", meta.Resource),
        };
        if (scope is not null)
        {
            query.Add(new("scope", scope));
        }

        var sep = meta.AuthorizationEndpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var url = meta.AuthorizationEndpoint + sep + string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return new McpOAuthStart(url, registered);
    }

    /// <summary>Tarayici donusu: kodu belirtece cevirir, sunucuya yazar. Doner: sunucu anahtari. Bilinmeyen/suresi dolmus state <c>mcp.oauth_state_invalid</c>.</summary>
    public async Task<string> CompleteAsync(string? state, string? code, string? error, CancellationToken ct)
    {
        var entry = pending.Take(state ?? "")
            ?? throw new DomainException(ErrorCodes.McpOAuthStateInvalid, "Giris oturumu bulunamadi ya da suresi doldu; panelden yeniden baslat.");
        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
        {
            throw new DomainException(ErrorCodes.McpOAuthTokenFailed, $"{entry.ServerKey}: yetkilendirme reddedildi ({error ?? "kod yok"}).");
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = entry.RedirectUri,
            ["client_id"] = entry.ClientId,
            ["code_verifier"] = entry.Verifier,
            ["resource"] = entry.Resource,
        };
        if (entry.ClientSecret is { } secret)
        {
            form["client_secret"] = secret;
        }

        var tokens = await client.TokenAsync(entry.TokenEndpoint, form, ct).ConfigureAwait(false);
        var server = await store.GetAsync(entry.ServerKey, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(ErrorCodes.McpNotFound, $"MCP sunucusu yok: '{entry.ServerKey}'.");
        var o = server.OAuth ?? new McpOAuth(entry.ClientId, entry.ClientSecret, "", entry.TokenEndpoint);
        await store.SaveAsync(server with { OAuth = Apply(o, tokens) }, ct).ConfigureAwait(false);
        return entry.ServerKey;
    }

    /// <summary>Belirtecleri siler; istemci kaydi kalir (yeniden giriste kullanilir).</summary>
    public async Task LogoutAsync(string key, CancellationToken ct)
    {
        var server = await store.GetAsync(key, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(ErrorCodes.McpNotFound, $"MCP sunucusu yok: '{key}'.");
        if (server.OAuth is { } o)
        {
            await store.SaveAsync(server with { OAuth = o with { AccessToken = null, RefreshToken = null, ExpiresAt = null } }, ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<McpServer>> EnsureFreshAsync(IReadOnlyList<McpServer> servers, IReadOnlyList<string> keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(keys);
        var now = DateTimeOffset.UtcNow;
        var result = servers.ToList();
        for (var i = 0; i < result.Count; i++)
        {
            var s = result[i];
            if (!keys.Contains(s.Key, StringComparer.Ordinal) || s.OAuth is not { } o || !o.NeedsRefresh(now, RefreshMargin) || s.Url is null)
            {
                continue;
            }

            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = o.RefreshToken!,
                ["client_id"] = o.ClientId,
                ["resource"] = s.Url,
            };
            if (o.ClientSecret is { } secret)
            {
                form["client_secret"] = secret;
            }

            try
            {
                var tokens = await client.TokenAsync(o.TokenEndpoint, form, ct).ConfigureAwait(false);
                result[i] = s with { OAuth = Apply(o, tokens) };
                await store.SaveAsync(result[i], ct).ConfigureAwait(false);
            }
            catch (DomainException)
            {
                // Yenilenemedi (belirtec iptal/sure bitti): sunucu oldugu gibi kalir; Resolve "giris yok" diye atlar, calisma durmaz.
            }
        }

        return result;
    }

    /// <summary>Yeni belirtecler; sunucu yenileme belirteci dondurmediyse eskisi korunur (RFC 6749 §6).</summary>
    private static McpOAuth Apply(McpOAuth o, McpOAuthTokens t) => o with
    {
        AccessToken = t.AccessToken,
        RefreshToken = t.RefreshToken ?? o.RefreshToken,
        ExpiresAt = t.ExpiresIn is { } s && s > 0 ? DateTimeOffset.UtcNow.AddSeconds(s) : null,
        Scope = t.Scope ?? o.Scope,
    };
}
