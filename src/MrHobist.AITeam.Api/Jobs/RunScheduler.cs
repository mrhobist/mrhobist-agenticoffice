using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Runs;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Api.Jobs;

/// <summary>
/// <see cref="IRunScheduler"/>'nun Api uygulamasi: adimi <see cref="JobChannel"/> isine cevirir. Hangi adimin hangi
/// RunService metodunu kostugu YALNIZ burada (uclar, RunResumer ve RunService'in uyandirmasi ayni kapidan gecer).
/// IRunService istek aninda cozulur: RunService bu siniifa, bu sinif RunService'e bagimlidir (dongu kurulumda degil, cagrida cozulur).
/// </summary>
public sealed class RunScheduler(JobChannel jobs, IServiceProvider services) : IRunScheduler
{
    public void Schedule(string runId, RunStep runStep)
    {
        switch (runStep)
        {
            case RunStep.Analyze:
                jobs.Enqueue(runId, $"analiz {runId}", token => Runs().AnalyzeAsync(runId, token));
                break;
            case RunStep.Dispatch:
                jobs.Enqueue(runId, $"dagitim {runId}", token => Runs().DispatchAsync(runId, token));
                break;
            default:
                break; // Approval: kullanici karar verir, is yok
        }
    }

    private IRunService Runs() => services.GetRequiredService<IRunService>();
}
