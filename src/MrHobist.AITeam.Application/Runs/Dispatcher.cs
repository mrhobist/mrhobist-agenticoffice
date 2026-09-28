using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Domain.Workflows;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>
/// Bir gorevin bir adima, o adimin ajanina verilmesi. <see cref="Agent"/> md anahtari (kayitlar, gecmis); <see cref="Worker"/>
/// adimi kosan kopya (kilit, sahne; docs/DOMAIN.md → Kopyalar). null = kopya 1.
/// </summary>
public sealed record Assignment(RunTask Task, Stage Stage, string Agent, string? Worker = null)
{
    public string WorkerId => Worker ?? Agent;
}

/// <summary>
/// Organizatorun dagitim kurali — KOD, sifir token (docs/DOMAIN.md → Dagitim). Saf fonksiyon: kayitlari okur,
/// atamalari doner; yazmaz, yayimlamaz. Kurallar: hazir gorev = bagimliliklari bitmis; KOPYA basina tek is (ajanin
/// <c>max_instances</c> kadar kopyasi paralel; tek kopyali ajanda eski "ajan basina tek is"); farkli ajanlar paralel;
/// sira topolojik (analist sirasi korunur). <paramref name="busyAgents"/> kopya kimlikleridir (<see cref="Workers"/>).
/// </summary>
public static class Dispatcher
{
    public static IReadOnlyList<Assignment> Plan(
        Workflow workflow,
        Spec spec,
        IReadOnlyDictionary<string, IReadOnlyList<Phase>> phasesByTask,
        IReadOnlySet<string> busyAgents,
        Func<string, int>? instancesOf = null,
        Func<string, string?>? preferredWorker = null)
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
            if (!ready || FreeWorker(next.Role, busy, instancesOf?.Invoke(next.Role) ?? 1, preferredWorker?.Invoke(next.Role)) is not { } worker)
            {
                continue;
            }

            busy.Add(worker);
            result.Add(new Assignment(task, next, next.Role, worker == next.Role ? null : worker));
        }

        return result;
    }

    /// <summary>
    /// Rolun bos kopyasi: once <paramref name="preferred"/> (ayni calismada ayni karakter surer), sonra en kucuk numarali bos kopya;
    /// hepsi doluysa null.
    /// </summary>
    public static string? FreeWorker(string role, IReadOnlySet<string> busy, int instances, string? preferred = null)
    {
        ArgumentNullException.ThrowIfNull(busy);
        var all = Workers.All(role, instances).ToList();
        if (preferred is not null && all.Contains(preferred, StringComparer.Ordinal) && !busy.Contains(preferred))
        {
            return preferred;
        }

        return all.FirstOrDefault(w => !busy.Contains(w));
    }

    /// <summary>Su anda bir fazi Started olan kopyalar (hangi gorevde oldugu fark etmez).</summary>
    public static IReadOnlySet<string> BusyAgents(IReadOnlyDictionary<string, IReadOnlyList<Phase>> phasesByTask)
    {
        ArgumentNullException.ThrowIfNull(phasesByTask);
        var busy = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phases in phasesByTask.Values)
        {
            var last = phases.Count == 0 ? null : phases[^1];
            if (last is { Status: PhaseStatus.Started })
            {
                busy.Add(last.Worker ?? last.Agent);
            }
        }

        return busy;
    }

    /// <summary>
    /// Gorevin son adimi Done ya da Skipped ise gorev bitmistir. Skipped = kullanici "bu adimi gec / elle hallettim" dedi.
    /// 2026-09-23'e kadar yalniz Done sayiliyordu: son adimi atlanan gorev ne bitmis ne dagitilabilir oluyordu, ona bagli
    /// gorevler hic hazir olmuyordu ve calisma "ajan bekleniyor" diye sonsuza dek asili kaliyordu (tek adimli akista ilk
    /// "Bu adimi gec" cevabinda yasandi).
    /// </summary>
    public static bool IsDone(IReadOnlyList<Stage> stages, IReadOnlyList<Phase> phases)
    {
        if (stages.Count == 0)
        {
            return true;
        }

        var lastStage = stages[^1];
        return phases.Any(p => p.Stage == lastStage.Id && p.Status is PhaseStatus.Done or PhaseStatus.Skipped);
    }

    /// <summary>
    /// Gorevin bir sonraki adimi (docs/DOMAIN.md → Geri donus kurali):
    /// hic faz yoksa ilk adim · son faz Done/Skipped ise ondan sonraki adim · <b>Rejected</b> ise en yakin onceki
    /// <c>implement</c> adimi (red developer'a doner; aradaki adimlar yeniden kosar) · <b>Failed</b> ise ayni adim
    /// (yeniden dene) · Started ise null (devam ediyor).
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
        var index = Workflow.IndexOf(stages, last.Stage);
        return last.Status switch
        {
            PhaseStatus.Started => null,
            PhaseStatus.Failed => index >= 0 ? stages[index] : stages[0],
            PhaseStatus.Rejected => Workflow.ProducerBefore(stages, last.Stage) ?? stages[0], // tek kaynak: Workflow
            _ => index >= 0 && index + 1 < stages.Count ? stages[index + 1] : null,
        };
    }

    private static IReadOnlyList<Phase> Phases(IReadOnlyDictionary<string, IReadOnlyList<Phase>> map, string task)
        => map.TryGetValue(task, out var p) ? p : [];
}
