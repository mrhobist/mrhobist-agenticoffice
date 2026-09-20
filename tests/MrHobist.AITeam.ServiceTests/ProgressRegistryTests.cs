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
}
