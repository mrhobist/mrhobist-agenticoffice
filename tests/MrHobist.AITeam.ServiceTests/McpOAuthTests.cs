using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Mcp;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Mcp;
using MrHobist.AITeam.Infrastructure.Runtime;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>
/// MCP OAuth girisi (kullanici istegi 2026-09-23) sahte bir yetki sunucusuna karsi: keşif (401 → korunan kaynak → yetki sunucusu),
/// dinamik kayit, PKCE (S256 dogrulanir), kod takasi, tur oncesi yenileme, tek kullanimlik state, cikis. Gercek HTTP istemcisi
/// (<see cref="McpOAuthHttpClient"/>) sahte isleyiciyle kosar: tel bicimi de sinanir.
/// </summary>
public sealed class McpOAuthTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Redirect = "http://127.0.0.1:5080/api/v1/mcp/oauth/callback";

    private readonly StorageFixture _fx = new();
    private readonly FakeAuthServer _auth = new();
    private readonly IMcpStore _store;
    private readonly McpOAuthService _oauth;
    private readonly McpService _mcp;

    public McpOAuthTests()
    {
        _store = _fx.Get<IMcpStore>();
        _oauth = new McpOAuthService(_store, new McpOAuthHttpClient(new Factory(_auth)), new McpOAuthPending());
        _mcp = new McpService(_store, new MarkdownAgentStore(_fx.Paths), new RunServiceTests.FakeRuntime());
        _mcp.CreateAsync(new McpServerRequest("remote", "Uzak", McpTransport.Http, Url: "https://mcp.test/mcp"), Ct).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _auth.Dispose();
        _fx.Dispose();
    }

    private static Dictionary<string, string> Query(string url)
        => new Uri(url).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal);

    [Fact]
    public async Task Giris_kesif_kayit_pkce_takas_ve_yenileme()
    {
        var start = await _oauth.StartAsync("remote", new McpOAuthStartRequest(), Redirect, Ct);
        Assert.True(start.Registered);
        var q = Query(start.AuthorizationUrl);
        Assert.StartsWith("https://auth.test/authorize?", start.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Equal("cid-1", q["client_id"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal(Redirect, q["redirect_uri"]);
        Assert.Equal("https://mcp.test/mcp", q["resource"]);
        Assert.Equal("read", q["scope"]);                       // korunan kaynak bilgisinden
        _auth.ExpectChallenge = q["code_challenge"];

        // Istemci kaydi hemen saklanir, belirtec yok: sunucu "giris yok" diye atlanir.
        var agent = new Agent("dev", "Dev", "", [], null, null, [], null, "p", Mcp: ["remote"]);
        Assert.Contains("OAuth", Assert.Single(McpService.Resolve(agent, await _store.ListAsync(Ct), DateTimeOffset.UtcNow).Skipped), StringComparison.Ordinal);

        Assert.Equal("remote", await _oauth.CompleteAsync(q["state"], "kod-ok", null, Ct));
        var view = await _mcp.GetAsync("remote", Ct);
        Assert.True(view.OAuth!.LoggedIn);
        Assert.True(view.OAuth.CanRefresh);

        // Belirtec her turda Bearer basligi olarak gider; yanit/goruntu belirteci tasimaz.
        var resolved = McpService.Resolve(agent, await _store.ListAsync(Ct), DateTimeOffset.UtcNow);
        Assert.Equal("Bearer at-1", resolved.Servers["remote"].Headers!["Authorization"]);
        Assert.DoesNotContain("at-1", JsonSerializer.Serialize(view), StringComparison.Ordinal);

        // State tek kullanimlik.
        Assert.Equal(ErrorCodes.McpOAuthStateInvalid, (await Assert.ThrowsAsync<DomainException>(() => _oauth.CompleteAsync(q["state"], "kod-ok", null, Ct))).ErrorCode);

        // expires_in 60 s < 5 dk: tur oncesi yenilenir; sunucu yeni yenileme belirteci vermediyse eskisi kalir.
        var fresh = await _oauth.EnsureFreshAsync(await _store.ListAsync(Ct), ["remote"], Ct);
        var o = fresh.Single(s => s.Key == "remote").OAuth!;
        Assert.Equal("at-2", o.AccessToken);
        Assert.Equal("rt-1", o.RefreshToken);
        Assert.Equal("at-2", (await _store.GetAsync("remote", Ct))!.OAuth!.AccessToken);

        // Yeniden giris kaydi tekrarlamaz.
        await _oauth.StartAsync("remote", new McpOAuthStartRequest(), Redirect, Ct);
        Assert.Equal(1, _auth.Registrations);

        // Cikis belirteci siler, istemci kalir.
        await _oauth.LogoutAsync("remote", Ct);
        var after = (await _store.GetAsync("remote", Ct))!.OAuth!;
        Assert.Null(after.AccessToken);
        Assert.Equal("cid-1", after.ClientId);
    }

    [Fact]
    public async Task Yanlis_pkce_dogrulayicisi_ve_reddedilen_yetki_belirtec_vermez()
    {
        var q = Query((await _oauth.StartAsync("remote", new McpOAuthStartRequest(), Redirect, Ct)).AuthorizationUrl);
        _auth.ExpectChallenge = "baska-bir-deger";
        Assert.Equal(ErrorCodes.McpOAuthTokenFailed, (await Assert.ThrowsAsync<DomainException>(() => _oauth.CompleteAsync(q["state"], "kod-ok", null, Ct))).ErrorCode);

        var q2 = Query((await _oauth.StartAsync("remote", new McpOAuthStartRequest(), Redirect, Ct)).AuthorizationUrl);
        Assert.Equal(ErrorCodes.McpOAuthTokenFailed, (await Assert.ThrowsAsync<DomainException>(() => _oauth.CompleteAsync(q2["state"], null, "access_denied", Ct))).ErrorCode);
        Assert.Null((await _store.GetAsync("remote", Ct))!.OAuth!.AccessToken);
    }

    [Fact]
    public async Task Kayit_yoksa_istemci_istenir_verilirse_o_kullanilir_yerel_sunucuda_oauth_yok()
    {
        _auth.OfferRegistration = false;
        Assert.Equal(ErrorCodes.McpOAuthClientRequired, (await Assert.ThrowsAsync<DomainException>(() => _oauth.StartAsync("remote", new McpOAuthStartRequest(), Redirect, Ct))).ErrorCode);

        var start = await _oauth.StartAsync("remote", new McpOAuthStartRequest("benim-uygulamam", "gizli", "repo"), Redirect, Ct);
        Assert.False(start.Registered);
        var q = Query(start.AuthorizationUrl);
        Assert.Equal("benim-uygulamam", q["client_id"]);
        Assert.Equal("repo", q["scope"]);
        _auth.ExpectChallenge = q["code_challenge"];
        await _oauth.CompleteAsync(q["state"], "kod-ok", null, Ct);
        Assert.Equal("gizli", _auth.LastClientSecret);          // gizli anahtar belirtec ucuna gider

        await _mcp.CreateAsync(new McpServerRequest("yerel", "Yerel", McpTransport.Stdio, "npx"), Ct);
        Assert.Equal(ErrorCodes.McpOAuthUnsupported, (await Assert.ThrowsAsync<DomainException>(() => _oauth.StartAsync("yerel", new McpOAuthStartRequest(), Redirect, Ct))).ErrorCode);
    }

    [Fact]
    public async Task Guvensiz_http_yetki_ucu_reddedilir()
    {
        _auth.InsecureTokenEndpoint = true;
        Assert.Equal(ErrorCodes.McpOAuthDiscoveryFailed, (await Assert.ThrowsAsync<DomainException>(() => _oauth.StartAsync("remote", new McpOAuthStartRequest(), Redirect, Ct))).ErrorCode);
    }

    // ------------------------------------------------------------------ sahte yetki sunucusu

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeAuthServer : HttpMessageHandler
    {
        private static readonly string[] AuthServers = ["https://auth.test"];
        private static readonly string[] Scopes = ["read"];
        private static readonly string[] S256 = ["S256"];

        public string? ExpectChallenge { get; set; }

        public bool OfferRegistration { get; set; } = true;

        public bool InsecureTokenEndpoint { get; set; }

        public int Registrations { get; private set; }

        public string? LastClientSecret { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Post && url == "https://mcp.test/mcp")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                r.Headers.WwwAuthenticate.ParseAdd("Bearer resource_metadata=\"https://mcp.test/.well-known/oauth-protected-resource/mcp\"");
                return r;
            }

            if (url == "https://mcp.test/.well-known/oauth-protected-resource/mcp")
            {
                return Json(new { resource = "https://mcp.test/mcp", authorization_servers = AuthServers, scopes_supported = Scopes });
            }

            if (url == "https://auth.test/.well-known/oauth-authorization-server")
            {
                return Json(new
                {
                    issuer = "https://auth.test",
                    authorization_endpoint = "https://auth.test/authorize",
                    token_endpoint = InsecureTokenEndpoint ? "http://auth.test/token" : "https://auth.test/token",
                    registration_endpoint = OfferRegistration ? "https://auth.test/register" : null,
                    code_challenge_methods_supported = S256,
                });
            }

            if (url == "https://auth.test/register")
            {
                Registrations++;
                return Json(new { client_id = "cid-1" }, HttpStatusCode.Created);
            }

            if (url == "https://auth.test/token")
            {
                var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
                    .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
                LastClientSecret = form.GetValueOrDefault("client_secret");
                if (form["grant_type"] == "refresh_token")
                {
                    return form["refresh_token"] == "rt-1" ? Json(new { access_token = "at-2", token_type = "Bearer", expires_in = 3600 }) : Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
                }

                var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                return form["code"] == "kod-ok" && challenge == ExpectChallenge && form["resource"] == "https://mcp.test/mcp"
                    ? Json(new { access_token = "at-1", token_type = "Bearer", refresh_token = "rt-1", expires_in = 60 })
                    : Json(new { error = "invalid_grant", error_description = "PKCE" }, HttpStatusCode.BadRequest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }
}
