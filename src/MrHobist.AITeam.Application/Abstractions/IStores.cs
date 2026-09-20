using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Projects;
using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Domain.Workflows;

namespace MrHobist.AITeam.Application.Abstractions;

/// <summary><c>config/agents/*.md</c> + <c>config/knowledge/*.md</c>. Her okuma diski yeniden okur; config degisince Api yeniden yukler.</summary>
public interface IAgentStore
{
    Task<Team> LoadTeamAsync(CancellationToken ct);

    /// <summary>Olusturur ya da uzerine yazar; atomik. Cagiran once <see cref="Team.Validate"/> ile dogrulamis olmali.</summary>
    Task SaveAgentAsync(Agent agent, CancellationToken ct);

    /// <summary>Md dosyasini siler. Referans denetimi (akislar, can_ask) cagiranin isidir.</summary>
    Task DeleteAgentAsync(string key, CancellationToken ct);
}

/// <summary><c>config/projects/{key}.json</c>; is yalniz bir projenin icinde baslar (docs/DOMAIN.md → Projeler).</summary>
public interface IProjectStore
{
    Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct);

    /// <summary>Yoksa <c>project.not_found</c>.</summary>
    Task<Project> LoadAsync(string key, CancellationToken ct);

    Task SaveAsync(Project project, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);
}

/// <summary><c>config/workflows/{key}.json</c>; <c>default</c> her zaman vardir.</summary>
public interface IWorkflowStore
{
    /// <summary>Dosya adlarindan anahtarlar, sirali. Gecersiz adli dosyalar yok sayilir.</summary>
    Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct);

    /// <summary>Yoksa <c>workflow.not_found</c>.</summary>
    Task<Workflow> LoadAsync(string key, CancellationToken ct);

    Task SaveAsync(Workflow workflow, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);
}

/// <summary>
/// <c>runs/{id}/</c> append-only JSONL deposu. Yazan tek yazici Api icindeki is kanalidir; uclar IRunReader ile okur.
/// Bozuk son satir yok sayilir, geri kalani kurtarilir.
/// </summary>
public interface IRunStore
{
    Task CreateAsync(Run run, CancellationToken ct);

    Task UpdateAsync(Run run, CancellationToken ct);

    Task WriteSpecAsync(string runId, Spec spec, CancellationToken ct);

    /// <summary>Calisma baslarken secilen akisin kopyasi: <c>runs/{id}/workflow.json</c>. Config sonradan degisse de calisma bunu okur.</summary>
    Task WriteWorkflowAsync(string runId, Workflow workflow, CancellationToken ct);

    Task<Workflow?> ReadWorkflowAsync(string runId, CancellationToken ct);

    Task AppendTurnAsync(string runId, Turn turn, CancellationToken ct);

    Task AppendMessageAsync(string runId, Message message, CancellationToken ct);

    Task AppendPhaseAsync(string runId, Phase phase, CancellationToken ct);

    Task<Run?> GetAsync(string runId, CancellationToken ct);

    /// <summary>Yeni → eski. <paramref name="project"/> verilirse yalniz o projenin calismalari.</summary>
    Task<IReadOnlyList<Run>> ListAsync(int limit, CancellationToken ct, string? project = null);

    Task<Spec?> ReadSpecAsync(string runId, CancellationToken ct);

    Task<IReadOnlyList<Turn>> ReadTurnsAsync(string runId, string agent, CancellationToken ct);

    /// <summary><c>conversations/*.jsonl</c> dosya adlari: bu calismada LLM cagirmis ajanlar (silinmis ajanlar dahil).</summary>
    Task<IReadOnlyList<string>> ListConversationsAsync(string runId, CancellationToken ct);

    Task<IReadOnlyList<Message>> ReadMessagesAsync(string runId, CancellationToken ct);

    Task<IReadOnlyList<Phase>> ReadPhasesAsync(string runId, string task, CancellationToken ct);

    Task<IReadOnlyList<string>> ListTasksAsync(string runId, CancellationToken ct);
}

/// <summary><c>config/settings.json</c>: calisma alani ayarlari (limit korumasi). Dosya yoksa varsayilan.</summary>
public interface ISettingsStore
{
    Task<Domain.Settings.AppSettings> LoadAsync(CancellationToken ct);

    Task SaveAsync(Domain.Settings.AppSettings settings, CancellationToken ct);
}

/// <summary>
/// Projenin hedef dizininin diskteki mutlak yolu (depo koku + <c>targetDir</c>). Developer/testci araclari bu dizinde
/// calisir, yazma disina cikamaz. Dizin yoksa OLUSTURULUR (SDK cwd'nin var olmasini ister); dosya I/O burada, Application'da degil.
/// </summary>
public interface IWorkspaceLocator
{
    string RootOf(Project project);
}

/// <summary>
/// Projeyi baslatma sozlesmesi (kullanici istegi 2026-09-20): proje kokundeki <c>run.cmd</c> yeni bir konsol
/// penceresinde kosulur; ne baslatilacagini developer o dosyaya yazar. Surec baglanmaz, cikti okunmaz: uygulama kullanicinin.
/// </summary>
public interface IProjectLauncher
{
    /// <summary>Kokte baslatici var mi (<c>run.cmd</c>).</summary>
    bool CanLaunch(string projectRoot);

    /// <summary>Baslaticiyi yeni pencerede kosar; surec kimligini doner. Yoksa <c>project.launch_missing</c>, kosamazsa <c>project.launch_failed</c>.</summary>
    int Launch(string projectRoot);
}
