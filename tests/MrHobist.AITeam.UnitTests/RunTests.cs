using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.UnitTests;

public sealed class RunTests
{
    private static RunTask T(string id, params string[] deps) => new(id, id, "", [], [], deps);

    [Fact]
    public void Gorevler_bagimlilik_sirasina_dizilir()
    {
        var ordered = TaskGraph.Order([T("c", "b"), T("a"), T("b", "a")]);
        Assert.Equal(["a", "b", "c"], ordered.Select(t => t.Id));
    }

    [Fact]
    public void Bilinmeyen_bagimlilik_yok_sayilir()
    {
        var ordered = TaskGraph.Order([T("a", "disarida")]);
        Assert.Equal(["a"], ordered.Select(t => t.Id));
    }

    [Fact]
    public void Dongu_patlatmaz_sira_korunur()
    {
        var ordered = TaskGraph.Order([T("x", "y"), T("y", "x"), T("z")]);
        Assert.Equal(["z", "x", "y"], ordered.Select(t => t.Id));
    }

    [Theory]
    [InlineData(Sensitivity.Local, Destination.Local, true)]
    [InlineData(Sensitivity.Local, Destination.Anthropic, false)]
    [InlineData(Sensitivity.Anthropic, Destination.Anthropic, true)]
    [InlineData(Sensitivity.Anthropic, Destination.Nvidia, false)]
    [InlineData(Sensitivity.Anthropic, Destination.Openai, false)]
    [InlineData(Sensitivity.Open, Destination.Nvidia, true)]
    [InlineData(Sensitivity.Open, Destination.Openai, true)]
    public void Hassasiyet_politikasi(Sensitivity s, Destination d, bool allowed)
    {
        Assert.Equal(allowed, SensitivityPolicy.Allows(s, d));
    }
}
