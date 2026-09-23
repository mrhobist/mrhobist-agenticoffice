using MrHobist.AITeam.Api.Jobs;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Runs;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Api.Runs;

/// <summary>
/// Calisma uclari (docs/API.md → Calismalar). Okuma <see cref="IRunReader"/>; yazma hizli dogrulanir
/// (400/409 hemen), uzun is (analiz, dagitim) <see cref="IRunScheduler"/> ile is kanalina birakilir ve 202 donulur.
/// </summary>
public static class RunEndpoints
{
    public static IEndpointRouteBuilder MapRuns(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/runs");

        g.MapGet("", (int? limit, string? project, IRunReader reader, CancellationToken ct) => reader.ListAsync(limit ?? 20, ct, project));
        // Sabit yol {id}'den once eslesir: "overview" diye bir calisma kimligi olamaz (kimlikler tarih-saat-hex).
        g.MapGet("/overview", (IRunReader reader, CancellationToken ct) => reader.GetOverviewAsync(ct));
        g.MapGet("/{id}", (string id, IRunReader reader, CancellationToken ct) => reader.GetDetailAsync(id, ct));
        g.MapGet("/{id}/turns", (string id, string? agent, IRunReader reader, CancellationToken ct) => reader.GetTurnsAsync(id, agent, ct));
        // Suren turlar: canlilik (son hareket), ajanin metni/dusuncesi, o ana kadarki kullanim ve elindeki baglam. Tur bitince bos.
        g.MapGet("/{id}/live", (string id, ProgressRegistry progress) => progress.Snapshot(id));

        // Yazma uclari: durum hemen yazilir, is Run.Step'e gore kuyruga girer (Approval → is yok). Kural RunScheduler'da, tek yerde.
        g.MapPost("", async (RunRequest body, IRunService runs, IRunScheduler scheduler, CancellationToken ct) =>
        {
            var run = await runs.CreateAsync(body, ct).ConfigureAwait(false);
            return ScheduleAndAccept(run, scheduler);
        });

        g.MapPost("/{id}/approve", async (string id, IRunService runs, IRunScheduler scheduler, CancellationToken ct)
            => ScheduleAndAccept(await runs.BeginApproveAsync(id, ct).ConfigureAwait(false), scheduler));

        g.MapPost("/{id}/revise", async (string id, ReviseRequest body, IRunService runs, IRunScheduler scheduler, CancellationToken ct)
            => ScheduleAndAccept(await runs.BeginReviseAsync(id, body.Note, ct).ConfigureAwait(false), scheduler));

        // Yeniden dene: kaldigi adimdan (analiz ya da dagitim) surer; onay bekliyorduysa yalniz durumu geri alir, kuyruga is girmez.
        g.MapPost("/{id}/retry", async (string id, IRunService runs, IRunScheduler scheduler, CancellationToken ct)
            => ScheduleAndAccept(await runs.RetryAsync(id, ct).ConfigureAwait(false), scheduler));

        // Takilma cevabi (docs/DOMAIN.md → Takilma): secim uygulanir; Running donerse dagitim kuyruga girer (iptal secildiyse Cancelled).
        g.MapPost("/{id}/answer", async (string id, AnswerRequest body, IRunService runs, IRunScheduler scheduler, CancellationToken ct)
            => ScheduleAndAccept(await runs.BeginAnswerAsync(id, body, ct).ConfigureAwait(false), scheduler));

        // Canli arac akisi: runtime suren turda her arac cagrisini buraya bildirir (belirtec = yetki, JWT yok). Bilinmeyen belirtec 204: tur bitmis.
        app.MapPost("/api/v1/progress/{token}", (string token, ProgressEvent body, ProgressRegistry registry) =>
        {
            registry.Report(token, body);
            return Results.NoContent();
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

    /// <summary>Running ise kaldigi adimin isi kuyruga girer; 202 + calisma. Running degilse (onay bekliyor, iptal) is yok.</summary>
    private static IResult ScheduleAndAccept(Run run, IRunScheduler scheduler)
    {
        if (run.Status == RunStatus.Running)
        {
            scheduler.Schedule(run.Id, run.Step ?? RunStep.Dispatch);
        }

        return Results.Accepted($"/api/v1/runs/{run.Id}", run);
    }
}
