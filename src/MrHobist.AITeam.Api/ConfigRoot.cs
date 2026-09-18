namespace MrHobist.AITeam.Api;

/// <summary>
/// <c>config/</c> klasorunun yeri. Once <c>AITeam:ConfigRoot</c> ayari, yoksa icerik kokunden
/// yukari dogru <c>config/workflow.json</c> araniyor (bin/Debug icinden calisirken de bulur).
/// </summary>
public sealed class ConfigRoot
{
    public ConfigRoot(IConfiguration configuration, IHostEnvironment env)
    {
        var configured = configuration["AITeam:ConfigRoot"];
        Directory = !string.IsNullOrWhiteSpace(configured)
            ? System.IO.Path.GetFullPath(configured)
            : Discover(env.ContentRootPath);
    }

    public string Directory { get; }

    public string Path(string fileName) => System.IO.Path.Combine(Directory, fileName);

    private static string Discover(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "config");
            if (File.Exists(System.IO.Path.Combine(candidate, "workflow.json")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"config/ klasoru bulunamadi ({start} ve ustu). AITeam:ConfigRoot ayarini ver.");
    }
}
