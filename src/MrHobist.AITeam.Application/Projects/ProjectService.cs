using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Projects;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Application.Projects;

/// <summary>PUT govdesi; anahtar yoldan gelir. <see cref="Workflow"/> bos → <c>default</c>, <see cref="TargetDir"/> bos → <c>projects/{key}</c>, <see cref="Color"/> bos → mevcut/palet.</summary>
public sealed record ProjectModel(string Title, string? Description, string? Workflow, string? TargetDir, string? Color = null);

/// <summary>POST govdesi: <see cref="ProjectModel"/> + anahtar.</summary>
public sealed record CreateProjectRequest(string Key, string Title, string? Description, string? Workflow, string? TargetDir, string? Color = null);

/// <summary><c>POST /projects/reorder</c> govdesi: anahtarlar yeni sirayla; listede olmayanlar sona, mevcut sirayla.</summary>
public sealed record ReorderRequest(IReadOnlyList<string> Keys);

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
    bool Launchable = false,
    /// <summary>Proje rengi (#rrggbb); ray karti ve Kanban "Tumu" kartlari bunu kullanir.</summary>
    string Color = "",
    /// <summary>Ray ve Kanban sirasi (kucuk once).</summary>
    int Order = 0);

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

    /// <summary>Proje sirasini yazar: verilen anahtarlar 0..n, kalanlar arkaya. Ray ve Kanban bu sirayi okur.</summary>
    Task<IReadOnlyList<ProjectCard>> ReorderAsync(ReorderRequest request, CancellationToken ct);
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
        var list = await projects.ListAsync(ct).ConfigureAwait(false);
        var cards = new List<ProjectCard>();
        foreach (var p in list)
        {
            cards.Add(ToCard(p, all.Where(r => r.Project == p.Key), ColorOf(p, list)));
        }

        return cards;
    }

    public async Task<ProjectCard> GetAsync(string key, CancellationToken ct)
    {
        var list = await projects.ListAsync(ct).ConfigureAwait(false);
        var p = list.FirstOrDefault(x => x.Key == key) ?? await projects.LoadAsync(key, ct).ConfigureAwait(false);
        return ToCard(p, await runs.ListAsync(1000, ct, p.Key).ConfigureAwait(false), ColorOf(p, list));
    }

    public async Task<IReadOnlyList<ProjectCard>> ReorderAsync(ReorderRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var all = await projects.ListAsync(ct).ConfigureAwait(false);
        var wanted = (request.Keys ?? []).Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var ordered = wanted.Select(k => all.FirstOrDefault(p => p.Key == k) ?? throw new Application.Common.NotFoundException(ErrorCodes.ProjectNotFound, $"Proje yok: '{k}'."))
            .Concat(all.Where(p => !wanted.Contains(p.Key)))
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            // Sira yazilirken bos renk de kalicilasir: renk listedeki siradan turetildigi icin yer degistirince kaymasin.
            var color = string.IsNullOrEmpty(ordered[i].Color) ? ColorOf(ordered[i], all) : ordered[i].Color;
            if (ordered[i].Order != i || color != ordered[i].Color)
            {
                await projects.SaveAsync(ordered[i] with { Order = i, Color = color }, ct).ConfigureAwait(false);
            }
        }

        return await ListAsync(ct).ConfigureAwait(false);
    }

    public async Task<ProjectCard> CreateAsync(CreateProjectRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = Identifiers.Require(request.Key, ErrorCodes.ProjectInvalidKey, "proje");
        var existing = await projects.ListAsync(ct).ConfigureAwait(false);
        if (existing.Any(p => p.Key == key))
        {
            throw new DomainException(ErrorCodes.ProjectExists, $"'{key}' anahtarli proje zaten var.");
        }

        // Renk: istenmediyse paletten kullanilmayan ilk renk; sira: sona.
        var used = existing.Select(p => ColorOf(p, existing)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var color = string.IsNullOrWhiteSpace(request.Color) ? Project.Palette.FirstOrDefault(c => !used.Contains(c)) ?? Project.Palette[existing.Count % Project.Palette.Count] : request.Color.Trim();
        var order = existing.Count == 0 ? 0 : existing.Max(p => p.Order) + 1;
        var project = Compose(key, DateTimeOffset.UtcNow, new ProjectModel(request.Title, request.Description, request.Workflow, request.TargetDir, color)) with { Order = order };
        await workflows.LoadAsync(project.Workflow, ct).ConfigureAwait(false); // yoksa workflow.not_found
        await projects.SaveAsync(project, ct).ConfigureAwait(false);
        return ToCard(project, [], project.Color);
    }

    public async Task<ProjectCard> UpdateAsync(string key, ProjectModel model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        var current = await projects.LoadAsync(key, ct).ConfigureAwait(false);
        var project = Compose(current.Key, current.CreatedAt, model) with { OwnerId = current.OwnerId, Order = current.Order, Color = string.IsNullOrWhiteSpace(model.Color) ? current.Color : model.Color.Trim() };
        await workflows.LoadAsync(project.Workflow, ct).ConfigureAwait(false);
        await projects.SaveAsync(project, ct).ConfigureAwait(false);
        return ToCard(project, await runs.ListAsync(1000, ct, project.Key).ConfigureAwait(false), ColorOf(project, await projects.ListAsync(ct).ConfigureAwait(false)));
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
            createdAt,
            (m.Color ?? "").Trim());
        project.Validate();
        return project;
    }

    /// <summary>Eski kayitlarda renk bos: paletten, listedeki siraya gore kararli bir renk (dosya degismez, gorunum tutarli).</summary>
    private static string ColorOf(Project p, IReadOnlyList<Project> all)
        => !string.IsNullOrEmpty(p.Color) ? p.Color : Project.Palette[Math.Max(0, all.ToList().FindIndex(x => x.Key == p.Key)) % Project.Palette.Count];

    private ProjectCard ToCard(Project p, IEnumerable<Run> runs, string color)
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
            launcher.CanLaunch(workspace.RootOf(p)),
            color,
            p.Order);
    }
}
