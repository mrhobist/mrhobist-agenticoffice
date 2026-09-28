using MrHobist.AITeam.Application.Runs;
using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Domain.Workflows;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>
/// Inceleme istemi kapinin beklettigi URETICI adima gore sekillenir. Olculdu 2026-09-21: tek istem ikisine birden
/// uymuyordu -- tasarim kapisindaki manager'a "dosyalar yazildi, build'i kos" deniyordu, kosacak sey yoktu,
/// sonra "supheyle reddet" talimatini uyguluyordu; tam-kadro 3 red verip $1.70 harcadi ve kod uretmedi.
/// </summary>
public sealed class ReviewPromptTests
{
    private static readonly RunTask Task = new("t1", "Merhaba dunya", "Konsol uygulamasi",
        Files: ["Program.cs"], Acceptance: ["dotnet run 'Merhaba, dünya!' yazar"], DependsOn: []);

    private static readonly Spec Spec = new("ozet", "mimari", Rules: ["ASCII"], Tasks: [Task]);

    private static Stage Stage(string id, StageKind kind, string role)
        => new(id, id, kind, role, "gate", $"{id} aciklamasi");

    private static string Build(Stage producer, Stage gate)
        => Prompts.ReviewTask(Spec, new Assignment(Task, gate, "manager"), @"C:\w", [], gate, producer, round: 1, maxRounds: 3);

    /// <summary>Tasarim kapisi: ortada kod YOK. Komut kosturma talimati ve "supheyle reddet" cikmamali.</summary>
    [Fact]
    public void Tasarim_kapisinda_kod_kosma_ve_supheyle_red_talimati_verilmez()
    {
        var prompt = Build(Stage("tasarim", StageKind.Design, "designer"), Stage("tasarim-onay", StageKind.Review, "manager"));

        Assert.Contains("KOD DEĞİL", prompt, StringComparison.Ordinal);
        Assert.Contains("Şüphedeyken KABUL ET", prompt, StringComparison.Ordinal);
        Assert.Contains("testsRun=false", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Şüphedeyken reddet", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("FİİLEN çalıştır", prompt, StringComparison.Ordinal); // build/test kosulmaz
        Assert.Contains("tasarim", prompt, StringComparison.Ordinal); // red hedefi istemde adiyla gecer
    }

    /// <summary>Kod kapisi: eski davranis aynen korunur -- komutlar fiilen kosulur, supheyle reddedilir.</summary>
    [Fact]
    public void Kod_kapisinda_komutlar_fiilen_kosulur_ve_supheyle_reddedilir()
    {
        var prompt = Build(Stage("gelistirme", StageKind.Implement, "developer"), Stage("test", StageKind.Review, "tester"));

        Assert.Contains("FİİLEN çalıştır", prompt, StringComparison.Ordinal);
        Assert.Contains("Şüphedeyken reddet", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Şüphedeyken KABUL ET", prompt, StringComparison.Ordinal);
    }

    /// <summary>Olcek kurali her iki kapida da gecerlidir: hello-world'e kurumsal cubuk uygulanmasin.</summary>
    [Theory]
    [InlineData(StageKind.Design, "designer")]
    [InlineData(StageKind.Implement, "developer")]
    public void Kapsamla_orantililik_her_kapida_istenir(StageKind kind, string role)
    {
        var prompt = Build(Stage("uretici", kind, role), Stage("kapi", StageKind.Review, "manager"));

        Assert.Contains("Kapsamla orantılı ol", prompt, StringComparison.Ordinal);
    }
}

/// <summary>
/// Limit beklemesinin kullaniciya yazilisi. 2026-09-22'de birebir yasandi: haftalik kota 3 gun sonrasina
/// sifirlaniyordu ama mesaj yalniz "07:00'de sürer" diyordu -- bugun sanildi.
/// </summary>
public sealed class ResumeTextTests
{
    [Fact]
    public void Bugunse_yalniz_saat_yazilir()
    {
        var at = DateTimeOffset.Now.Date.AddHours(23).AddMinutes(5);

        var text = Application.Runs.ResumeText.For(new DateTimeOffset(at, DateTimeOffset.Now.Offset));

        Assert.Equal("23:05'de sürer", text);
    }

    [Fact]
    public void Baska_gunse_tarih_de_yazilir()
    {
        var at = DateTimeOffset.Now.Date.AddDays(3).AddHours(7);

        var text = Application.Runs.ResumeText.For(new DateTimeOffset(at, DateTimeOffset.Now.Offset));

        Assert.Contains("07:00", text, StringComparison.Ordinal);
        Assert.NotEqual("07:00'de sürer", text); // saat tek basina "bugun" sanilir
    }

    [Fact]
    public void Bilinmiyorsa_soylenir() => Assert.Equal("sıfırlanma zamanı bilinmiyor", Application.Runs.ResumeText.For(null));
}
