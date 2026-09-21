using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Workflows;

namespace MrHobist.AITeam.UnitTests;

public sealed class WorkflowTests
{
    private static Stage S(string id, StageKind kind, string role = "developer", string office = "dev")
        => new(id, id, kind, role, office, "");

    private static Workflow Wf(params Stage[] stages) => new("default", "Varsayilan", 3, null, stages);

    private static Workflow Valid() => new("default", "Varsayilan", 3, "organizer",
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
        Assert.True(wf.IsDefault);
        Assert.Equal(3, wf.TaskStages.Count);
        Assert.Equal("gelistirme", wf.ProducerBefore(wf.Stages[3]).Id);
        Assert.Equal(["analyst", "designer", "developer", "tester", "organizer"], wf.Roles);
    }

    [Fact]
    public void Review_tasarimi_reddederse_tasarimciya_doner()
    {
        // 2026-09-21 genellemesi: geri donus "en yakin onceki URETICI adim" (design | implement).
        // Onceden yalniz implement araniyordu, dolayisiyla tasarim kapisi ya kurulamiyor ya yanlis hedefe donuyordu.
        var wf = new Workflow("tam-kadro", "Tam kadro", 3, null,
        [
            S("analiz", StageKind.Analyze, "analyst", "pm"),
            S("tasarim", StageKind.Design, "designer", "designer"),
            S("tasarim-onay", StageKind.Review, "manager", "gate"),
            S("gelistirme", StageKind.Implement),
            S("test", StageKind.Review, "tester", "qa"),
        ]);
        wf.Validate();

        Assert.Equal("tasarim", wf.ProducerBefore(wf.Stages[2]).Id);   // tasarim reddi → tasarimciya
        Assert.Equal("gelistirme", wf.ProducerBefore(wf.Stages[4]).Id); // kod reddi → developer'a
    }

    [Fact]
    public void Review_oncesinde_uretici_adim_yoksa_reddedilir()
    {
        var wf = Wf(S("analiz", StageKind.Analyze, "analyst", "pm"), S("kapi", StageKind.Review, "manager", "gate"), S("gelistirme", StageKind.Implement));
        var ex = Assert.Throws<DomainException>(wf.Validate);
        Assert.Equal(ErrorCodes.WorkflowReviewBeforeImplement, ex.ErrorCode);
    }

    [Fact]
    public void Soru_hedefi_ve_plan_onaylayani_akista_tutulur()
    {
        // Kullanici degerleri ekipte aranmaz; ajan anahtarlari aranir (akista olmayan ajan ise sokulmasin).
        var user = Valid() with { AskRole = Workflow.UserRole, PlanApprover = Workflow.UserRole };
        user.Validate();
        Assert.Null(user.PlanApproverAgent);
        Assert.DoesNotContain(Workflow.UserRole, user.Roles);

        var byManager = Valid() with { AskRole = "manager", PlanApprover = "manager" };
        byManager.Validate();
        Assert.Equal("manager", byManager.PlanApproverAgent);
        Assert.Contains("manager", byManager.Roles);

        var bad = Valid() with { AskRole = "Gecersiz Anahtar" };
        Assert.Equal(ErrorCodes.WorkflowInvalidStage, Assert.Throws<DomainException>(bad.Validate).ErrorCode);
    }

    [Fact]
    public void Eski_akis_yeni_alanlar_olmadan_gecerli()
    {
        // Geriye uyum: alanlar yoksa null = eski davranis (ajanin can_ask'i, plani kullanici onaylar).
        var wf = Valid();
        wf.Validate();
        Assert.Null(wf.AskRole);
        Assert.Null(wf.PlanApprover);
        Assert.Null(wf.PlanApproverAgent);
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
        var wf = Wf(S("gelistirme", StageKind.Implement), S("analiz", StageKind.Analyze, "analyst", "pm"));
        Assert.Equal(ErrorCodes.WorkflowAnalyzeFirst, Assert.Throws<DomainException>(wf.Validate).ErrorCode);
    }

    [Fact]
    public void Analyze_tam_bir_tane()
    {
        var wf = Wf(S("a", StageKind.Analyze, "analyst", "pm"), S("b", StageKind.Analyze, "analyst", "pm"), S("c", StageKind.Implement));
        Assert.Equal(ErrorCodes.WorkflowAnalyzeCount, Assert.Throws<DomainException>(wf.Validate).ErrorCode);
    }

    [Fact]
    public void Implement_zorunlu()
    {
        var wf = Wf(S("analiz", StageKind.Analyze, "analyst", "pm"), S("tasarim", StageKind.Design, "designer", "designer"));
        Assert.Equal(ErrorCodes.WorkflowNoImplement, Assert.Throws<DomainException>(wf.Validate).ErrorCode);
    }

    [Fact]
    public void Review_oncesinde_implement_olmali()
    {
        var wf = Wf(S("analiz", StageKind.Analyze, "analyst", "pm"), S("test", StageKind.Review, "tester", "qa"), S("gelistirme", StageKind.Implement));
        Assert.Equal(ErrorCodes.WorkflowReviewBeforeImplement, Assert.Throws<DomainException>(wf.Validate).ErrorCode);
    }

    [Fact]
    public void Yinelenen_adim_reddedilir()
    {
        var wf = Wf(S("analiz", StageKind.Analyze, "analyst", "pm"), S("x", StageKind.Implement), S("x", StageKind.Implement));
        Assert.Equal(ErrorCodes.WorkflowDuplicateStage, Assert.Throws<DomainException>(wf.Validate).ErrorCode);
    }

    [Fact]
    public void Gecersiz_officeRole_reddedilir()
    {
        var wf = Wf(S("analiz", StageKind.Analyze, "analyst", "pm"), S("x", StageKind.Implement, "developer", "ceo"));
        Assert.Equal(ErrorCodes.WorkflowInvalidStage, Assert.Throws<DomainException>(wf.Validate).ErrorCode);
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("")]
    public void Gecersiz_akis_anahtari_reddedilir(string key)
    {
        var wf = Valid() with { Key = key };
        Assert.Equal(ErrorCodes.WorkflowInvalidStage, Assert.Throws<DomainException>(wf.Validate).ErrorCode);
    }

    [Fact]
    public void Ekipte_olmayan_rol_yakalanir()
    {
        var team = new Team(
            new Dictionary<string, Agent>(StringComparer.Ordinal)
            {
                ["analyst"] = new("analyst", "A", "", [], null, null, [], null, "p"),
                ["developer"] = new("developer", "D", "", [], null, null, [], null, "p"),
                ["tester"] = new("tester", "T", "", [], null, null, [], null, "p"),
            },
            new Dictionary<string, Knowledge>(StringComparer.Ordinal));
        var ex = Assert.Throws<DomainException>(() => Valid().ValidateAgainst(team));
        Assert.Equal(ErrorCodes.WorkflowUnknownRole, ex.ErrorCode);
        Assert.Contains("designer", ex.Message, StringComparison.Ordinal);
        Assert.Contains("organizer", ex.Message, StringComparison.Ordinal);
    }
}
