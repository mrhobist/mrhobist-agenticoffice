using System.Net;
using MrHobist.AITeam.Application.Mcp;
using MrHobist.AITeam.Domain;

namespace MrHobist.AITeam.Api.Mcp;

/// <summary>
/// MCP sunucu yonetimi (docs/API.md → MCP). Ince adaptor: kural <see cref="IMcpService"/>'te. Ortam degiskeni / baslik degerleri
/// hicbir yanita yazilmaz; istekte degeri null gelen satir kayitli degeri korur.
/// </summary>
public static class McpEndpoints
{
    public static IEndpointRouteBuilder MapMcp(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/mcp");

        g.MapGet("", (IMcpService s, CancellationToken ct) => s.ListAsync(ct));
        g.MapPost("", async (McpServerRequest body, IMcpService s, CancellationToken ct) =>
        {
            var created = await s.CreateAsync(body, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/mcp/{created.Key}", created);
        });
        // Hazir sunucular: kurulum formunun kaynagi (config/mcp-catalog.json). Sabit yol {key}'den once eslesir; "catalog" anahtari ayrilmistir.
        g.MapGet("/catalog", (IMcpService s, CancellationToken ct) => s.CatalogAsync(ct));
        // Kullanim raporu: son N calismada sunucu/arac/ajan basina cagri, "verildi ama kullanilmadi" turlar. "usage" anahtari ayrilmistir.
        g.MapGet("/usage", (int? runs, IMcpUsageReader r, CancellationToken ct) => r.SummarizeAsync(runs ?? 200, ct));
        g.MapPost("/catalog/{key}/install", async (string key, McpInstallRequest body, IMcpService s, CancellationToken ct) =>
        {
            var created = await s.InstallAsync(key, body, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/mcp/{created.Key}", created);
        });
        g.MapGet("/{key}", (string key, IMcpService s, CancellationToken ct) => s.GetAsync(key, ct));
        g.MapPut("/{key}", (string key, McpServerRequest body, IMcpService s, CancellationToken ct) => s.UpdateAsync(key, body, ct));
        g.MapDelete("/{key}", async (string key, IMcpService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(key, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        // Yetki: bu sunucuyu kullanabilecek ajanlarin TAM listesi; ajan md'lerinin `mcp` alani guncellenir.
        g.MapPut("/{key}/access", (string key, McpAccessRequest body, IMcpService s, CancellationToken ct) => s.SetAccessAsync(key, body, ct));

        // OAuth girisi (docs/DOMAIN.md → MCP → OAuth): baslat → tarayicida yetkilendirme adresi acilir → saglayici loopback donus
        // adresine yonlendirir → kod belirtece cevrilir. Donus JWT'siz gelir; yetki tek kullanimlik state'tir (auth kapisinda acik).
        g.MapPost("/{key}/oauth/start", (string key, McpOAuthStartRequest? body, McpOAuthService oauth, IConfiguration config, CancellationToken ct)
            => oauth.StartAsync(key, body ?? new McpOAuthStartRequest(), RedirectUri(config), ct));
        g.MapPost("/{key}/oauth/logout", async (string key, McpOAuthService oauth, IMcpService s, CancellationToken ct) =>
        {
            await oauth.LogoutAsync(key, ct).ConfigureAwait(false);
            return await s.GetAsync(key, ct).ConfigureAwait(false);
        });
        g.MapGet("/oauth/callback", async (string? state, string? code, string? error, McpOAuthService oauth, CancellationToken ct) =>
        {
            try
            {
                var key = await oauth.CompleteAsync(state, code, error, ct).ConfigureAwait(false);
                return Page("Giriş tamamlandı", $"<b>{WebUtility.HtmlEncode(key)}</b> MCP sunucusuna bağlandı. Bu sekmeyi kapatıp ofise dönebilirsin.", ok: true);
            }
            catch (DomainException ex)
            {
                return Page("Giriş tamamlanamadı", WebUtility.HtmlEncode(ex.Message), ok: false);
            }
        }).ExcludeFromDescription();

        // Arac secimi: ajana yalniz secilen araclar acilir (null = hepsi). Secenekler son baglanti denemesinde gorulen araclardir.
        g.MapPut("/{key}/tools", (string key, McpToolsRequest body, IMcpService s, CancellationToken ct) => s.SetToolsAsync(key, body, ct));

        // Baglantiyi dene: runtime sunucuyu acar, araclarini listeler, kapatir (durumsuz). Runtime kapaliysa 503.
        g.MapPost("/{key}/test", (string key, IMcpService s, CancellationToken ct) => s.TestAsync(key, ct));

        return app;
    }

    /// <summary>Loopback donus adresi (CLAUDE.md §3: Api yalniz 127.0.0.1). Saglayicida kayitli adresle birebir ayni olmali.</summary>
    public static string RedirectUri(IConfiguration config)
        => new Uri(new Uri(config["AITeam:ApiUrl"] ?? "http://127.0.0.1:5080"), "/api/v1/mcp/oauth/callback").ToString();

    /// <summary>Tarayici donus sayfasi: belirtec icermez; basariliysa sekmeyi kendisi kapatmayi dener.</summary>
    private static IResult Page(string title, string body, bool ok) => Results.Content($$"""
        <!doctype html><html lang="tr"><head><meta charset="utf-8"><title>{{title}}</title>
        <style>body{font-family:system-ui,sans-serif;background:#ede9dc;color:#23283a;display:grid;place-items:center;height:100vh;margin:0}
        main{background:#fff;border:2px solid {{(ok ? "#7cc46b" : "#e0605e")}};border-radius:8px;padding:24px 28px;max-width:520px}</style></head>
        <body><main><h2>{{title}}</h2><p>{{body}}</p></main>{{(ok ? "<script>setTimeout(()=>window.close(),1500)</script>" : "")}}</body></html>
        """, "text/html; charset=utf-8", System.Text.Encoding.UTF8, ok ? 200 : 400);
}
