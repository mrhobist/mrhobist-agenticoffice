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

    private static readonly Spec Three = new("s", "a", [], [new RunTask("t1", "a", "", [], [], []), new RunTask("t2", "b", "", [], [], []), new RunTask("t3", "c", "", [], [], [])]);

    /// <summary>
    /// Kopyalar (docs/DOMAIN.md → Kopyalar): rolun bos kopyasi secilir; kopya 1 baska calismada mesgulse kopya 2 alir. Tek kopyali
    /// ajanda eski kural (ajan basina tek is) aynen: ilk hazir gorev, rol doluysa hic.
    /// </summary>
    [Fact]
    public void Bos_kopya_secilir_dolu_kopyalar_atlanir_tek_kopyada_eski_kural()
    {
        var none = new Dictionary<string, IReadOnlyList<Phase>>();

        var others = Dispatcher.Plan(Solo, Three, none, new HashSet<string> { "dev" }, _ => 3);
        Assert.Equal(["dev~2", "dev~3"], others.Select(a => a.WorkerId)); // kopya 1 dolu: 2 ve 3 is alir
        Assert.All(others, a => Assert.Equal("dev", a.Agent)); // kayit md anahtariyla

        var parallel = Dispatcher.Plan(Solo, Three, none, new HashSet<string>(), _ => 3);
        Assert.Equal(["dev", "dev~2", "dev~3"], parallel.Select(a => a.WorkerId));
        Assert.Null(parallel[0].Worker); // kopya 1 kayitta ajanin kendisi

        Assert.Empty(Dispatcher.Plan(Solo, Three, none, new HashSet<string> { "dev" })); // tek kopya, dolu
        Assert.Empty(Dispatcher.Plan(Solo, Three, none, new HashSet<string> { "dev", "dev~2" }, _ => 2));

        // Ayni calisma ayni karakterle surer: tercih edilen kopya bossa o.
        Assert.Equal("dev~3", Dispatcher.Plan(Solo, Three, none, new HashSet<string>(), _ => 3, _ => "dev~3")[0].WorkerId);
    }

    [Fact]
    public void Mesgul_kopya_fazdaki_kopya_kimliginden_okunur()
    {
        var phases = new Dictionary<string, IReadOnlyList<Phase>> { ["t1"] = [P("t1", PhaseStatus.Started) with { Worker = "dev~2" }] };
        Assert.Equal(["dev~2"], Dispatcher.BusyAgents(phases));
    }
}
