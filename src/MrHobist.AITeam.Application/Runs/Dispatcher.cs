using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Domain.Workflows;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>Bir gorevin bir adima, o adimin ajanina verilmesi.</summary>
public sealed record Assignment(RunTask Task, Stage Stage, string Agent);

/// <summary>
/// Organizatorun dagitim kurali — KOD, sifir token (docs/DOMAIN.md → Dagitim). Saf fonksiyon: kayitlari okur,
/// atamalari doner; yazmaz, yayimlamaz. Kurallar: hazir gorev = bagimliliklari bitmis; ajan basina tek is;
/// farkli ajanlar paralel; sira topolojik (analist sirasi korunur).
/// </summary>
public static class Dispatcher
{
    public static IReadOnlyList<Assignment> Plan(
        Workflow workflow,
        Spec spec,
        IReadOnlyDictionary<string, IReadOnlyList<Phase>> phasesByTask,
        IReadOnlySet<string> busyAgents)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(phasesByTask);
        ArgumentNullException.ThrowIfNull(busyAgents);

        var stages = workflow.TaskStages;
        var busy = new HashSet<string>(busyAgents, StringComparer.Ordinal);
        var ordered = TaskGraph.Order(spec.Tasks);
        var done = ordered.Where(t => IsDone(stages, Phases(phasesByTask, t.Id))).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var known = ordered.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

        var result = new List<Assignment>();
        foreach (var task in ordered)
        {
            if (done.Contains(task.Id))
            {
                continue;
            }

            var phases = Phases(phasesByTask, task.Id);
            var next = NextStage(stages, phases);
            if (next is null)
            {
                continue; // devam ediyor (Started) ya da takildi (Failed/Rejected): dagitici karismaz
            }

            var ready = task.DependsOn.All(d => done.Contains(d) || !known.Contains(d));
            if (!ready || busy.Contains(next.Role))
            {
                continue;
            }

            busy.Add(next.Role);
            result.Add(new Assignment(task, next, next.Role));
        }

        return result;
    }

    /// <summary>Su anda bir fazi Started olan ajanlar (hangi gorevde oldugu fark etmez).</summary>
    public static IReadOnlySet<string> BusyAgents(IReadOnlyDictionary<string, IReadOnlyList<Phase>> phasesByTask)
    {
        ArgumentNullException.ThrowIfNull(phasesByTask);
        var busy = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phases in phasesByTask.Values)
        {
            var last = phases.Count == 0 ? null : phases[^1];
            if (last is { Status: PhaseStatus.Started })
            {
                busy.Add(last.Agent);
            }
        }

        return busy;
    }

    /// <summary>Gorevin son adimi Done ise gorev bitmistir.</summary>
    public static bool IsDone(IReadOnlyList<Stage> stages, IReadOnlyList<Phase> phases)
    {
        if (stages.Count == 0)
        {
            return true;
        }

        var lastStage = stages[^1];
        return phases.Any(p => p.Stage == lastStage.Id && p.Status == PhaseStatus.Done);
    }

    /// <summary>
    /// Gorevin bir sonraki adimi: hic faz yoksa ilk adim; son faz Done ise ondan sonraki adim;
    /// Started/Failed/Rejected ise null (dagitim yapilmaz — o durumlarin kurali ayri karardir).
    /// </summary>
    public static Stage? NextStage(IReadOnlyList<Stage> stages, IReadOnlyList<Phase> phases)
    {
        if (stages.Count == 0)
        {
            return null;
        }

        if (phases.Count == 0)
        {
            return stages[0];
        }

        var last = phases[^1];
        if (last.Status != PhaseStatus.Done && last.Status != PhaseStatus.Skipped)
        {
            return null;
        }

        var index = -1;
        for (var i = 0; i < stages.Count; i++)
        {
            if (stages[i].Id == last.Stage)
            {
                index = i;
                break;
            }
        }

        return index >= 0 && index + 1 < stages.Count ? stages[index + 1] : null;
    }

    private static IReadOnlyList<Phase> Phases(IReadOnlyDictionary<string, IReadOnlyList<Phase>> map, string task)
        => map.TryGetValue(task, out var p) ? p : [];
}
