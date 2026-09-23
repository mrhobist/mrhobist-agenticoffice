using MrHobist.AITeam.Application.Mcp;

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

        // Baglantiyi dene: runtime sunucuyu acar, araclarini listeler, kapatir (durumsuz). Runtime kapaliysa 503.
        g.MapPost("/{key}/test", (string key, IMcpService s, CancellationToken ct) => s.TestAsync(key, ct));

        return app;
    }
}
