using System.Text;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Mcp;

namespace MrHobist.AITeam.UnitTests;

public sealed class McpCatalogTests
{
    private static McpCatalogEntry Entry(params McpAuthOption[] options) => new("ornek", "Örnek", "satıcı", "açıklama", options);

    private static Dictionary<string, string?> V(params (string Key, string? Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Basic_basligi_iki_alandan_base64_uretilir()
    {
        var option = new McpAuthOption("basic", "Basic", McpTransport.Http, Url: "https://ornek/mcp", Fields:
        [
            new("EMAIL", "E-posta", McpFieldTarget.Input),
            new("TOKEN", "Token", McpFieldTarget.Header, Secret: true, Header: "Authorization", Format: "Basic {base64:EMAIL:TOKEN}"),
        ]);
        var server = McpCatalogBuilder.Build(Entry(option), option, "jira", null, V(("EMAIL", "a@b.com"), ("TOKEN", "t0k")));
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("a@b.com:t0k")), server.Headers["Authorization"]);
        Assert.Empty(server.Env);
        Assert.Equal("Örnek", server.Name);
    }

    [Fact]
    public void Bos_istege_bagli_alan_yazilmaz_ona_dayanan_arguman_atilir_varsayilan_kullanilir()
    {
        var option = new McpAuthOption("x", "X", McpTransport.Stdio, "cmd", ["/c", "npx", "paket", "--key={KEY}", "--browser={BROWSER}"], Fields:
        [
            new("KEY", "Anahtar", McpFieldTarget.Input, Required: false),
            new("BROWSER", "Tarayıcı", McpFieldTarget.Input, Default: "msedge", Choices: ["msedge", "chrome"]),
            new("TOKEN", "Token", McpFieldTarget.Header, Required: false, Format: "Bearer {value}"),
        ]);
        var server = McpCatalogBuilder.Build(Entry(option), option, "pw", "Playwright", V());
        Assert.Equal(["/c", "npx", "paket", "--browser=msedge"], server.Args);
        Assert.Empty(server.Headers);
    }

    [Fact]
    public void Zorunlu_alan_gecersiz_secim_ve_desteklenmeyen_secenek_reddedilir()
    {
        var option = new McpAuthOption("x", "X", McpTransport.Stdio, "npx", Fields: [new("MODE", "Mod", Choices: ["true", "false"])]);
        Assert.Equal(ErrorCodes.McpInvalid, Assert.Throws<DomainException>(() => McpCatalogBuilder.Build(Entry(option), option, "k", null, V())).ErrorCode);
        Assert.Equal(ErrorCodes.McpInvalid, Assert.Throws<DomainException>(() => McpCatalogBuilder.Build(Entry(option), option, "k", null, V(("MODE", "belki")))).ErrorCode);

        var oauth = new McpAuthOption("oauth", "OAuth", McpTransport.Http, Url: "https://ornek/mcp", Supported: false);
        Assert.Equal(ErrorCodes.McpInvalid, Assert.Throws<DomainException>(() => McpCatalogBuilder.Build(Entry(oauth), oauth, "k", null, V())).ErrorCode);
    }

    [Fact]
    public void Katalog_bilinmeyen_alana_basvuran_ve_siri_argumana_yazan_secenegi_reddeder()
    {
        var unknown = new McpAuthOption("a", "A", McpTransport.Stdio, "npx", ["--x={YOK}"]);
        Assert.Equal(ErrorCodes.ConfigFileInvalid, Assert.Throws<DomainException>(() => McpCatalogBuilder.Validate([Entry(unknown)])).ErrorCode);

        var leak = new McpAuthOption("b", "B", McpTransport.Stdio, "npx", ["--api-key", "{KEY}"], Fields: [new("KEY", "Anahtar", McpFieldTarget.Input, Secret: true)]);
        Assert.Equal(ErrorCodes.ConfigFileInvalid, Assert.Throws<DomainException>(() => McpCatalogBuilder.Validate([Entry(leak)])).ErrorCode);
    }
}
