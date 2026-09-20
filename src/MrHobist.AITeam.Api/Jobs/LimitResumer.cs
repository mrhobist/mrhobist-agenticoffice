using MrHobist.AITeam.Application.Runs;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Api.Jobs;

/// <summary>
/// Limit korumasiyla bekleyen calismalari (Paused + ResumeAt) pencere sifirlaninca surdurur (docs/DOMAIN.md → Butce ve limit).
/// Dakikada bir bakar; suresi gelen calismayi surdurur (kullanici tekrari sayilmaz, not organizatorden) ve dagitimi kuyruga koyar.
/// Kullanici ayni seyi "yeniden dene" ile erken yapabilir. Kota hala doluysa ilk LLM cagrisi yine LimitReached verir ve calisma yeniden bekler.
/// </summary>
public sealed partial class LimitResumer(IRunReader reader, IRunService runs, JobChannel jobs, ILogger<LimitResumer> logger) : BackgroundService
{
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
                        var result = await runs.ResumeAsync(run.Id, stoppingToken).ConfigureAwait(false);
                        if (result.Run.Status == RunStatus.Running)
                        {
                            jobs.Enqueue(run.Id, result.Step == RetryStep.Analyze ? $"analiz {run.Id}" : $"dagitim {run.Id}",
                                token => result.Step == RetryStep.Analyze ? runs.AnalyzeAsync(run.Id, token) : runs.DispatchAsync(run.Id, token));
                            LogResumed(logger, run.Id);
                        }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "limit surdurucu turu hata verdi")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
