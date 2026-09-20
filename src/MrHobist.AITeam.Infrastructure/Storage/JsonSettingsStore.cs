using System.Text.Json;
using System.Text.Json.Serialization;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Settings;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary><c>config/settings.json</c>: <c>{ "limitGuards": { "anthropic": 99 } }</c>. Dosya yoksa varsayilan; yazma atomik.</summary>
public sealed class JsonSettingsStore(StoragePaths paths) : ISettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Dto(Dictionary<string, int>? LimitGuards);

    private string File => paths.ConfigFile("settings.json");

    // Her LLM cagrisi oncesi okunur: dosya damgasi degismediyse onbellekten (stat ucuz, JSON ayristirma degil).
    private (DateTime Stamp, AppSettings Value)? _cache;

    public async Task<AppSettings> LoadAsync(CancellationToken ct)
    {
        if (!System.IO.File.Exists(File))
        {
            return AppSettings.Default;
        }

        var stamp = System.IO.File.GetLastWriteTimeUtc(File);
        if (_cache is { } c && c.Stamp == stamp)
        {
            return c.Value;
        }

        Dto? dto;
        try
        {
            await using var stream = System.IO.File.OpenRead(File);
            dto = await JsonSerializer.DeserializeAsync<Dto>(stream, Json, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new DomainException(ErrorCodes.ConfigFileInvalid, $"settings.json gecersiz: {ex.Message}");
        }

        var guards = new Dictionary<Provider, int>(AppSettings.Default.LimitGuards);
        foreach (var (key, value) in dto?.LimitGuards ?? [])
        {
            if (Providers.Parse(key) is { } p)
            {
                guards[p] = value;
            }
        }

        var settings = new AppSettings(guards);
        settings.Validate();
        _cache = (stamp, settings);
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var dto = new Dto(settings.LimitGuards.ToDictionary(kv => Providers.Wire(kv.Key), kv => kv.Value));
        await AtomicFile.WriteAsync(File, JsonSerializer.Serialize(dto, Json), ct).ConfigureAwait(false);
    }
}

/// <summary>Depo koku = <c>config/</c>'in ustu; proje hedef dizini ona gore cozulur. Yol depo disina cikamaz (Project.Validate).</summary>
public sealed class WorkspaceLocator(StoragePaths paths) : IWorkspaceLocator
{
    /// <summary>Klasor secicide gosterilmeyenler: bagimlilik ve derleme ciktilari, calisma gecmisi (noktayla baslayanlar da gizli).</summary>
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "__pycache__", "dist", "TestResults", "runs",
    };

    public string RootOf(Domain.Projects.Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var full = Resolve(project.TargetDir, project.Key);
        Directory.CreateDirectory(full);
        return full;
    }

    public IReadOnlyList<WorkspaceDirectory> ListDirectories(string? relativePath)
    {
        var repo = Repo();
        var rel = (relativePath ?? "").Replace('\\', '/').Trim().Trim('/');
        var full = rel.Length == 0 ? repo : Resolve(rel, "klasor");
        if (!Directory.Exists(full))
        {
            return [];
        }

        return Directory.EnumerateDirectories(full)
            .Select(d => Path.GetFileName(d)!)
            .Where(n => !n.StartsWith('.') && !Hidden.Contains(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new WorkspaceDirectory(n, rel.Length == 0 ? n : rel + "/" + n))
            .ToList();
    }

    public bool DeleteRoot(Domain.Projects.Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var full = Resolve(project.TargetDir, project.Key);
        if (!Directory.Exists(full))
        {
            return false;
        }

        Directory.Delete(full, recursive: true);
        return true;
    }

    private string Repo() => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(paths.ConfigRoot))!;

    /// <summary>Depo kokune gore yolu mutlaklar; kokun kendisi ya da disina cikan yol reddedilir.</summary>
    private string Resolve(string relative, string subject)
    {
        var repo = Repo();
        var full = Path.GetFullPath(Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException(ErrorCodes.ProjectTargetDirInvalid, $"{subject}: hedef dizin depo disina cikiyor.");
        }

        return full;
    }
}
