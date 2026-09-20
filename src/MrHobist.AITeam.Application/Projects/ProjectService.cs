using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Projects;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Application.Projects;

/// <summary>PUT govdesi; anahtar yoldan gelir. <see cref="Workflow"/> bos → <c>default</c>, <see cref="TargetDir"/> bos → <c>projects/{key}</c>.</summary>
public sealed record ProjectModel(string Title, string? Description, string? Workflow, string? TargetDir);

/// <summary>POST govdesi: <see cref="ProjectModel"/> + anahtar.</summary>
public sealed record CreateProjectRequest(string Key, string Title, string? Description, string? Workflow, string? TargetDir);

/// <summary>Proje karti: tanim + islerin ozeti (rayda kac is calisiyor, kaci senden bir sey bekliyor, toplam maliyet, son hareket).</summary>
public sealed record ProjectCard(
    string Key,
    string Title,
    string Description,
    string Workflow,
    string TargetDir,
    string OwnerId,
    DateTimeOffset CreatedAt,
    int Runs,
    int Running,
    int AwaitingApproval,
    int Paused,
    int Failed,
    int Completed,
    decimal TotalCostUsd,
    DateTimeOffset? LastActivityAt,
    /// <summary>Kokte <c>run.cmd</c> var: "Projeyi baslat" dugmesi acik. Sona eklendi (CLAUDE.md §5).</summary>
    bool Launchable = false);

/// <summary><c>POST /projects/{key}/launch</c> yaniti.</summary>
public sealed record LaunchResult(string Key, int ProcessId, string Launcher);

public interface IProjectService
{
    Task<IReadOnlyList<ProjectCard>> ListAsync(CancellationToken ct);

    Task<ProjectCard> GetAsync(string key, CancellationToken ct);

    /// <summary>Var olan anahtar <c>project.exists</c>; akis ekipte gecerli olmali.</summary>
    Task<ProjectCard> CreateAsync(CreateProjectRequest request, CancellationToken ct);

    Task<ProjectCard> UpdateAsync(string key, ProjectModel model, CancellationToken ct);

    /// <summary>Icinde calisma varsa <c>project.in_use</c>: gecmis silinmez, proje kapatilmaz.</summary>
    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>Proje kokundeki <c>run.cmd</c>'yi yeni konsolda baslatir (docs/DOMAIN.md → Projeyi baslatma).</summary>
    Task<LaunchResult> LaunchAsync(string key, CancellationToken ct);
}

public sealed class ProjectService(IProjectStore projects, IWorkflowStore workflows, IRunStore runs, IWorkspaceLocator workspace, IProjectLauncher launcher) : IProjectService
{
    public const string LauncherFile = "run.cmd";

    public async Task<LaunchResult> LaunchAsync(string key, CancellationToken ct)
    {
        var project = await projects.LoadAsync(key, ct).ConfigureAwait(false);
        var pid = launcher.Launch(workspace.RootOf(project));
        return new LaunchResult(project.Key, pid, LauncherFile);
    }

    public async Task<IReadOnlyList<ProjectCard>> ListAsync(CancellationToken ct)
    {
        var all = await runs.ListAsync(1000, ct).ConfigureAwait(false);
        var cards = new List<ProjectCard>();
        foreach (var p in await projects.ListAsync(ct).ConfigureAwait(false))
        {
            cards.Add(ToCard(p, all.Where(r => r.Project == p.Key)));
        }

        return cards;
    }

    public async Task<ProjectCard> GetAsync(string key, CancellationToken ct)
    {
        var p = await projects.LoadAsync(key, ct).ConfigureAwait(false);
        return ToCard(p, await runs.ListAsync(1000, ct, p.Key).ConfigureAwait(false));
    }

    public async Task<ProjectCard> CreateAsync(CreateProjectRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = Identifiers.Require(request.Key, ErrorCodes.ProjectInvalidKey, "proje");
        if ((await projects.ListAsync(ct).ConfigureAwait(false)).Any(p => p.Key == key))
        {
            throw new DomainException(ErrorCodes.ProjectExists, $"'{key}' anahtarli proje zaten var.");
        }

        var project = Compose(key, DateTimeOffset.UtcNow, new ProjectModel(request.Title, request.Description, request.Workflow, request.TargetDir));
        await workflows.LoadAsync(project.Workflow, ct).ConfigureAwait(false); // yoksa workflow.not_found
        await projects.SaveAsync(project, ct).ConfigureAwait(false);
        return ToCard(project, []);
    }

    public async Task<ProjectCard> UpdateAsync(string key, ProjectModel model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        var current = await projects.LoadAsync(key, ct).ConfigureAwait(false);
        var project = Compose(current.Key, current.CreatedAt, model) with { OwnerId = current.OwnerId };
        await workflows.LoadAsync(project.Workflow, ct).ConfigureAwait(false);
        await projects.SaveAsync(project, ct).ConfigureAwait(false);
        return ToCard(project, await runs.ListAsync(1000, ct, project.Key).ConfigureAwait(false));
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        var project = await projects.LoadAsync(key, ct).ConfigureAwait(false);
        var count = (await runs.ListAsync(1, ct, project.Key).ConfigureAwait(false)).Count;
        if (count > 0)
        {
            throw new DomainException(ErrorCodes.ProjectInUse, $"'{key}' icinde calisma var; gecmis silinmez. Once calismalari arsivleyin.");
        }

        await projects.DeleteAsync(project.Key, ct).ConfigureAwait(false);
    }

    private static Project Compose(string key, DateTimeOffset createdAt, ProjectModel m)
    {
        var project = new Project(
            key,
            (m.Title ?? "").Trim(),
            (m.Description ?? "").Trim(),
            string.IsNullOrWhiteSpace(m.Workflow) ? Domain.Workflows.Workflow.DefaultKey : m.Workflow.Trim(),
            string.IsNullOrWhiteSpace(m.TargetDir) ? Project.DefaultTargetDir(key) : m.TargetDir.Trim().Replace('\\', '/').TrimEnd('/'),
            Project.LocalOwner,
            createdAt);
        project.Validate();
        return project;
    }

    private ProjectCard ToCard(Project p, IEnumerable<Run> runs)
    {
        var list = runs.ToList();
        return new ProjectCard(
            p.Key, p.Title, p.Description, p.Workflow, p.TargetDir, p.OwnerId, p.CreatedAt,
            list.Count,
            list.Count(r => r.Status == RunStatus.Running),
            list.Count(r => r.Status == RunStatus.AwaitingApproval),
            list.Count(r => r.Status == RunStatus.Paused),
            list.Count(r => r.Status is RunStatus.Failed or RunStatus.Interrupted or RunStatus.BudgetExceeded or RunStatus.PolicyRejected),
            list.Count(r => r.Status == RunStatus.Completed),
            list.Sum(r => r.TotalCostUsd),
            list.Count == 0 ? null : list.Max(r => r.FinishedAt ?? r.StartedAt),
            launcher.CanLaunch(workspace.RootOf(p)));
    }
}
