using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.UnitTests;

public sealed class AgentTests
{
    private static Agent A(string key, string prompt = "Sen bir roldsun.", string[]? includes = null, string? canAsk = null)
        => new(key, key, "", ["dev"], null, null, includes ?? [], canAsk, prompt);

    private static readonly string[] BaseRoles = ["analyst", "developer", "tester", "manager", "organizer"];

    private static Team TeamOf(params Agent[] agents)
    {
        var all = BaseRoles.ToDictionary(r => r, r => A(r), StringComparer.Ordinal);
        foreach (var a in agents)
        {
            all[a.Key] = a;
        }

        var knowledge = new Dictionary<string, Knowledge>(StringComparer.Ordinal)
        {
            ["mimari-kurallar"] = new("mimari-kurallar", "Mimari Kurallar", "Katmanlar ayridir."),
        };
        return new Team(all, knowledge);
    }

    [Fact]
    public void Bilesik_prompt_govde_arti_alt_mdler()
    {
        var team = TeamOf(A("developer", "Sen DEVELOPER'sin.", ["mimari-kurallar"]));
        var text = team.Agents["developer"].ComposePrompt(team.Knowledge);
        Assert.StartsWith("Sen DEVELOPER'sin.", text, StringComparison.Ordinal);
        Assert.Contains("# Mimari Kurallar", text, StringComparison.Ordinal);
        Assert.EndsWith("Katmanlar ayridir.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Eksik_alt_md_yuklenirken_yakalanir()
    {
        var team = TeamOf(A("developer", includes: ["yok-boyle-dosya"]));
        Assert.Equal(ErrorCodes.AgentUnknownInclude, Assert.Throws<DomainException>(team.Validate).ErrorCode);
    }

    [Fact]
    public void Gecersiz_can_ask_yakalanir()
    {
        var team = TeamOf(A("developer", canAsk: "ceo"));
        Assert.Equal(ErrorCodes.AgentUnknownCanAsk, Assert.Throws<DomainException>(team.Validate).ErrorCode);
    }

    [Fact]
    public void Bos_prompt_reddedilir()
    {
        Assert.Equal(ErrorCodes.AgentPromptEmpty, Assert.Throws<DomainException>(() => A("developer", "   ").Validate()).ErrorCode);
    }

    [Fact]
    public void Ekip_aciktir_eksik_rol_hata_degildir()
    {
        var team = TeamOf();
        var agents = team.Agents.Where(kv => kv.Key != "manager").ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        new Team(agents, team.Knowledge).Validate();
    }

    [Fact]
    public void Gecersiz_bilgi_anahtari_yakalanir()
    {
        var team = TeamOf();
        var knowledge = new Dictionary<string, Knowledge>(team.Knowledge, StringComparer.Ordinal)
        {
            ["Mimari Kurallar"] = new("Mimari Kurallar", "x", "y"),
        };
        var ex = Assert.Throws<DomainException>(() => new Team(team.Agents, knowledge).Validate());
        Assert.Equal(ErrorCodes.KnowledgeInvalidKey, ex.ErrorCode);
    }

    [Fact]
    public void Soranlar_bulunur()
    {
        var team = TeamOf(A("developer", canAsk: "manager"), A("tester", canAsk: "manager"));
        Assert.Equal(["developer", "tester"], team.AskersOf("manager"));
        Assert.Empty(team.AskersOf("developer"));
    }

    [Theory]
    [InlineData("developer", true)]
    [InlineData("back-end_2", true)]
    [InlineData("Developer", false)]
    [InlineData("../etc", false)]
    [InlineData("", false)]
    public void Anahtar_dosya_adina_guvenli(string key, bool ok)
    {
        Assert.Equal(ok, Identifiers.IsValidKey(key));
    }
}
