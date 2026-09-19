using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Workflows;

namespace MrHobist.AITeam.UnitTests;

public sealed class WorkflowTests
{
    private static Stage S(string id, StageKind kind, string role = "developer", string office = "dev")
        => new(id, id, kind, role, office, "");

    private static Workflow Valid() => new(3,
    [
        S("analiz", StageKind.Analyze, "analyst", "pm"),
        S("tasarim", StageKind.Design, "designer", "designer"),
        S("gelistirme", StageKind.Implement),
        S("test", StageKind.Review, "tester", "qa"),
    ]);

    [Fact]
    public void Gecerli_akis_dogrulanir()
    {
        var wf = Valid();
        wf.Validate();
        Assert.Equal("analiz", wf.AnalyzeStage.Id);
        Assert.Equal(3, wf.TaskStages.Count);
        Assert.Equal("gelistirme", wf.ImplementBefore(wf.Stages[3]).Id);
    }

    [Theory]
    [InlineData(0, ErrorCodes.WorkflowRoundsMin)]
    [InlineData(-1, ErrorCodes.WorkflowRoundsMin)]
    public void Tur_siniri_en_az_bir(int rounds, string code)
    {
        var wf = Valid() with { MaxReviewRounds = rounds };
        var ex = Assert.Throws<DomainException>(wf.Validate);
        Assert.Equal(code, ex.ErrorCode);
    }

    [Fact]
    public void Analyze_ilk_sirada_olmali()
    {
        var wf = new Workflow(3, [S("gelistirme", StageKind.Implement), S("analiz", StageKind.Analyze, "analyst", "pm")]);
        Assert.Equal(ErrorCodes.WorkflowAnalyzeFirst, Assert.Throws<DomainException>(wf.Validate).ErrorCode);
    }

    [Fact]
    public void Analyze_tam_bir_tane()
    {
        var wf = new Workflow(3, [S("a", StageKind.Analyze, "analyst", "pm"), S("b", StageKind.Analyze, "analyst", "pm"), S("c", StageKind.Implement)]);
        Assert.Equal(ErrorCodes.WorkflowAnalyzeCount, Assert.Throws<DomainException>(wf.Validate).ErrorCode);
    }

    [Fact]
    public void Implement_zorunlu()
    {
        var wf = new Workflow(3, [S("analiz", StageKind.Analyze, "analyst", "pm"), S("tasarim", StageKind.Design, "designer", "designer")]);
        Assert.Equal(ErrorCodes.WorkflowNoImplement, Assert.Throws<DomainException>(wf.Validate).ErrorCode);
    }

    [Fact]
    public void Review_oncesinde_implement_olmali()
    {
        var wf = new Workflow(3, [S("analiz", StageKind.Analyze, "analyst", "pm"), S("test", StageKind.Review, "tester", "qa"), S("gelistirme", StageKind.Implement)]);
        Assert.Equal(ErrorCodes.WorkflowReviewBeforeImplement, Assert.Throws<DomainException>(wf.Validate).ErrorCode);
    }

    [Fact]
    public void Yinelenen_adim_reddedilir()
    {
        var wf = new Workflow(3, [S("analiz", StageKind.Analyze, "analyst", "pm"), S("x", StageKind.Implement), S("x", StageKind.Implement)]);
        Assert.Equal(ErrorCodes.WorkflowDuplicateStage, Assert.Throws<DomainException>(wf.Validate).ErrorCode);
    }

    [Fact]
    public void Gecersiz_officeRole_reddedilir()
    {
        var wf = new Workflow(3, [S("analiz", StageKind.Analyze, "analyst", "pm"), S("x", StageKind.Implement, "developer", "ceo")]);
        Assert.Equal(ErrorCodes.WorkflowInvalidStage, Assert.Throws<DomainException>(wf.Validate).ErrorCode);
    }
}
