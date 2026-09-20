using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Runs;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Api.Jobs;

/// <summary>
/// Dakikada bir, yurutucusu olmayan calismalari surdurur (docs/DOMAIN.md → Butce ve limit; Paralellik):
/// <list type="bullet">
/// <item><b>Limit beklemesi</b> (Paused + ResumeAt gecmis): <see cref="IRunService.ResumeAsync"/> (kullanici tekrari sayilmaz, not organizatorden), is kuyruga.</item>
/// <item><b>Ajan bekleyen</b> (Running + WaitingSince, 2 dk'dan eski): emniyet; asil uyandirma adim kapanisinda RunService'tedir.
/// Uyandirma kacmis olsa bile calisma asili kalmaz.</item>
/// </list>
/// Kota hala doluysa ilk LLM cagrisi yine LimitReached verir ve calisma yeniden bekler.
/// </summary>
public sealed partial class RunResumer(IRunReader reader, IRunService runs, IRunScheduler scheduler, ILogger<RunResumer> logger) : BackgroundService
{
    private static readonly TimeSpan WaitingGrace = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var run in await reader.ListAsync(200, stoppingToken).ConfigureAwait(false))
                {
                    if (run.Status == RunStatus.Paused && run.ResumeAt is { } at && at <= now)
                    {
                        var resumed = await runs.ResumeAsync(run.Id, stoppingToken).ConfigureAwait(false);
                        if (resumed.Status == RunStatus.Running)
                        {
                            scheduler.Schedule(run.Id, resumed.Step ?? RunStep.Dispatch);
                            LogResumed(logger, run.Id);
                        }
                    }
                    else if (run.Status == RunStatus.Running && run.WaitingSince is { } since && now - since > WaitingGrace)
                    {
                        scheduler.Schedule(run.Id, RunStep.Dispatch);
                        LogWoken(logger, run.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "limit beklemesi bitti, calisma surduruldu: {Run}")]
    private static partial void LogResumed(ILogger logger, string run);

    [LoggerMessage(Level = LogLevel.Information, Message = "ajan bekleyen calisma yeniden dagitima kondu (emniyet): {Run}")]
    private static partial void LogWoken(ILogger logger, string run);

    [LoggerMessage(Level = LogLevel.Warning, Message = "surdurucu turu hata verdi")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
