using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Projects;

namespace MrHobist.AITeam.UnitTests;

/// <summary>Dil seridi ve iceri alma onerisi (docs/DOMAIN.md → Projeyi iceri alma): saf siniflama.</summary>
public sealed class CodebaseTests
{
    private static SourceFile F(string path, long bytes) => new(path, bytes);

    [Fact]
    public void Dil_payi_baytla_olculur_veri_ve_duzyazi_sayilmaz()
    {
        var code = Codebase.Measure(
        [
            F("src/Api/Program.cs", 6000),
            F("src/Api/Api.csproj", 400),
            F("ui/app/app.vue", 3000),
            F("ui/app/api/client.ts", 1000),
            F("README.md", 9000),        // duzyazi
            F("ui/package.json", 800),   // veri
            F("config/scene.json", 50_000),
        ]);

        Assert.Equal(["C#", "Vue", "TypeScript"], code.Languages.Select(l => l.Name));
        Assert.Equal([60.0, 30.0, 10.0], code.Languages.Select(l => l.Percent));
        Assert.Equal("#178600", code.Languages[0].Color); // linguist rengi: kullanici GitHub'da gordugunu gorsun
        Assert.Equal(10_000, code.Bytes);
        Assert.Equal(7, code.Files);
        Assert.Equal(["src/Api/Api.csproj", "ui/package.json"], code.Manifests.Order()); // en cok iki seviye
        Assert.False(code.IsEmpty);
    }

    [Fact]
    public void Ucuncu_taraf_uretilmis_ve_belge_dosyalari_elenir()
    {
        var code = Codebase.Measure(
        [
            F("app.py", 100),
            F("node_modules/react/index.js", 1_000_000),
            F("static/vendor/lib.js", 1_000_000),
            F("wwwroot/lib/jquery/dist/jquery.js", 1_000_000),
            F("web/site.min.js", 1_000_000),
            F("web/jquery-3.7.1.js", 1_000_000),
            F("Forms/Main.Designer.cs", 1_000_000),
            F("Proto/api.pb.go", 1_000_000),
            F("docs/conf.py", 1_000_000),
            F("pkg/examples/demo.py", 1_000_000),
        ]);

        Assert.Equal(("Python", 100.0), (code.Languages.Single().Name, code.Languages.Single().Percent));
        Assert.Equal(1, code.Files);
        Assert.True(Codebase.IsExcluded("a/node_modules/b.js"));
        Assert.False(Codebase.IsExcluded("src/docs/page.ts")); // docs yalniz kokte belgedir
    }

    [Fact]
    public void Baslik_dosyasi_komsularina_gore_dile_duser()
    {
        Assert.Equal("C++", Codebase.Measure([F("a.cpp", 100), F("a.h", 100)]).Languages.Single().Name);
        Assert.Equal("C", Codebase.Measure([F("a.c", 100), F("a.h", 100)]).Languages.Single().Name);
        Assert.Equal("C", Codebase.Measure([F("only.h", 100)]).Languages.Single().Name);
        Assert.Equal(["Dockerfile", "Makefile"], Codebase.Measure([F("Dockerfile.prod", 10), F("Makefile", 10)]).Languages.Select(l => l.Name).Order());
    }

    [Fact]
    public void Bos_ya_da_yalniz_belge_olan_dizin_bostur()
    {
        Assert.True(Codebase.Measure([]).IsEmpty);
        Assert.True(Codebase.Measure([F("notlar.md", 100), F("data.json", 10)]).IsEmpty);
        Assert.False(Codebase.Measure([F("package.json", 10)]).IsEmpty); // manifest tek basina kurulu bir proje demek
    }

    [Fact]
    public void Dil_satiri_turkce_ondalikla_ve_diger_payla_yazilir()
    {
        var code = Codebase.Measure([F("a.cs", 621), F("b.ts", 300), F("c.css", 50), F("d.html", 29)]);
        Assert.Equal("C# %62,1 · TypeScript %30", code.LanguageLine(2).Split(" · diğer")[0]);
        Assert.Equal("C# %62,1 · TypeScript %30 · diğer %7,9", code.LanguageLine(2));
        Assert.Equal("C# %62,1 · TypeScript %30 · CSS %5 · HTML %2,9", code.LanguageLine());
    }

    [Fact]
    public void Oneri_package_json_ve_README_den_gelir_yoksa_klasor_adindan()
    {
        const string readme = """
            # Anket Uygulaması

            [![build](https://x/badge.svg)](https://x)

            Ekiplerin haftalık nabız anketini toplayan küçük bir web uygulaması.
            İkinci satır da paragrafa dahil.

            ## Kurulum
            """;
        var (title, desc) = ProjectSuggestion.From("anket-app", """{ "name": "@acme/anket-app", "description": "" }""", readme);
        Assert.Equal("Anket Uygulaması", title);
        Assert.Equal("Ekiplerin haftalık nabız anketini toplayan küçük bir web uygulaması. İkinci satır da paragrafa dahil.", desc);

        (title, desc) = ProjectSuggestion.From("anket-app", """{ "name": "@acme/anket-app", "description": "Nabız anketi" }""", null);
        Assert.Equal(("Anket app", "Nabız anketi"), (title, desc));

        (title, desc) = ProjectSuggestion.From("my_tool", "{ bozuk json", null);
        Assert.Equal(("My tool", ""), (title, desc));
    }

    [Fact]
    public void Anahtar_klasor_adindan_gecerli_bicimde_uretilir()
    {
        Assert.Equal("musteri-portali-v2", ProjectSuggestion.Key("Müşteri Portalı V2"));
        Assert.Equal("istanbul-app", ProjectSuggestion.Key("İstanbul.App"));
        Assert.Equal("proje", ProjectSuggestion.Key("___"));
        Assert.True(Identifiers.IsValidKey(ProjectSuggestion.Key(new string('a', 80))));
        Assert.Equal(40, ProjectSuggestion.Key(new string('a', 80)).Length);
    }
}
