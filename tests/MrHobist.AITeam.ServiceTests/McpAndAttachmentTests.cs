using System.IO.Compression;
using System.Text;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Mcp;
using MrHobist.AITeam.Application.Projects;
using MrHobist.AITeam.Application.Runs;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Mcp;
using MrHobist.AITeam.Domain.Projects;
using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Domain.Workflows;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>
/// 2026-09-23 kullanici istegi: is verilirken PDF/resim/belge eklenebilsin; MCP sunuculari yonetilsin, ekipteki ajanlara
/// yetki verilsin (docs/DOMAIN.md → Ekler, MCP sunuculari). Python yok: sahte runtime'a GIDEN istek dogrulanir.
/// </summary>
public sealed class McpAndAttachmentTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private const string Dev = "mcp-test-dev";

    private readonly StorageFixture _fx = new();
    private readonly RunServiceTests.FakeRuntime _runtime = new();
    private readonly MarkdownAgentStore _agents;
    private readonly IMcpStore _mcpStore;
    private readonly IAttachmentStore _files;
    private readonly McpService _mcp;
    private readonly RunService _svc;

    public McpAndAttachmentTests()
    {
        _agents = new MarkdownAgentStore(_fx.Paths);
        _mcpStore = _fx.Get<IMcpStore>();
        _files = _fx.Get<IAttachmentStore>();
        _mcp = new McpService(_mcpStore, _agents, _runtime);

        // Kendi ajani: "ajan basina tek is" kilidi surec geneli (statik); paralel kosan RunServiceTests 'developer'i
        // kullanirken bu siniftaki dagitim o ajani mesgul gorup bekliyordu (aralikli dusme, 2026-09-23).
        _agents.SaveAgentAsync(new Agent(Dev, "MCP Dev", "", ["dev"], null, null, [], null, "Sen bir developer'sin."), Ct).GetAwaiter().GetResult();

        var workflows = new JsonWorkflowStore(_fx.Paths);
        workflows.SaveAsync(new Workflow(
            "tek", "Tek kişi", 3, null,
            [
                new("analiz", "Analiz", StageKind.Analyze, Dev, "dev", ""),
                new("gelistirme", "Geliştirme", StageKind.Implement, Dev, "dev", ""),
            ],
            AskRole: Workflow.UserRole,
            PlanApprover: Workflow.AutoApprove), Ct).GetAwaiter().GetResult();
        _fx.Projects.SaveAsync(new Project("test", "Test", "", "tek", "projects/test", Project.LocalOwner, DateTimeOffset.UtcNow), Ct).GetAwaiter().GetResult();

        var scene = new RunServiceTests.FakeScene();
        _svc = new RunService(
            _fx.Runs, workflows, _agents, _fx.Projects, new RunReader(_fx.Runs),
            new AgentCaller(_agents, _runtime, _fx.Runs, scene, RetryPolicy.None, mcp: _mcpStore),
            scene, new WorkspaceLocator(_fx.Paths), new RunServiceTests.FakeScheduler(), attachments: _files);
    }

    public void Dispose() => _fx.Dispose();

    private Task<McpServerView> AddGithubAsync(string key = "gh")
        => _mcp.CreateAsync(new McpServerRequest(key, "GitHub", McpTransport.Stdio, "npx", ["-y", "@modelcontextprotocol/server-github"],
            Env: [new McpSecretInput("GITHUB_TOKEN", "ghp_gizli")]), Ct);

    private async Task<(Run Run, RuntimeTurnRequest Analyze, RuntimeTurnRequest Implement)> RunOnceAsync(IReadOnlyList<string>? attachments = null)
    {
        var run = await _svc.CreateAsync(new RunRequest("brief", Project: "test", Attachments: attachments), Ct);
        await _svc.AnalyzeAsync(run.Id, Ct);
        await _svc.DispatchAsync(run.Id, Ct);
        var analyze = _runtime.Calls.First(c => c.SchemaJson?.Contains("\"tasks\"", StringComparison.Ordinal) == true);
        var implement = _runtime.Calls.First(c => c.SchemaJson?.Contains("filesChanged", StringComparison.Ordinal) == true);
        return (await _fx.Runs.GetAsync(run.Id, Ct) ?? run, analyze, implement);
    }

    // ------------------------------------------------------------------ MCP

    [Fact]
    public async Task Sirlar_yanita_yazilmaz_ve_bos_gelen_deger_korunur()
    {
        var view = await AddGithubAsync();
        Assert.Equal([new McpSecretEntry("GITHUB_TOKEN", true)], view.Env);

        // UI sirri geri okuyamaz: degeri null gelen satir kayitli degeri korur; yeni satir eklenir.
        await _mcp.UpdateAsync("gh", new McpServerRequest("gh", "GitHub", McpTransport.Stdio, "npx", ["-y", "srv"],
            Env: [new McpSecretInput("GITHUB_TOKEN"), new McpSecretInput("LOG", "debug")]), Ct);
        var stored = await _mcpStore.GetAsync("gh", Ct);
        Assert.Equal("ghp_gizli", stored!.Env["GITHUB_TOKEN"]);
        Assert.Equal("debug", stored.Env["LOG"]);
        Assert.Equal(["-y", "srv"], stored.Args);

        // Listede olmayan ad silinir.
        await _mcp.UpdateAsync("gh", new McpServerRequest("gh", "GitHub", McpTransport.Stdio, "npx", Env: []), Ct);
        Assert.Empty((await _mcpStore.GetAsync("gh", Ct))!.Env);
    }

    [Fact]
    public async Task Http_sunucusu_adres_ister()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => _mcp.CreateAsync(new McpServerRequest("docs", "Docs", McpTransport.Http), Ct));
        Assert.Equal(ErrorCodes.McpInvalid, ex.ErrorCode);
    }

    [Fact]
    public async Task Yetki_ajan_mdsine_yazilir_ve_aracli_turda_runtimea_gider()
    {
        await AddGithubAsync();
        var view = await _mcp.SetAccessAsync("gh", new McpAccessRequest([Dev]), Ct);
        Assert.Equal([Dev], view.Agents);
        Assert.Contains("mcp: [gh]", await File.ReadAllTextAsync(Path.Combine(_fx.Paths.AgentsDir, Dev + ".md"), Ct), StringComparison.Ordinal);

        var (_, analyze, implement) = await RunOnceAsync();
        foreach (var call in new[] { analyze, implement })
        {
            var gh = Assert.Single(call.McpServers!);
            Assert.Equal("gh", gh.Key);
            Assert.Equal("stdio", gh.Value.Type);
            Assert.Equal("npx", gh.Value.Command);
            Assert.Equal("ghp_gizli", gh.Value.Env!["GITHUB_TOKEN"]);
        }

        // Yetki kaldirilinca md'den de kalkar.
        await _mcp.SetAccessAsync("gh", new McpAccessRequest([]), Ct);
        var team = await _agents.LoadTeamAsync(Ct);
        Assert.Empty(team.Agents[Dev].McpServers);
    }

    [Fact]
    public async Task Kapali_sunucu_verilmez_ve_kayda_not_duser()
    {
        await AddGithubAsync();
        await _mcp.SetAccessAsync("gh", new McpAccessRequest([Dev]), Ct);
        await _mcp.UpdateAsync("gh", new McpServerRequest("gh", "GitHub", McpTransport.Stdio, "npx", Enabled: false), Ct);

        var (run, analyze, _) = await RunOnceAsync();
        Assert.Null(analyze.McpServers);
        Assert.Contains(await _fx.Runs.ReadMessagesAsync(run.Id, Ct), m => m.Subject == "mcp" && m.Body.Contains("gh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Yetkili_sunucu_silinemez()
    {
        await AddGithubAsync();
        await _mcp.SetAccessAsync("gh", new McpAccessRequest(["developer"]), Ct);
        var ex = await Assert.ThrowsAsync<DomainException>(() => _mcp.DeleteAsync("gh", Ct));
        Assert.Equal(ErrorCodes.McpInUse, ex.ErrorCode);

        await _mcp.SetAccessAsync("gh", new McpAccessRequest([]), Ct);
        await _mcp.DeleteAsync("gh", Ct);
        Assert.Null(await _mcpStore.GetAsync("gh", Ct));
    }

    [Fact]
    public async Task Mcp_calistiramayan_saglayiciya_yetki_verilemez()
    {
        await AddGithubAsync();
        await _agents.SaveAgentAsync(new Agent("yerel", "Yerel", "", ["dev"], Provider.Ollama, "qwen", [], null, "Sen yerel bir modelsin."), Ct);
        var ex = await Assert.ThrowsAsync<DomainException>(() => _mcp.SetAccessAsync("gh", new McpAccessRequest(["developer", "yerel"]), Ct));
        Assert.Equal(ErrorCodes.AgentMcpUnsupported, ex.ErrorCode);

        // Hepsi-ya-hic: gecerli olan developer'a da yazilmadi.
        Assert.Empty((await _agents.LoadTeamAsync(Ct)).Agents["developer"].McpServers);
    }

    [Fact]
    public async Task Baglanti_denemesi_kayitli_tanimi_runtimea_iletir()
    {
        await AddGithubAsync();
        var result = await _mcp.TestAsync("gh", Ct);
        Assert.True(result.Ok);
        Assert.Equal("echo", Assert.Single(result.Tools).Name);
        Assert.Equal("npx", Assert.Single(_runtime.Probes).Command);
    }

    [Fact]
    public async Task Arac_secimi_izin_listesi_ve_gizlenen_araclar_olarak_runtimea_gider()
    {
        await AddGithubAsync();
        await _mcp.SetAccessAsync("gh", new McpAccessRequest([Dev]), Ct);

        // Baglanti denemesi gorulen araclari saklar (sahte runtime: "echo").
        await _mcp.TestAsync("gh", Ct);
        Assert.Equal("echo", Assert.Single((await _mcp.GetAsync("gh", Ct)).KnownTools!).Name);

        // Uc arac gorulmus olsun; ikisi secilsin.
        var stored = (await _mcpStore.GetAsync("gh", Ct))!;
        await _mcpStore.SaveAsync(stored with { KnownTools = [new("get_issue"), new("list_repos"), new("delete_repo")] }, Ct);
        var view = await _mcp.SetToolsAsync("gh", new McpToolsRequest(["get_issue", "list_repos"]), Ct);
        Assert.Equal(["get_issue", "list_repos"], view.Tools);
        Assert.Equal(ErrorCodes.McpInvalid, (await Assert.ThrowsAsync<DomainException>(() => _mcp.SetToolsAsync("gh", new McpToolsRequest(["yok"]), Ct))).ErrorCode);

        // Tanim duzenlemesi secimi silmez.
        await _mcp.UpdateAsync("gh", new McpServerRequest("gh", "GitHub 2", McpTransport.Stdio, "npx", Env: [new McpSecretInput("GITHUB_TOKEN")]), Ct);
        Assert.Equal(["get_issue", "list_repos"], (await _mcp.GetAsync("gh", Ct)).Tools);

        var (run, _, implement) = await RunOnceAsync();
        Assert.Equal(["get_issue", "list_repos"], implement.McpServers!["gh"].Tools);
        Assert.Equal(["mcp__gh__delete_repo"], implement.DisallowedTools);

        // Tur kaydi acilan sunucuyu tutar: kullanim raporu buradan okur.
        var turns = await _fx.Runs.ReadTurnsAsync(run.Id, Dev, Ct);
        Assert.All(turns, t => Assert.Equal(["gh"], t.McpServers));

        // Hic arac secilmediyse sunucu verilmez, kayda neden duser.
        await _mcp.SetToolsAsync("gh", new McpToolsRequest([]), Ct);
        var (run2, analyze2, _) = await RunOnceAsync();
        Assert.Null(_runtime.Calls.Last(c => c.SchemaJson?.Contains("filesChanged", StringComparison.Ordinal) == true).McpServers);
        Assert.Contains(await _fx.Runs.ReadMessagesAsync(run2.Id, Ct), m => m.Subject == "mcp" && m.Body.Contains("hiçbir araç", StringComparison.Ordinal));
        _ = analyze2;
    }

    [Fact]
    public async Task Kullanim_raporu_verilen_ve_kullanilan_turlari_arac_ve_ajan_basina_sayar()
    {
        await AddGithubAsync();
        var run = await _svc.CreateAsync(new RunRequest("b", Project: "test"), Ct);
        await _svc.CancelAsync(run.Id, Ct);
        Turn T(string agent, string[]? servers, params string[] tools) => new(
            DateTimeOffset.UtcNow, agent, "gelistirme", "t1", 1, "anthropic", "m", Destination.Anthropic, 1, 10, 10,
            ToolUses: [.. tools.Select(t => new ToolUse(t, null))], McpServers: servers);
        await _fx.Runs.AppendTurnAsync(run.Id, T("dev", ["gh"], "Read", "mcp__gh__get_issue", "mcp__gh__get_issue", "mcp__gh__list_repos"), Ct);
        await _fx.Runs.AppendTurnAsync(run.Id, T("dev", ["gh"], "Write"), Ct);                  // verildi, kullanilmadi
        await _fx.Runs.AppendTurnAsync(run.Id, T("qa", ["gh", "eski"], "mcp__eski__x"), Ct);   // silinmis sunucu
        await _fx.Runs.AppendTurnAsync(run.Id, T("dev", null, "Bash"), Ct);                     // MCP'siz tur sayilmaz

        var report = await new McpUsageReader(_fx.Runs, _mcpStore).SummarizeAsync(50, Ct);
        Assert.Equal(3, report.TurnsWithMcp);
        Assert.Equal(4, report.Calls);

        var gh = report.Servers.Single(s => s.Key == "gh");
        Assert.True(gh.Registered);
        Assert.Equal((3, 1, 3, 1), (gh.OfferedTurns, gh.UsedTurns, gh.Calls, gh.Runs));
        Assert.Equal([new McpToolUsage("get_issue", 2), new McpToolUsage("list_repos", 1)], gh.Tools);
        Assert.Equal(new McpAgentUsage("dev", 2, 3), gh.Agents.Single(a => a.Agent == "dev"));
        Assert.Equal(new McpAgentUsage("qa", 1, 0), gh.Agents.Single(a => a.Agent == "qa"));

        var old = report.Servers.Single(s => s.Key == "eski");
        Assert.False(old.Registered);
        Assert.Equal(1, old.Calls);
    }

    private sealed class OneEntryCatalog : IMcpCatalog
    {
        public Task<IReadOnlyList<McpCatalogEntry>> LoadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpCatalogEntry>>(
        [
            new("jira", "Jira", "Atlassian", "Jira kayıtları",
            [
                new("server-pat", "Server/DC PAT", McpTransport.Stdio, "uvx", ["mcp-atlassian"], Fields:
                [
                    new("JIRA_URL", "Jira adresi"),
                    new("JIRA_PERSONAL_TOKEN", "PAT", Secret: true),
                    new("READ_ONLY_MODE", "Yalnız okuma", Choices: ["true", "false"], Default: "true"),
                ]),
                new("oauth", "OAuth", McpTransport.Http, Url: "https://ornek/mcp", Supported: false),
            ]),
        ]);
    }

    [Fact]
    public async Task Katalogdan_kurulum_sirri_veritabanina_yazar_yanita_yazmaz()
    {
        var mcp = new McpService(_mcpStore, _agents, _runtime, new OneEntryCatalog());
        var view = await mcp.InstallAsync("jira", new McpInstallRequest("server-pat", new Dictionary<string, string?> { ["JIRA_URL"] = "https://jira.local", ["JIRA_PERSONAL_TOKEN"] = "pat-gizli" }), Ct);

        Assert.Equal("jira", view.Key);
        Assert.Contains(new McpSecretEntry("JIRA_PERSONAL_TOKEN", true), view.Env);
        var stored = (await _mcpStore.GetAsync("jira", Ct))!;
        Assert.Equal("pat-gizli", stored.Env["JIRA_PERSONAL_TOKEN"]);
        Assert.Equal("true", stored.Env["READ_ONLY_MODE"]); // varsayilan

        // Ayni anahtar ikinci kez kurulmaz; baska anahtarla kurulur.
        Assert.Equal(ErrorCodes.McpExists, (await Assert.ThrowsAsync<DomainException>(() => mcp.InstallAsync("jira", new McpInstallRequest("server-pat", new Dictionary<string, string?> { ["JIRA_URL"] = "u", ["JIRA_PERSONAL_TOKEN"] = "p" }), Ct))).ErrorCode);
        await mcp.InstallAsync("jira", new McpInstallRequest("server-pat", new Dictionary<string, string?> { ["JIRA_URL"] = "https://jira2.local", ["JIRA_PERSONAL_TOKEN"] = "p" }, Key: "jira-test"), Ct);

        Assert.Equal(ErrorCodes.McpInvalid, (await Assert.ThrowsAsync<DomainException>(() => mcp.InstallAsync("jira", new McpInstallRequest("oauth", Key: "jira-oauth"), Ct))).ErrorCode);
        await Assert.ThrowsAsync<Application.Common.NotFoundException>(() => mcp.InstallAsync("yok", new McpInstallRequest("x"), Ct));
        Assert.Equal(ErrorCodes.McpInvalidKey, (await Assert.ThrowsAsync<DomainException>(() => mcp.InstallAsync("jira", new McpInstallRequest("server-pat", Key: "catalog"), Ct))).ErrorCode);
    }

    // ------------------------------------------------------------------ ekler

    private static byte[] Docx(string text)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("word/document.xml");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write($"""<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>{text}</w:t></w:r><w:r><w:t xml:space="preserve"> ikinci</w:t></w:r></w:p><w:p><w:r><w:t>satır</w:t></w:r></w:p></w:body></w:document>""");
        }

        return ms.ToArray();
    }

    private async Task<StagedAttachment> StageAsync(string name, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return await _files.StageAsync(name, bytes.Length, stream, Ct);
    }

    [Fact]
    public async Task Ekler_calismaya_tasinir_isteme_yol_olarak_girer_ve_okuma_dizini_acilir()
    {
        var pdf = await StageAsync("Gereksinim Dokümanı.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 sahte"));
        var docx = await StageAsync("../../notlar.docx", Docx("Merhaba dünya"));
        Assert.Equal(AttachmentKind.Document, pdf.Kind);
        Assert.Equal("notlar.docx", docx.Name); // yol parcasi atilir

        var (run, analyze, implement) = await RunOnceAsync([pdf.Id, docx.Id]);
        var dir = _files.DirectoryOf(run.Id);
        Assert.Equal(2, run.Attachments!.Count);
        Assert.All(run.Attachments, a => Assert.True(File.Exists(Path.Combine(dir, a.FileName))));

        // Word metni cikarilip yanina yazildi; ajan onu okur.
        var word = run.Attachments.Single(a => a.Kind == AttachmentKind.Word);
        Assert.Equal("Merhaba dünya ikinci\nsatır", await File.ReadAllTextAsync(Path.Combine(dir, word.TextFile!), Ct));

        // Icerik gomulmez, yol listelenir; okuma izni ek dizinine verilir (yazma yine cwd'de).
        Assert.Contains("# Ekler", analyze.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(dir, run.Attachments[0].FileName), analyze.Messages[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("%PDF", analyze.Messages[0].Content, StringComparison.Ordinal);
        Assert.Equal([dir], analyze.ReadDirs);
        Assert.Contains("# Ekler", implement.Messages[^1].Content, StringComparison.Ordinal);
        Assert.Equal([dir], implement.ReadDirs);

        // Gecici alan bosaldi: ayni kimlik ikinci ise baglanamaz.
        var again = await Assert.ThrowsAsync<DomainException>(() => _svc.CreateAsync(new RunRequest("b", Project: "test", Attachments: [pdf.Id]), Ct));
        Assert.Equal(ErrorCodes.AttachmentNotFound, again.ErrorCode);
    }

    [Fact]
    public async Task Eksiz_iste_okuma_dizini_ve_ek_bolumu_yok()
    {
        var (_, analyze, implement) = await RunOnceAsync();
        Assert.Null(analyze.ReadDirs);
        Assert.DoesNotContain("# Ekler", implement.Messages[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bilinmeyen_ek_kimligi_isi_baslatmaz()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => _svc.CreateAsync(new RunRequest("b", Project: "test", Attachments: [new string('a', 32)]), Ct));
        Assert.Equal(ErrorCodes.AttachmentNotFound, ex.ErrorCode);
        Assert.Empty(await _fx.Runs.ListAsync(10, Ct));
    }

    [Fact]
    public async Task Desteklenmeyen_tur_diske_yazilmaz()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => StageAsync("kur.exe", [1, 2, 3]));
        Assert.Equal(ErrorCodes.AttachmentTypeUnsupported, ex.ErrorCode);
        var staging = Path.Combine(_fx.Paths.DataRoot, "attachments", "_staging");
        Assert.False(Directory.Exists(staging) && Directory.EnumerateFiles(staging).Any());
    }

    [Fact]
    public async Task Proje_silinince_ekler_de_silinir()
    {
        var png = await StageAsync("logo.png", [0x89, 0x50, 0x4E, 0x47]);
        var run = await _svc.CreateAsync(new RunRequest("brief", Project: "test", Attachments: [png.Id]), Ct);
        var dir = _files.DirectoryOf(run.Id);
        Assert.True(Directory.Exists(dir));

        await _svc.CancelAsync(run.Id, Ct);
        await _fx.Get<IProjectService>().DeleteAsync("test", deleteFiles: false, Ct);
        Assert.False(Directory.Exists(dir));
    }
}
