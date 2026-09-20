using System.Text.Json;
using System.Text.Json.Serialization;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Common;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Projects;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary><c>config/projects/{key}.json</c>, dosya basina bir proje; camelCase, atomik yazim. Depo durum tutmaz.</summary>
public sealed class JsonProjectStore(StoragePaths paths) : IProjectStore
{
    private sealed record ProjectDto(string? Title, string? Description, string? Workflow, string? TargetDir, string? OwnerId, DateTimeOffset? CreatedAt, string? Color = null, int? Order = null);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct)
    {
        if (!Directory.Exists(paths.ProjectsDir))
        {
            return [];
        }

        var list = new List<Project>();
        foreach (var file in Directory.EnumerateFiles(paths.ProjectsDir, "*.json").Order(StringComparer.Ordinal))
        {
            var key = Path.GetFileNameWithoutExtension(file);
            if (Identifiers.IsValidKey(key))
            {
                list.Add(await LoadAsync(key, ct).ConfigureAwait(false));
            }
        }

        return list.OrderBy(p => p.Order).ThenBy(p => p.CreatedAt).ToList();
    }

    public async Task<Project> LoadAsync(string key, CancellationToken ct)
    {
        var file = paths.ProjectFile(Identifiers.Require(key, ErrorCodes.ProjectInvalidKey, "proje"));
        if (!File.Exists(file))
        {
            throw new NotFoundException(ErrorCodes.ProjectNotFound, $"Proje yok: '{key}'.");
        }

        ProjectDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<ProjectDto>(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false), Json)
                ?? throw new DomainException(ErrorCodes.ConfigFileInvalid, $"{key}.json bos.");
        }
        catch (JsonException ex)
        {
            throw new DomainException(ErrorCodes.ConfigFileInvalid, $"{key}.json gecersiz JSON: {ex.Message}");
        }

        var project = new Project(
            key,
            dto.Title ?? key,
            dto.Description ?? "",
            dto.Workflow ?? Domain.Workflows.Workflow.DefaultKey,
            dto.TargetDir ?? Project.DefaultTargetDir(key),
            dto.OwnerId ?? Project.LocalOwner,
            dto.CreatedAt ?? File.GetCreationTimeUtc(file),
            dto.Color ?? "",
            dto.Order ?? 0);
        project.Validate();
        return project;
    }

    public Task SaveAsync(Project project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Validate();
        Directory.CreateDirectory(paths.ProjectsDir);
        var dto = new ProjectDto(project.Title, project.Description, project.Workflow, project.TargetDir, project.OwnerId, project.CreatedAt, project.Color, project.Order);
        return AtomicFile.WriteAsync(paths.ProjectFile(project.Key), JsonSerializer.Serialize(dto, Json) + "\n", ct);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var file = paths.ProjectFile(Identifiers.Require(key, ErrorCodes.ProjectInvalidKey, "proje"));
        if (!File.Exists(file))
        {
            throw new NotFoundException(ErrorCodes.ProjectNotFound, $"Proje yok: '{key}'.");
        }

        File.Delete(file);
        return Task.CompletedTask;
    }
}
