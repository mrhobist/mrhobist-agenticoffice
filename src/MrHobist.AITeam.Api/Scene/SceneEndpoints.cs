using System.Text.Json;
using MrHobist.AITeam.Api.Errors;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.Api.Scene;

/// <summary>
/// Sahne uclari. Yerlesim <c>config/scene.json</c>'dan oldugu gibi sunulur (tek dogru kaynak),
/// canli olaylar SSE ile akar, komutlar dogrulanip <see cref="ISceneEventPublisher"/> ile yayimlanir.
/// </summary>
public static class SceneEndpoints
{
    public static IEndpointRouteBuilder MapScene(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1");

        g.MapGet("/scene", (StoragePaths paths) => ServeJson(paths.ConfigFile("scene.json")));
        g.MapGet("/scene/events", StreamEvents);

        g.MapPost("/scene/commands", async (HttpRequest req, ISceneEventPublisher publisher, SceneEventBus bus, CancellationToken ct) =>
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
                if (!SceneEventTypes.All.Contains(type))
                {
                    return Problem("scene.command.type_unknown", $"Bilinmeyen olay türü: {type}");
                }

                if (!body.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object)
                {
                    return Problem("scene.command.data_missing", "Gövdede 'data' nesnesi yok.");
                }

                var at = DateTimeOffset.UtcNow;
                publisher.Publish(type, dataEl.GetRawText());
                return Results.Accepted(value: new { accepted = true, subscribers = bus.SubscriberCount, at });
            }
        });

        return app;
    }

    private static IResult ServeJson(string path)
    {
        if (!File.Exists(path))
        {
            return Problem(ErrorCodes.ConfigFileMissing, $"Yapılandırma dosyası yok: {Path.GetFileName(path)}", StatusCodes.Status404NotFound);
        }

        // Dogrulama: bozuk JSON sessizce gecmez (LESSONS: sessiz kabul en kotu hata).
        var text = File.ReadAllText(path);
        try
        {
            using var _ = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            return Problem(ErrorCodes.ConfigFileInvalid, $"{Path.GetFileName(path)} geçersiz JSON: {ex.Message}", StatusCodes.Status500InternalServerError);
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
        => ProblemMapping.Result(status, errorCode, detail);
}
