using MrHobist.AITeam.Application.Runs;
using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Domain.Workflows;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>
/// 2026-09-23 ilk gercek is frontend kosusu: tek adimli akista (analiz → gelistirme) kullanici t1'e "Bu adimi gec" dedi.
/// Skipped bitmis sayilmadigi icin t2/t4 (t1'e bagli) hic hazir olmadi; calisma "ajan bekleniyor" diye asili kaldi.
/// </summary>
public sealed class DispatcherSkipTests
{
    private static readonly Workflow Solo = new("default", "Tek", 1, null,
    [
        new Stage("analiz", "Analiz", StageKind.Analyze, "dev", "pm", ""),
        new Stage("gelistirme", "Geliştirme", StageKind.Implement, "dev", "dev", ""),
    ]);

    private static readonly Spec Plan = new("s", "a", [],
    [
        new RunTask("t1", "iskelet", "", [], [], []),
        new RunTask("t2", "ekran", "", [], [], ["t1"]),
    ]);

    private static Phase P(string task, PhaseStatus status) => new(DateTimeOffset.UtcNow, task, "gelistirme", "Geliştirme", "implement", "dev", 1, status);

    [Fact]
    public void Son_adimi_atlanan_gorev_bitmis_sayilir_bagimli_gorev_dagitilir()
    {
        var phases = new Dictionary<string, IReadOnlyList<Phase>>
        {
            ["t1"] = [P("t1", PhaseStatus.Started), P("t1", PhaseStatus.Failed), P("t1", PhaseStatus.Skipped)],
        };

        Assert.True(Dispatcher.IsDone(Solo.TaskStages, phases["t1"]));
        var next = Assert.Single(Dispatcher.Plan(Solo, Plan, phases, new HashSet<string>()));
        Assert.Equal("t2", next.Task.Id);
    }

    [Fact]
    public void Basarisiz_son_adim_bitmis_sayilmaz_ayni_adim_yeniden_dagitilir()
    {
        var phases = new Dictionary<string, IReadOnlyList<Phase>> { ["t1"] = [P("t1", PhaseStatus.Started), P("t1", PhaseStatus.Failed)] };

        Assert.False(Dispatcher.IsDone(Solo.TaskStages, phases["t1"]));
        Assert.Equal("t1", Assert.Single(Dispatcher.Plan(Solo, Plan, phases, new HashSet<string>())).Task.Id);
    }
}
