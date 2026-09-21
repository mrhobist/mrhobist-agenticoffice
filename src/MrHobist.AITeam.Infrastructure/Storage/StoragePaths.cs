namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary>
/// Iki kok, iki sorumluluk (CLAUDE.md §2):
/// <c>config/</c> ajan md'leri, bilgi md'leri, is akislari ve sahne yerlesimi -- git'te, elle duzenlenir;
/// <c>data/</c> calisma zamani durumu -- <c>aiteam.db</c> (SQLite), gitignore'da.
/// Ayar verilmezse icerik kokunden yukari dogru <c>config/workflows/</c> aranir; bin/Debug icinden de bulur.
/// </summary>
public sealed record StoragePaths(string ConfigRoot, string DataRoot)
{
    public string AgentsDir => Path.Combine(ConfigRoot, "agents");

    public string KnowledgeDir => Path.Combine(ConfigRoot, "knowledge");

    public string WorkflowsDir => Path.Combine(ConfigRoot, "workflows");

    public string WorkflowFile(string key) => Path.Combine(WorkflowsDir, key + ".json");

    public string ConfigFile(string fileName) => Path.Combine(ConfigRoot, fileName);

    /// <summary>Tek veritabani siniri. Proje, calisma, tur, mesaj, faz ve ayarlar burada.</summary>
    public string DatabaseFile => Path.Combine(DataRoot, "aiteam.db");

    /// <summary>Depo koku: <c>config/</c>'in ustu. <c>runtime/</c> ve <c>scripts/</c> buradan bulunur.</summary>
    public string RepoRoot => Path.GetDirectoryName(ConfigRoot)!;

    public static StoragePaths Discover(string start, string? configRoot = null, string? dataRoot = null)
    {
        var config = string.IsNullOrWhiteSpace(configRoot) ? FindConfig(start) : Path.GetFullPath(configRoot);
        var data = string.IsNullOrWhiteSpace(dataRoot)
            ? Path.Combine(Path.GetDirectoryName(config)!, "data")
            : Path.GetFullPath(dataRoot);
        return new StoragePaths(config, data);
    }

    private static string FindConfig(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config");
            if (Directory.Exists(Path.Combine(candidate, "workflows")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"config/ klasoru bulunamadi ({start} ve ustu). AITeam:ConfigRoot ayarini ver.");
    }
}
