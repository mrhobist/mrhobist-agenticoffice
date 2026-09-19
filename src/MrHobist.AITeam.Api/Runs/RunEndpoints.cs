using MrHobist.AITeam.Api.Jobs;
using MrHobist.AITeam.Application.Runs;

namespace MrHobist.AITeam.Api.Runs;

/// <summary>
/// Calisma uclari (docs/API.md → Calismalar). Okuma <see cref="IRunReader"/>; yazma hizli dogrulanir
/// (400/409 hemen), uzun is (analiz, dagitim) <see cref="JobChannel"/>'a birakilir ve 202 donulur.
/// </summary>
public static class RunEndpoints
{
    public static IEndpointRouteBuilder MapRuns(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/runs");

        g.MapGet("", (int? limit, IRunReader reader, CancellationToken ct) => reader.ListAsync(limit ?? 20, ct));
        // Sabit yol {id}'den once eslesir: "overview" diye bir calisma kimligi olamaz (kimlikler tarih-saat-hex).
        g.MapGet("/overview", (IRunReader reader, CancellationToken ct) => reader.GetOverviewAsync(ct));
        g.MapGet("/{id}", (string id, IRunReader reader, CancellationToken ct) => reader.GetDetailAsync(id, ct));
        g.MapGet("/{id}/turns", (string id, string? agent, IRunReader reader, CancellationToken ct) => reader.GetTurnsAsync(id, agent, ct));

        g.MapPost("", async (RunRequest body, IRunService runs, JobChannel jobs, CancellationToken ct) =>
        {
            var run = await runs.CreateAsync(body, ct).ConfigureAwait(false);
            jobs.Enqueue(run.Id, $"analiz {run.Id}", token => runs.AnalyzeAsync(run.Id, token));
            return Results.Accepted($"/api/v1/runs/{run.Id}", run);
        });

        g.MapPost("/{id}/approve", async (string id, IRunService runs, JobChannel jobs, CancellationToken ct) =>
        {
            var run = await runs.BeginApproveAsync(id, ct).ConfigureAwait(false);
            jobs.Enqueue(run.Id, $"dagitim {run.Id}", token => runs.DispatchAsync(run.Id, token));
            return Results.Accepted($"/api/v1/runs/{run.Id}", run);
        });

        g.MapPost("/{id}/revise", async (string id, ReviseRequest body, IRunService runs, JobChannel jobs, CancellationToken ct) =>
        {
            var run = await runs.BeginReviseAsync(id, body.Note, ct).ConfigureAwait(false);
            jobs.Enqueue(run.Id, $"revize {run.Id}", token => runs.AnalyzeAsync(run.Id, token));
            return Results.Accepted($"/api/v1/runs/{run.Id}", run);
        });

        // Yeniden dene: kaldigi adimdan (analiz ya da dagitim) surer; onay bekliyorduysa yalniz durumu geri alir, kuyruga is girmez.
        g.MapPost("/{id}/retry", async (string id, IRunService runs, JobChannel jobs, CancellationToken ct) =>
        {
            var result = await runs.RetryAsync(id, ct).ConfigureAwait(false);
            if (result.Run.Status == Domain.Runs.RunStatus.Running)
            {
                jobs.Enqueue(
                    result.Run.Id,
                    result.Step == RetryStep.Analyze ? $"analiz {result.Run.Id}" : $"dagitim {result.Run.Id}",
                    token => result.Step == RetryStep.Analyze ? runs.AnalyzeAsync(result.Run.Id, token) : runs.DispatchAsync(result.Run.Id, token));
            }

            return Results.Accepted($"/api/v1/runs/{result.Run.Id}", result.Run);
        });

        // Iptal: durum hemen yazilir; suren is varsa belirteci kesilir (LLM cagrisi durur), yarim sonuc yazilmaz.
        g.MapPost("/{id}/cancel", async (string id, IRunService runs, JobChannel jobs, CancellationToken ct) =>
        {
            var run = await runs.CancelAsync(id, ct).ConfigureAwait(false);
            jobs.Cancel(id);
            return run;
        });

        return app;
    }
}
