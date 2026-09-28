using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MrHobist.AITeam.Application.Mcp;
using MrHobist.AITeam.Domain;

namespace MrHobist.AITeam.Infrastructure.Runtime;

/// <summary>
/// MCP yetkilendirme spesifikasyonuna gore keşif, dinamik kayit ve belirtec cagrilari (docs/DOMAIN.md → MCP → OAuth).
/// Keşif sirasi: MCP adresine kimliksiz istek → 401 <c>WWW-Authenticate: Bearer resource_metadata="…"</c> → korunan kaynak bilgisi
/// (RFC 9728; yoksa <c>/.well-known/oauth-protected-resource</c>) → yetki sunucusu bilgisi (RFC 8414 / OpenID). Uzak uclar https
/// olmali (loopback haric). Belirtecler ve gizli anahtar gunluge yazilmaz.
/// </summary>
public sealed partial class McpOAuthHttpClient(IHttpClientFactory factory) : IMcpOAuthClient
{
    public const string HttpClientName = "mcp-oauth";

    [GeneratedRegex("resource_metadata=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceMetadata();

    [GeneratedRegex("(?:^|[\\s,])scope=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderScope();

    private HttpClient Http => factory.CreateClient(HttpClientName);

    public async Task<McpOAuthMetadata> DiscoverAsync(string mcpUrl, CancellationToken ct)
    {
        var mcp = RequireSafe(mcpUrl, "MCP adresi");
        var http = Http;

        // 1) Kimliksiz istek: 401 ise korunan kaynak bilgisinin adresi basliktan.
        var candidates = new List<string>();
        string? headerScope = null;
        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Post, mcp)
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"mrhobist","version":"1"}}}""", Encoding.UTF8, "application/json"),
            };
            probe.Headers.Accept.ParseAdd("application/json");
            probe.Headers.Accept.ParseAdd("text/event-stream");
            using var res = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            foreach (var h in res.Headers.WwwAuthenticate)
            {
                if (ResourceMetadata().Match(h.ToString()) is { Success: true } m)
                {
                    candidates.Add(m.Groups[1].Value);
                }

                // Sunucunun istedigi kapsam (MCP spec: varsa once bu kullanilir; ör. Figma "mcp:connect").
                if (HeaderScope().Match(h.ToString()) is { Success: true } sc)
                {
                    headerScope = sc.Groups[1].Value;
                }
            }
        }
        catch (HttpRequestException ex)
        {
            throw new DomainException(ErrorCodes.McpOAuthDiscoveryFailed, $"MCP adresine ulasilamadi: {ex.Message}");
        }

        var origin = mcp.GetLeftPart(UriPartial.Authority);
        var path = mcp.AbsolutePath.TrimEnd('/');
        candidates.Add($"{origin}/.well-known/oauth-protected-resource{path}");
        candidates.Add($"{origin}/.well-known/oauth-protected-resource");

        // 2) Korunan kaynak bilgisi → yetki sunucusu.
        string? issuer = null;
        var resource = mcp.ToString();
        IReadOnlyList<string>? scopes = null;
        foreach (var url in candidates.Distinct(StringComparer.Ordinal))
        {
            if (await GetJsonAsync(http, url, ct).ConfigureAwait(false) is { } prm && prm.TryGetProperty("authorization_servers", out var servers) && servers.ValueKind == JsonValueKind.Array && servers.GetArrayLength() > 0)
            {
                issuer = servers[0].GetString();
                if (prm.TryGetProperty("resource", out var r) && r.GetString() is { Length: > 0 } rs)
                {
                    resource = rs;
                }

                scopes = Strings(prm, "scopes_supported");
                break;
            }
        }

        // 3) Yetki sunucusu bilgisi. Korunan kaynak bilgisi yoksa (eski sunucular) yetki sunucusu MCP'nin kokudur.
        var asUri = RequireSafe(issuer ?? origin, "yetki sunucusu");
        var asOrigin = asUri.GetLeftPart(UriPartial.Authority);
        var asPath = asUri.AbsolutePath.TrimEnd('/');
        var metaUrls = asPath.Length > 0
            ? new[] { $"{asOrigin}/.well-known/oauth-authorization-server{asPath}", $"{asOrigin}/.well-known/openid-configuration{asPath}", $"{asUri.ToString().TrimEnd('/')}/.well-known/openid-configuration" }
            : new[] { $"{asOrigin}/.well-known/oauth-authorization-server", $"{asOrigin}/.well-known/openid-configuration" };
        foreach (var url in metaUrls)
        {
            if (await GetJsonAsync(http, url, ct).ConfigureAwait(false) is { } meta
                && meta.TryGetProperty("authorization_endpoint", out var auth) && auth.GetString() is { } authorization
                && meta.TryGetProperty("token_endpoint", out var tok) && tok.GetString() is { } token)
            {
                if (Strings(meta, "code_challenge_methods_supported") is { Count: > 0 } methods && !methods.Contains("S256"))
                {
                    throw new DomainException(ErrorCodes.McpOAuthDiscoveryFailed, "Yetki sunucusu PKCE S256 desteklemiyor.");
                }

                var registration = meta.TryGetProperty("registration_endpoint", out var reg) ? reg.GetString() : null;
                return new McpOAuthMetadata(
                    RequireSafe(authorization, "yetkilendirme ucu").ToString(),
                    RequireSafe(token, "belirtec ucu").ToString(),
                    registration is null ? null : RequireSafe(registration, "kayit ucu").ToString(),
                    // Kapsam yalniz korunan kaynak bilgisinden: yetki sunucusunun tum listesini istemek gereksiz yetki demek (MCP spec).
                    headerScope is null ? scopes : headerScope.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    resource);
            }
        }

        throw new DomainException(ErrorCodes.McpOAuthDiscoveryFailed, $"Yetki sunucusu bilgisi bulunamadi ({asUri}). Sunucu OAuth desteklemiyor olabilir.");
    }

    public async Task<McpOAuthClientRegistration> RegisterAsync(string registrationEndpoint, string redirectUri, string clientName, CancellationToken ct)
    {
        var body = new
        {
            client_name = clientName,
            redirect_uris = new[] { redirectUri },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
        };
        HttpResponseMessage res;
        try
        {
            res = await Http.PostAsJsonAsync(RequireSafe(registrationEndpoint, "kayit ucu"), body, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new DomainException(ErrorCodes.McpOAuthDiscoveryFailed, $"Istemci kaydi yapilamadi: {ex.Message}");
        }

        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                throw new DomainException(ErrorCodes.McpOAuthClientRequired, $"Sunucu dinamik istemci kaydini reddetti (HTTP {(int)res.StatusCode}{OAuthError(text)}). Kendi OAuth uygulamanin istemci kimligini gir.");
            }

            using var doc = JsonDocument.Parse(text);
            var id = doc.RootElement.TryGetProperty("client_id", out var c) ? c.GetString() : null;
            if (string.IsNullOrEmpty(id))
            {
                throw new DomainException(ErrorCodes.McpOAuthClientRequired, "Istemci kaydi client_id dondurmedi.");
            }

            var secret = doc.RootElement.TryGetProperty("client_secret", out var s) ? s.GetString() : null;
            return new McpOAuthClientRegistration(id, string.IsNullOrEmpty(secret) ? null : secret);
        }
    }

    public async Task<McpOAuthTokens> TokenAsync(string tokenEndpoint, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, RequireSafe(tokenEndpoint, "belirtec ucu")) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage res;
        try
        {
            res = await Http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new DomainException(ErrorCodes.McpOAuthTokenFailed, $"Belirtec ucuna ulasilamadi: {ex.Message}");
        }

        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                throw new DomainException(ErrorCodes.McpOAuthTokenFailed, $"Belirtec alinamadi (HTTP {(int)res.StatusCode}{OAuthError(text)}).");
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
                if (string.IsNullOrEmpty(access))
                {
                    throw new DomainException(ErrorCodes.McpOAuthTokenFailed, $"Belirtec yaniti access_token icermiyor{OAuthError(text)}.");
                }

                int? expires = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32()
                    : root.TryGetProperty("expires_in", out var es) && int.TryParse(es.GetString(), out var ei) ? ei : null;
                return new McpOAuthTokens(
                    access,
                    root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
                    expires,
                    root.TryGetProperty("scope", out var sc) ? sc.GetString() : null);
            }
            catch (JsonException)
            {
                throw new DomainException(ErrorCodes.McpOAuthTokenFailed, "Belirtec yaniti JSON degil.");
            }
        }
    }

    private static async Task<JsonElement?> GetJsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var res = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (res.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static List<string>? Strings(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList()
            : null;

    /// <summary>Hata yanitindaki <c>error</c> / <c>error_description</c> (varsa); belirtec icermez.</summary>
    private static string OAuthError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var err = doc.RootElement.TryGetProperty("error", out var e) ? e.ToString() : null;
            var desc = doc.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            return err is null ? "" : $": {err}{(desc is null ? "" : " — " + desc)}";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>OAuth uclari https olmali; yalniz loopback'te http kabul edilir (yerel sunucu, test).</summary>
    private static Uri RequireSafe(string url, string what)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)))
        {
            throw new DomainException(ErrorCodes.McpOAuthDiscoveryFailed, $"{what} guvenli degil ya da gecersiz: '{url}' (https gerekli).");
        }

        return u;
    }
}
