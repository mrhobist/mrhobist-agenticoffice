using MrHobist.AITeam.Domain.Agents;
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
/// <c>runs/{id}/</c> append-only JSONL deposu. Yazan tek surec Task.Api; Api yalniz okur.
/// Bozuk son satir yok sayilir, geri kalani kurtarilir.
/// </summary>
public interface IRunStore
{
    Task CreateAsync(Run run, CancellationToken ct);

    Task UpdateAsync(Run run, CancellationToken ct);

    Task WriteSpecAsync(string runId, Spec spec, CancellationToken ct);

    Task AppendTurnAsync(string runId, Turn turn, CancellationToken ct);

    Task AppendMessageAsync(string runId, Message message, CancellationToken ct);

    Task AppendPhaseAsync(string runId, Phase phase, CancellationToken ct);

    Task<Run?> GetAsync(string runId, CancellationToken ct);

    Task<IReadOnlyList<Run>> ListAsync(int limit, CancellationToken ct);

    Task<Spec?> ReadSpecAsync(string runId, CancellationToken ct);

    Task<IReadOnlyList<Turn>> ReadTurnsAsync(string runId, string agent, CancellationToken ct);

    Task<IReadOnlyList<Message>> ReadMessagesAsync(string runId, CancellationToken ct);

    Task<IReadOnlyList<Phase>> ReadPhasesAsync(string runId, string task, CancellationToken ct);

    Task<IReadOnlyList<string>> ListTasksAsync(string runId, CancellationToken ct);
}
