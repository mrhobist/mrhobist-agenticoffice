using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Mcp;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.UnitTests;

public sealed class McpAndAttachmentRuleTests
{
    private static McpServer Server(McpTransport transport, string? command = null, string? url = null, string key = "gh")
        => new(key, "GitHub", transport, command, [], url, new Dictionary<string, string>(), new Dictionary<string, string>());

    [Fact]
    public void Stdio_komut_http_adres_ister()
    {
        Server(McpTransport.Stdio, command: "npx").Validate();
        Server(McpTransport.Http, url: "https://example.com/mcp").Validate();
        Assert.Equal(ErrorCodes.McpInvalid, Assert.Throws<DomainException>(() => Server(McpTransport.Stdio).Validate()).ErrorCode);
        Assert.Equal(ErrorCodes.McpInvalid, Assert.Throws<DomainException>(() => Server(McpTransport.Sse, url: "ftp://x").Validate()).ErrorCode);
        Assert.Equal(ErrorCodes.McpInvalidKey, Assert.Throws<DomainException>(() => Server(McpTransport.Stdio, command: "npx", key: "Git Hub").Validate()).ErrorCode);
    }

    [Fact]
    public void Mcp_yetkisi_yalniz_anthropic_ya_da_varsayilan_saglayicida()
    {
        new Agent("dev", "Dev", "", [], null, null, [], null, "p", Mcp: ["gh"]).Validate();
        new Agent("dev", "Dev", "", [], Provider.Anthropic, null, [], null, "p", Mcp: ["gh"]).Validate();
        var ex = Assert.Throws<DomainException>(() => new Agent("dev", "Dev", "", [], Provider.Nvidia, null, [], null, "p", Mcp: ["gh"]).Validate());
        Assert.Equal(ErrorCodes.AgentMcpUnsupported, ex.ErrorCode);
        new Agent("dev", "Dev", "", [], Provider.Nvidia, null, [], null, "p").Validate(); // yetkisiz ajan etkilenmez
    }

    [Theory]
    [InlineData("rapor.PDF", AttachmentKind.Document)]
    [InlineData("ekran.png", AttachmentKind.Image)]
    [InlineData("notlar.docx", AttachmentKind.Word)]
    [InlineData("veri.csv", AttachmentKind.Text)]
    public void Tur_uzantidan_belirlenir(string name, AttachmentKind kind)
        => Assert.Equal(kind, AttachmentRules.Check(name, 10).Kind);

    [Fact]
    public void Tur_bos_ve_boyut_siniri()
    {
        Assert.Equal(ErrorCodes.AttachmentTypeUnsupported, Assert.Throws<DomainException>(() => AttachmentRules.Check("kur.exe", 10)).ErrorCode);
        Assert.Equal(ErrorCodes.AttachmentEmpty, Assert.Throws<DomainException>(() => AttachmentRules.Check("a.pdf", 0)).ErrorCode);
        Assert.Equal(ErrorCodes.AttachmentTooLarge, Assert.Throws<DomainException>(() => AttachmentRules.Check("a.pdf", AttachmentRules.MaxBytes + 1)).ErrorCode);
    }

    [Fact]
    public void Guvenli_ad_yolu_ve_ozel_karakterleri_atar_cakismada_numaralar()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("gizli.pdf", AttachmentRules.SafeFileName("..\\..\\gizli.pdf", taken));
        Assert.Equal("gizli-2.pdf", AttachmentRules.SafeFileName("gizli.PDF", taken));
        Assert.Equal("a_b_c.png", AttachmentRules.SafeFileName("a|b;c.png", taken));
        Assert.Equal("ek.txt", AttachmentRules.SafeFileName("....txt", taken));
    }
}
