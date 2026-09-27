using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Projects;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Application.Projects;

/// <summary>PUT govdesi; anahtar yoldan gelir. <see cref="Workflow"/> bos → <c>default</c>, <see cref="TargetDir"/> bos → <c>projects/{key}</c>, <see cref="Color"/> bos → mevcut/palet.</summary>
public sealed record ProjectModel(
    string Title,
    string? Description,
    string? Workflow,
    string? TargetDir,
    string? Color = null,
    /// <summary>Proje butcesi: $ tavani. <c>null</c> = sinirsiz (varsayilan).</summary>
    decimal? MaxCostUsd = null,
    /// <summary>Proje butcesi: token tavani (girdi + cikti). <c>null</c> = sinirsiz (varsayilan).</summary>
    long? MaxTokens = null);

/// <summary>POST govdesi: <see cref="ProjectModel"/> + anahtar.</summary>
public sealed record CreateProjectRequest(
    string Key,
    string Title,
    string? Description,
    string? Workflow,
    string? TargetDir,
    string? Color = null,
    decimal? MaxCostUsd = null,
    long? MaxTokens = null);

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
    int Order = 0,
    /// <summary>Projenin butun calismalarinda harcanan girdi token toplami. 2026-09-22 oncesi isler 0 sayilir (olculmedi).</summary>
    long TotalInputTokens = 0,
    /// <summary>Projenin butun calismalarinda uretilen cikti token toplami.</summary>
    long TotalOutputTokens = 0,
    /// <summary>Proje butcesi: $ tavani; <c>null</c> = sinirsiz.</summary>
    decimal? MaxCostUsd = null,
    /// <summary>Proje butcesi: token tavani; <c>null</c> = sinirsiz.</summary>
    long? MaxTokens = null);

/// <summary><c>POST /projects/{key}/launch</c> yaniti.</summary>
public sealed record LaunchResult(string Key, int ProcessId, string Launcher);

/// <summary><c>DELETE /projects/{key}</c> sonucu: kac calisma gecmisi silindi, hedef dizin silindi mi (istenmediyse ya da yoksa false).</summary>
public sealed record ProjectDeleteResult(string Key, string TargetDir, int RunsDeleted, bool FilesDeleted);

/// <summary>
/// <c>GET /projects/dirs?path=</c>: klasor secicinin bir seviyesi. <see cref="Parent"/> kokte (depo koku ya da surucu koku) null.
/// <see cref="Drives"/> (2026-09-26, sona eklendi): depo disi gezinmenin baslangici (<c>C:/</c>, <c>D:/</c>).
/// </summary>
public sealed record DirectoryListing(string Path, string? Parent, IReadOnlyList<WorkspaceDirectory> Dirs, IReadOnlyList<string>? Drives = null);

/// <summary>
/// Bir klasorun incelemesi (2026-09-26, kullanici istegi: "projede kullanilan diller, GitHub'daki gibi" + "baslamis projeyi
/// iceri al"). <see cref="Languages"/> bayt payiyla (<see cref="Codebase"/>); <see cref="Files"/> sayilan dosya (ucuncu taraf,
/// belge ve uretilmis dosyalar haric). <c>Suggested*</c> iceri alma formunun on dolumu. <see cref="UsedBy"/>: klasor (ya da
/// ic/dis klasoru) zaten bir projenin hedef dizini ise o projenin anahtari -- iceri alma o durumda 409 verir.
/// </summary>
public sealed record ProjectInspection(
    string Path,
    bool IsGit,
    string? GitBranch,
    string? GitRemote,
    int Files,
    long Bytes,
    bool Truncated,
    IReadOnlyList<LanguageShare> Languages,
    IReadOnlyList<string> Manifests,
    bool Launchable,
    string SuggestedKey,
    string SuggestedTitle,
    string SuggestedDescription,
    string? UsedBy);

/// <summary>
/// <c>POST /projects/import</c> govdesi: var olan bir klasor proje olur. Yalniz <see cref="Path"/> zorunlu; bos birakilan
/// anahtar/baslik/aciklama incelemenin onerisinden gelir (anahtar alinmissa sonuna <c>-2</c>, <c>-3</c>…).
/// </summary>
public sealed record ImportProjectRequest(
    string Path,
    string? Key = null,
    string? Title = null,
    string? Description = null,
    string? Workflow = null,
    string? Color = null,
    decimal? MaxCostUsd = null,
    long? MaxTokens = null);

public interface IProjectService
{
    Task<IReadOnlyList<ProjectCard>> ListAsync(CancellationToken ct);

    Task<ProjectCard> GetAsync(string key, CancellationToken ct);

    /// <summary>Var olan anahtar <c>project.exists</c>; akis ekipte gecerli olmali.</summary>
    Task<ProjectCard> CreateAsync(CreateProjectRequest request, CancellationToken ct);

    Task<ProjectCard> UpdateAsync(string key, ProjectModel model, CancellationToken ct);

    /// <summary>
    /// Projeyi siler (kullanici karari 2026-09-20): suren calisma varsa <c>project.in_use</c>; bitmis calismalarin gecmisi
    /// projeyle birlikte silinir; <paramref name="deleteFiles"/> ise hedef dizin de silinir (UI iki adimda onay alir).
    /// </summary>
    Task<ProjectDeleteResult> DeleteAsync(string key, bool deleteFiles, CancellationToken ct);

    /// <summary>Klasor secici: depo icindeki bir seviyenin alt klasorleri.</summary>
    Task<DirectoryListing> ListDirectoriesAsync(string? path, CancellationToken ct);

    /// <summary>Proje kokundeki <c>run.cmd</c>'yi yeni konsolda baslatir (docs/DOMAIN.md → Projeyi baslatma).</summary>
    Task<LaunchResult> LaunchAsync(string key, CancellationToken ct);

    /// <summary>Proje sirasini yazar: verilen anahtarlar 0..n, kalanlar arkaya. Ray ve Kanban bu sirayi okur.</summary>
    Task<IReadOnlyList<ProjectCard>> ReorderAsync(ReorderRequest request, CancellationToken ct);

    /// <summary>Bir klasoru inceler (iceri alma onizlemesi). Yol depo icinde goreli ya da suruculu tam yol; yoksa <c>project.dir_not_found</c>.</summary>
    Task<ProjectInspection> InspectAsync(string? path, CancellationToken ct);

    /// <summary>Projenin hedef dizinini inceler: dil seridi, derleme dosyalari, git.</summary>
    Task<ProjectInspection> InspectProjectAsync(string key, CancellationToken ct);

    /// <summary>Var olan klasoru proje olarak alir (docs/DOMAIN.md → Projeyi iceri alma). Klasor baska projedeyse <c>project.dir_in_use</c>.</summary>
    Task<ProjectCard> ImportAsync(ImportProjectRequest request, CancellationToken ct);
}

public sealed class ProjectService(IProjectStore projects, IWorkflowStore workflows, IRunStore runs, IWorkspaceLocator workspace, IProjectLauncher launcher, IAttachmentStore? attachments = null, IWorkspaceInspector? inspector = null) : IProjectService
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
        var project = Compose(key, DateTimeOffset.UtcNow, new ProjectModel(request.Title, request.Description, request.Workflow, request.TargetDir, color, request.MaxCostUsd, request.MaxTokens)) with { Order = order };
        await workflows.LoadAsync(project.Workflow, ct).ConfigureAwait(false); // yoksa workflow.not_found
        EnsureDirFree(project.TargetDir, project.Key, existing);
        await projects.SaveAsync(project, ct).ConfigureAwait(false);
        return ToCard(project, [], project.Color);
    }

    public async Task<ProjectCard> UpdateAsync(string key, ProjectModel model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        var current = await projects.LoadAsync(key, ct).ConfigureAwait(false);
        var project = Compose(current.Key, current.CreatedAt, model) with { OwnerId = current.OwnerId, Order = current.Order, Color = string.IsNullOrWhiteSpace(model.Color) ? current.Color : model.Color.Trim() };
        await workflows.LoadAsync(project.Workflow, ct).ConfigureAwait(false);
        if (!string.Equals(project.TargetDir, current.TargetDir, StringComparison.OrdinalIgnoreCase))
        {
            EnsureDirFree(project.TargetDir, project.Key, await projects.ListAsync(ct).ConfigureAwait(false));
        }

        await projects.SaveAsync(project, ct).ConfigureAwait(false);
        return ToCard(project, await runs.ListAsync(1000, ct, project.Key).ConfigureAwait(false), ColorOf(project, await projects.ListAsync(ct).ConfigureAwait(false)));
    }

    /// <summary>Silmeyi engelleyen durumlar: is suruyor ya da kullanicidan/limitten bir sey bekliyor.</summary>
    private static bool IsActive(RunStatus s)
        => s is RunStatus.Running or RunStatus.AwaitingApproval or RunStatus.Paused or RunStatus.AwaitingInput;

    public async Task<ProjectDeleteResult> DeleteAsync(string key, bool deleteFiles, CancellationToken ct)
    {
        var project = await projects.LoadAsync(key, ct).ConfigureAwait(false);
        var history = await runs.ListAsync(10_000, ct, project.Key).ConfigureAwait(false);
        var active = history.Count(r => IsActive(r.Status));
        if (active > 0)
        {
            throw new DomainException(ErrorCodes.ProjectInUse, $"'{key}' icinde suren {active} calisma var; once bitirin ya da iptal edin.");
        }

        // Gecmis projeyle gider: proje kapsamli UI'da projesiz gecmisin yeri yok (varsayimla ilerlenir, docs/DOMAIN.md).
        foreach (var run in history)
        {
            await runs.DeleteAsync(run.Id, ct).ConfigureAwait(false);
            attachments?.DeleteRun(run.Id);
        }

        var filesDeleted = deleteFiles && workspace.DeleteRoot(project);
        await projects.DeleteAsync(project.Key, ct).ConfigureAwait(false);
        return new ProjectDeleteResult(project.Key, project.TargetDir, history.Count, filesDeleted);
    }

    public Task<DirectoryListing> ListDirectoriesAsync(string? path, CancellationToken ct)
    {
        var raw = Project.NormalizeDir(path);
        if (Project.IsDriveRoot(raw) || Project.IsExternalDir(raw))
        {
            // Depo disi (2026-09-26, iceri alma): surucu koku "C:/", ustu yok; "C:/Users" → ustu "C:/".
            var shown = raw.Length == 2 ? raw + "/" : raw;
            var slash = raw.LastIndexOf('/');
            var up = raw.Length == 2 ? null : (slash <= 2 ? raw[..2] + "/" : raw[..slash]);
            return Task.FromResult(new DirectoryListing(shown, up, workspace.ListDirectories(shown), workspace.Drives()));
        }

        // "C:klasor" (surucuye goreli) ve ".." iceren tam yol buraya duser; locator 400 verir.
        var rel = raw.TrimStart('/');
        var parent = rel.Length == 0 ? null : (rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "");
        return Task.FromResult(new DirectoryListing(rel, parent, workspace.ListDirectories(rel), workspace.Drives()));
    }

    public async Task<ProjectInspection> InspectAsync(string? path, CancellationToken ct)
        => await InspectDirAsync(NormalizeDir(path), await projects.ListAsync(ct).ConfigureAwait(false), self: null, missingIsEmpty: false, ct).ConfigureAwait(false);

    public async Task<ProjectInspection> InspectProjectAsync(string key, CancellationToken ct)
    {
        var project = await projects.LoadAsync(key, ct).ConfigureAwait(false);
        // Okuma ucu diske YAZMAZ (2026-09-27): hedef dizin henuz yoksa (ilk is baslamadi) ya da tasindiysa "0 dosya" doner,
        // dizin olusturulmaz. Dizini olusturmak isi baslatanin isidir (RunService → RootOf).
        return await InspectDirAsync(project.TargetDir, await projects.ListAsync(ct).ConfigureAwait(false), self: project.Key, missingIsEmpty: true, ct).ConfigureAwait(false);
    }

    public async Task<ProjectCard> ImportAsync(ImportProjectRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dir = NormalizeDir(request.Path);
        var existing = await projects.ListAsync(ct).ConfigureAwait(false);
        // Klasor cakismasi (project.dir_in_use) CreateAsync'te denetlenir: yeni proje ve iceri alma ayni kurala baglidir.
        var seen = await InspectDirAsync(dir, existing, self: null, missingIsEmpty: false, ct).ConfigureAwait(false);

        // Anahtar verilmediyse oneriden; o da alinmissa sonuna sayi (klasor adi cogu zaman benzersiz ama garanti degil).
        var key = string.IsNullOrWhiteSpace(request.Key) ? FreeKey(seen.SuggestedKey, existing) : request.Key.Trim();
        return await CreateAsync(new CreateProjectRequest(
            key,
            string.IsNullOrWhiteSpace(request.Title) ? seen.SuggestedTitle : request.Title,
            request.Description ?? seen.SuggestedDescription,
            request.Workflow,
            dir,
            request.Color,
            request.MaxCostUsd,
            request.MaxTokens), ct).ConfigureAwait(false);
    }

    /// <summary><paramref name="missingIsEmpty"/>: projenin kendi dizini yoksa bos inceleme; iceri alma onizlemesinde 404.</summary>
    private async Task<ProjectInspection> InspectDirAsync(string dir, IReadOnlyList<Project> all, string? self, bool missingIsEmpty, CancellationToken ct)
    {
        // Depo disina kacan goreli yol, surucu koku: project.target_dir_invalid; ofisin kendi deposu: project.dir_reserved.
        var full = workspace.PathOf(dir);
        var scan = inspector is null ? WorkspaceScan.Missing : await inspector.ScanAsync(full, ct).ConfigureAwait(false);
        if (!scan.Exists && !missingIsEmpty)
        {
            throw new Application.Common.NotFoundException(ErrorCodes.ProjectDirNotFound, $"Klasor yok: '{dir}'.");
        }

        var code = scan.Code;
        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
        return new ProjectInspection(
            dir, scan.IsGit, scan.GitBranch, scan.GitRemote, code.Files, code.Bytes, scan.Truncated, code.Languages, code.Manifests,
            launcher.CanLaunch(full), ProjectSuggestion.Key(folder), scan.SuggestedTitle, scan.SuggestedDescription, OwnerOf(full, all, self));
    }

    /// <summary>
    /// Iki proje ayni klasore -- ya da biri digerinin icine -- baglanamaz (2026-09-26): ayni-proje sira kilidi anahtara gore
    /// tutuldugu icin iki is ayni dosyalara ayni anda yazar; ustteki projenin "son turu geri al"i alttakinin yazdiklarini da siler.
    /// </summary>
    private void EnsureDirFree(string targetDir, string key, IReadOnlyList<Project> all)
    {
        if (OwnerOf(workspace.PathOf(targetDir), all, key) is { } owner)
        {
            throw new DomainException(ErrorCodes.ProjectDirInUse, $"'{targetDir}' zaten '{owner}' projesinin klasoru (ya da onun ic/dis klasoru).");
        }
    }

    private string? OwnerOf(string full, IReadOnlyList<Project> all, string? self)
    {
        var mine = Normal(full);
        foreach (var p in all.Where(p => p.Key != self))
        {
            string other;
            try
            {
                other = Normal(workspace.PathOf(p.TargetDir));
            }
            catch (DomainException)
            {
                continue; // eski, gecersiz hedef dizinli kayit karsilastirmayi bozmasin
            }

            if (string.Equals(mine, other, StringComparison.OrdinalIgnoreCase)
                || mine.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || other.StartsWith(mine + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return p.Key;
            }
        }

        return null;

        static string Normal(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
    }

    private static string FreeKey(string wanted, IReadOnlyList<Project> all)
    {
        var key = wanted;
        for (var i = 2; all.Any(p => p.Key == key); i++)
        {
            key = $"{wanted}-{i}";
        }

        return key;
    }

    /// <summary>Ters bolu → ileri bolu, sondaki bolu atilir. Bos yol gecersiz: iceri alma depo kokunu kastedemez.</summary>
    private static string NormalizeDir(string? path)
    {
        var dir = Project.NormalizeDir(path);
        return dir.Length == 0 ? throw new DomainException(ErrorCodes.ProjectTargetDirInvalid, "Klasor verilmedi.") : dir;
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
            (m.Color ?? "").Trim(),
            Order: 0,
            MaxCostUsd: m.MaxCostUsd,
            MaxTokens: m.MaxTokens);
        project.Validate();
        return project;
    }

    /// <summary>Eski kayitlarda renk bos: paletten, listedeki siraya gore kararli bir renk (dosya degismez, gorunum tutarli).</summary>
    private static string ColorOf(Project p, IReadOnlyList<Project> all)
        => !string.IsNullOrEmpty(p.Color) ? p.Color : Project.Palette[Math.Max(0, all.ToList().FindIndex(x => x.Key == p.Key)) % Project.Palette.Count];

    /// <summary>
    /// Kartin "Projeyi baslat" bayragi. Liste okumasi diske yazmaz (2026-09-27): <c>RootOf</c> eksik dizini olusturup bariyer
    /// dosyasi yaziyordu; artik yol yalniz cozulur. Gecersiz/ayrilmis hedef dizinli eski kayit listeyi dusurmez, baslatilamaz gorunur.
    /// </summary>
    private bool CanLaunch(Project p)
    {
        try
        {
            return launcher.CanLaunch(workspace.PathOf(p.TargetDir));
        }
        catch (DomainException)
        {
            return false;
        }
    }

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
            CanLaunch(p),
            color,
            p.Order,
            list.Sum(r => r.InputTokens),
            list.Sum(r => r.OutputTokens),
            p.MaxCostUsd,
            p.MaxTokens);
    }
}
