using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace MrHobist.AITeam.Api.Scene;

/// <summary>
/// Sahne uclari. Yerlesim <c>config/scene.json</c>'dan oldugu gibi sunulur (tek dogru kaynak),
/// canli olaylar SSE ile akar, komutlar dogrulanip yayimlanir.
/// </summary>
public static class SceneEndpoints
{
    /// <summary>UI'in tanidigi olay turleri. Yeni tur sona eklenir, var olan silinmez (CLAUDE.md §5).</summary>
    public static readonly IReadOnlySet<string> EventTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "agent.state",   // { agent, state: idle|working|thinking|blocked|waiting|done, note? }
        "agent.say",     // { agent, kind: talk|ask|alert, text?, ms? }
        "agent.goto",    // { agent, spot }  -> spots[] icinden
        "agent.home",    // { agent }
        "meet",          // { from, to, kind: handoff|ask|reject, ms? }  from, to'nun yanina yurur, konusurlar, doner
        "board.set",     // { tasks: [{ id, title, stage, state: queued|active|blocked|done }] }
        "board.move",    // { task, stage, state }
        "run.stage",     // { stage, task, round }
        "cat",           // { action: sleep|wander|sit, spot? }
        "door",          // { state: closed|open }  iki kare; acilan kapi 1.4 s sonra kapanir
        "agent.leave",   // { agent }  kapiya yurur, disari cikar (sahneden kaybolur)
        "agent.enter",   // { agent }  kapidan girer, evine yurur
        "clock.set",     // { hour: 0-24 | null }  pencere manzarasi; null = gercek saat
        "cafe.special",  // { text: string | null }  kahve bari panosu; null = liste doner
    };

    public static IEndpointRouteBuilder MapScene(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1");

        g.MapGet("/scene", (ConfigRoot cfg) => ServeJson(cfg.Path("scene.json")));
        g.MapGet("/workflow", (ConfigRoot cfg) => ServeJson(cfg.Path("workflow.json")));

        g.MapGet("/scene/events", StreamEvents);

        g.MapPost("/scene/commands", async (HttpRequest req, SceneEventBus bus, CancellationToken ct) =>
        {
            // Govde elle okunur: bozuk JSON ya da gecersiz UTF-8, 500 degil 400 + errorCode dondurur
            // (LESSONS: sessiz/yanlis hata bicimi saatler kaybettirir).
            JsonDocument doc;
            try
            {
                doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return Problem("scene.command.body_invalid", $"Gövde geçerli JSON değil: {ex.Message}");
            }

            using (doc)
            {
                var body = doc.RootElement;
                if (body.ValueKind != JsonValueKind.Object
                    || !body.TryGetProperty("type", out var typeEl)
                    || typeEl.ValueKind != JsonValueKind.String)
                {
                    return Problem("scene.command.type_missing", "Gövdede 'type' dizesi yok.");
                }

                var type = typeEl.GetString()!;
                if (!EventTypes.Contains(type))
                {
                    return Problem("scene.command.type_unknown", $"Bilinmeyen olay türü: {type}");
                }

                if (!body.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object)
                {
                    return Problem("scene.command.data_missing", "Gövdede 'data' nesnesi yok.");
                }

                string json;
                try
                {
                    json = dataEl.GetRawText();
                }
                catch (InvalidOperationException)
                {
                    return Problem("scene.command.body_invalid", "Gövde geçerli UTF-8 değil.");
                }

                var evt = new SceneEvent(type, json, DateTimeOffset.UtcNow);
                bus.Publish(evt);
                return Results.Accepted(value: new { accepted = true, subscribers = bus.SubscriberCount, at = evt.At });
            }
        });

        return app;
    }

    private static IResult ServeJson(string path)
    {
        if (!File.Exists(path))
        {
            return Problem("config.file_missing", $"Yapılandırma dosyası yok: {Path.GetFileName(path)}", StatusCodes.Status404NotFound);
        }

        // Dogrulama: bozuk JSON sessizce gecmez (LESSONS: sessiz kabul en kotu hata).
        var text = File.ReadAllText(path);
        try
        {
            using var _ = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            return Problem("config.file_invalid", $"{Path.GetFileName(path)} geçersiz JSON: {ex.Message}", StatusCodes.Status500InternalServerError);
        }

        return Results.Content(text, "application/json");
    }

    private static async Task StreamEvents(HttpContext ctx, SceneEventBus bus, CancellationToken ct)
    {
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Connection = "keep-alive";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        var reader = bus.Subscribe(out var handle);
        try
        {
            await ctx.Response.WriteAsync(": connected\n\n", ct).ConfigureAwait(false);
            await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tick.CancelAfter(TimeSpan.FromSeconds(15));
                bool ready;
                try
                {
                    ready = await reader.WaitToReadAsync(tick.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Kalp atisi: ara sunucular bos baglantiyi kesmesin.
                    await ctx.Response.WriteAsync(": ping\n\n", ct).ConfigureAwait(false);
                    await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (!ready)
                {
                    break;
                }

                while (reader.TryRead(out var evt))
                {
                    await ctx.Response.WriteAsync($"event: {evt.Type}\ndata: {evt.Json}\n\n", ct).ConfigureAwait(false);
                }

                await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Istemci ayrildi; normal.
        }
        finally
        {
            bus.Unsubscribe(handle);
        }
    }

    private static IResult Problem(string errorCode, string detail, int status = StatusCodes.Status400BadRequest)
        => Results.Problem(
            detail: detail,
            statusCode: status,
            extensions: new Dictionary<string, object?> { ["errorCode"] = errorCode });
}
