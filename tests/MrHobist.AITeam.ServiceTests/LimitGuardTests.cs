using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Runs;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>
/// 2026-09-23 olcumu: Fable'a ozel haftalik pencere %99 "aktif"ti, tum-modeller penceresi %85 "aktif degil".
/// Eski koruma Opus cagrisini da durduruyor, %85'i hic saymiyordu.
/// </summary>
public sealed class LimitGuardTests
{
    private static RuntimeUsageLimit Window(double percent, string? scope, bool active)
        => new(scope is null ? "weekly_all" : "weekly_scoped", "weekly", percent, "warning", DateTimeOffset.UtcNow.AddDays(2), scope, active);

    [Theory]
    [InlineData(null, "claude-opus-5-5", true)]          // kapsamsiz pencere her modeli sinirlar
    [InlineData("Fable", "claude-fable-5-1", true)]      // kapsam model ailesiyle eslesir (buyuk/kucuk harf duyarsiz)
    [InlineData("Fable", "claude-opus-5-5", false)]      // baska modelin kotasi bu cagriyi durdurmaz
    [InlineData("Fable", null, true)]                    // model bilinmiyorsa temkinli: sayilir
    public void Pencere_yalniz_uygulandigi_modeli_sinirlar(string? scope, string? model, bool expected)
        => Assert.Equal(expected, LimitGuard.AppliesTo(Window(50, scope, true), model));

    [Fact]
    public void Olculen_durum_opus_icin_yalniz_tum_modeller_penceresini_sayar()
    {
        RuntimeUsageLimit[] measured = [Window(85, null, false), Window(99, "Fable", true)];
        var applied = measured.Where(l => LimitGuard.AppliesTo(l, "claude-opus-5-5")).Select(l => l.Percent);
        Assert.Equal([85d], applied); // isActive=false olsa da sayilir; Fable'in %99'u Opus'u durdurmaz
    }
}
