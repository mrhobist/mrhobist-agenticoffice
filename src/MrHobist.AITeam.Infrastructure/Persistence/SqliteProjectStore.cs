using Microsoft.EntityFrameworkCore;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Common;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Projects;

namespace MrHobist.AITeam.Infrastructure.Persistence;

/// <summary>
/// <c>project</c> tablosu. Anahtar dogrulamasi ve <see cref="Project.Validate"/> aynen korunur: hedef dizin
/// depo icinde goreli bir yoldur ve satira da oyle yazilir -- mutlak yol veritabanina hic girmez, boylece
/// veritabani makineden makineye tasinabilir kalir (opencode'un directoryColumn dersi).
/// </summary>
internal sealed class SqliteProjectStore(IDbContextFactory<AiTeamContext> factory) : IProjectStore
{
    public async Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Projects.AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        // Dosya doneminde de listeleme her kaydi dogruluyordu: elle bozulmus satir listede gorunup acilamamali.
        var projects = new List<Project>(rows.Count);
        foreach (var row in rows)
        {
            var project = ToProject(row);
            project.Validate();
            projects.Add(project);
        }

        return projects;
    }

    public async Task<Project> LoadAsync(string key, CancellationToken ct)
    {
        var id = Identifiers.Require(key, ErrorCodes.ProjectInvalidKey, "proje");
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Key == id, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(ErrorCodes.ProjectNotFound, $"Proje yok: '{key}'.");
        var project = ToProject(row);
        project.Validate();
        return project;
    }

    public async Task SaveAsync(Project project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Validate();
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Projects.FirstOrDefaultAsync(x => x.Key == project.Key, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new ProjectRow { Key = project.Key };
            db.Projects.Add(row);
        }

        row.Title = project.Title;
        row.Description = project.Description;
        row.Workflow = project.Workflow;
        row.TargetDir = project.TargetDir.Replace('\\', '/');
        row.OwnerId = project.OwnerId;
        row.Color = project.Color;
        row.SortOrder = project.Order;
        row.CreatedAt = project.CreatedAt;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        var id = Identifiers.Require(key, ErrorCodes.ProjectInvalidKey, "proje");
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var removed = await db.Projects.Where(x => x.Key == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (removed == 0)
        {
            throw new NotFoundException(ErrorCodes.ProjectNotFound, $"Proje yok: '{key}'.");
        }
    }

    private static Project ToProject(ProjectRow row) => new(
        row.Key,
        row.Title,
        row.Description,
        row.Workflow,
        row.TargetDir,
        row.OwnerId,
        row.CreatedAt,
        row.Color,
        row.SortOrder);
}
