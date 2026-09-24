using MrHobist.AITeam.Application;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Runs;

namespace MrHobist.AITeam.ServiceTests;

public class ProgressRegistryTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData(@"C:\proj\src\App\Program.cs", "App/Program.cs")]
    [InlineData("src/App/Program.cs", "App/Program.cs")]
    [InlineData("run.cmd", "run.cmd")]
    [InlineData("cd \"C:/Users/x/proj/hello\" && dotnet build src/App", "dotnet build src/App")]
    [InlineData("cd C:/x; cd \"C:/y\" && dotnet run --project src/App", "dotnet run --project src/App")]
    [InlineData("cd \"C:/x\"", "cd \"C:/x\"")]
    [InlineData("ls -la &&\n  echo hi", "ls -la && echo hi")]
    public void Shorten_yolun_son_iki_parcasini_komutun_cd_on_ekini_atar(string? input, string? expected)
        => Assert.Equal(expected, ProgressRegistry.Shorten(input));

    [Fact]
    public void Shorten_uzun_komutu_60_karaktere_keser()
    {
        var s = ProgressRegistry.Shorten(new string('a', 100))!;
        Assert.Equal(60, s.Length);
        Assert.EndsWith("…", s);
    }

    private sealed class NullScene : ISceneEventPublisher
    {
        public List<string> Types { get; } = [];

        public void Publish(string type, string json) => Types.Add(type);
    }

    [Fact]
    public void Canli_akis_metin_dusunce_arac_ve_baglami_tutar_kullanim_mesaj_basina_son_degerle_toplanir()
    {
        var scene = new NullScene();
        var reg = new ProgressRegistry(scene);
        var token = reg.Register(new ProgressContext("r1", "dev", "t1", "gelistirme"), [new LiveContextPart("sistem · dev", "system", 3, "abc")]);
        reg.Register(new ProgressContext("r2", "dev", "t9", "gelistirme"));

        Assert.Null(reg.UsageOf(token)); // bildirim yok: tahmin yapilmaz
        reg.Report(token, new ProgressEvent(null, null, "thinking", "dizine bakayim"));
        reg.Report(token, new ProgressEvent("Read", @"C:\p\src\A.cs")); // eski govde: kind yok = arac
        reg.Report(token, new ProgressEvent(null, null, "usage", MessageId: "m1", Usage: new(1000, 10, 0, 900, 90)));
        reg.Report(token, new ProgressEvent(null, null, "usage", MessageId: "m1", Usage: new(1000, 40, 0, 900, 90))); // ayni mesaj: son deger
        reg.Report(token, new ProgressEvent(null, null, "usage", MessageId: "m2", Usage: new(2000, 5, 0, 1900, 50), Chars: 12)); // 12 kr / 3 = 4 < 5: bildirilen kalir
        reg.Report(token, new ProgressEvent(Kind: "usage", MessageId: "m3", Usage: new(0, 1, 0, 0, 0), Chars: 30)); // akistaki cikti mesaj basindaki deger: icerikten 10
        reg.Report(token, new ProgressEvent(null, null, "text", "   ")); // bos metin akisa girmez

        var live = Assert.Single(reg.Snapshot("r1"));
        Assert.Equal(["thinking", "tool"], live.Stream.Select(e => e.Kind));
        Assert.Equal("src/A.cs", live.Stream[1].Target);
        Assert.Equal(1, live.ToolCount);
        // Tepe baglam toplam degil en buyuk tek mesajdir (m2: 2000).
        Assert.Equal(new RuntimeUsage(3000, 55, 0, 2800, 140, 0, 2000), live.Usage);
        Assert.Equal(new RuntimeUsage(3000, 55, 0, 2800, 140, 0, 2000), reg.UsageOf(token));
        Assert.Equal("abc", Assert.Single(live.Context).Text);
        Assert.Equal([SceneEventTypes.AgentTool], scene.Types); // sahneye yalniz arac gider

        reg.Release(token);
        Assert.Empty(reg.Snapshot("r1"));
        Assert.Null(reg.LastSeen(token));
    }

    [Fact]
    public void Akis_ust_sinirda_eski_satirlar_duser()
    {
        var reg = new ProgressRegistry(new NullScene());
        var token = reg.Register(new ProgressContext("r1", "dev", null, null));
        for (var i = 0; i < ProgressRegistry.StreamCap + 10; i++)
        {
            reg.Report(token, new ProgressEvent(null, null, "text", $"satir {i}"));
        }

        var stream = Assert.Single(reg.Snapshot("r1")).Stream;
        Assert.Equal(ProgressRegistry.StreamCap, stream.Count);
        Assert.Equal("satir 10", stream[0].Text);
    }

    [Fact]
    public void Fiyat_tahmini_toplam_girdiden_dogrudan_payi_ayirir()
    {
        var price = new ModelPrice(4m, 20m, 0.2m, 8m);
        // 1M okuma (0.2) + 0.1M yazma (0.8) + 0.01M dogrudan (0.04) + 0.05M cikti (1.0)
        Assert.Equal(2.04m, price.Estimate(new RuntimeUsage(1_110_000, 50_000, 0, 1_000_000, 100_000)));
    }

    [Fact]
    public void Bes_dakikalik_yazma_ucuz_fiyatla_hesaplanir()
    {
        // 5 dk fiyati verilmezse standart oran: girdi x 1,25 = 5 $/M. 0.1M yazmanin 0.06M'i 5 dk: 0.04*8 + 0.06*5 = 0.62.
        var price = new ModelPrice(4m, 20m, 0.2m, 8m);
        Assert.Equal(5m, price.CacheWriteShort);
        Assert.Equal(0.62m, price.Estimate(100_000, 0, 0, 100_000, 60_000));
        Assert.Equal(0.8m, price.Estimate(100_000, 0, 0, 100_000)); // kirilim yok: hepsi 1 sa (bugunku davranis)
        Assert.Equal(0.5m, price.Estimate(100_000, 0, 0, 100_000, 500_000)); // pay yazmayi asamaz
        Assert.Equal(0.3m, new ModelPrice(4m, 20m, 0.2m, 8m, 3m).Estimate(100_000, 0, 0, 100_000, 100_000)); // tabloda verilen fiyat kazanir
    }

    /// <summary>
    /// 2026-09-23: Api JSON'u zorunlu kurucu parametresine uyar; runtime bos alani gondermez. Tool/Target zorunluyken metin ve
    /// kullanim govdeleri 400 aliyor, ekranda yalniz araclar gorunuyordu. Runtime'in gercek govdeleri Api ayariyla okunmali.
    /// </summary>
    [Theory]
    [InlineData("""{"kind":"usage","messageId":"m1","chars":19,"usage":{"inputTokens":10,"outputTokens":2,"reasoningChars":0,"cacheReadTokens":5,"cacheWriteTokens":3}}""")]
    [InlineData("""{"kind":"text","text":"merhaba"}""")]
    [InlineData("""{"kind":"tool","tool":"Glob"}""")]
    [InlineData("""{"tool":"Read","target":"a.cs"}""")]
    public void Runtime_govdeleri_Api_json_ayariyla_okunur(string json)
        => Assert.NotNull(System.Text.Json.JsonSerializer.Deserialize<ProgressEvent>(json, ApiJson));

    /// <summary>Api'nin <c>ConfigureHttpJsonOptions</c> ayari (Program.cs) ile ayni katilik.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions ApiJson = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };
}
