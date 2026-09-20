namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary>
/// Dosya deposunun iki koku: <c>config/</c> (md + json, git'te) ve <c>runs/</c> (JSONL, gitignore'da).
/// Ayar verilmezse icerik kokunden yukari dogru <c>config/workflows/</c> aranir; bin/Debug icinden de bulur.
/// </summary>
public sealed record StoragePaths(string ConfigRoot, string RunsRoot)
{
    public string AgentsDir => Path.Combine(ConfigRoot, "agents");

    public string KnowledgeDir => Path.Combine(ConfigRoot, "knowledge");

    public string WorkflowsDir => Path.Combine(ConfigRoot, "workflows");

    public string WorkflowFile(string key) => Path.Combine(WorkflowsDir, key + ".json");

    public string ProjectsDir => Path.Combine(ConfigRoot, "projects");

    public string ProjectFile(string key) => Path.Combine(ProjectsDir, key + ".json");

    public string ConfigFile(string fileName) => Path.Combine(ConfigRoot, fileName);

    /// <summary>Depo koku: <c>config/</c>'in ustu. <c>runtime/</c> ve <c>scripts/</c> buradan bulunur.</summary>
    public string RepoRoot => Path.GetDirectoryName(ConfigRoot)!;

    public static StoragePaths Discover(string start, string? configRoot = null, string? runsRoot = null)
    {
        var config = string.IsNullOrWhiteSpace(configRoot) ? FindConfig(start) : Path.GetFullPath(configRoot);
        var runs = string.IsNullOrWhiteSpace(runsRoot)
            ? Path.Combine(Path.GetDirectoryName(config)!, "runs")
            : Path.GetFullPath(runsRoot);
        return new StoragePaths(config, runs);
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
