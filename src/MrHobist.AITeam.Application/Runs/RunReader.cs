using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Common;
using MrHobist.AITeam.Application.Workflows;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>Salt okuma: Api bunu kullanir, dosyaya dokunmaz (CLAUDE.md §2).</summary>
public interface IRunReader
{
    Task<IReadOnlyList<Run>> ListAsync(int limit, CancellationToken ct);

    Task<Run> GetAsync(string runId, CancellationToken ct);

    Task<RunDetail> GetDetailAsync(string runId, CancellationToken ct);

    /// <summary>Tum ajanlarin LLM turlari (tam prompt/cikti ile), zamana gore. Gunluk ekrani icin.</summary>
    Task<IReadOnlyList<Turn>> GetTurnsAsync(string runId, string? agent, CancellationToken ct);

    /// <summary>Bir ajanin son calismalardaki isleri (yeni → eski); turu ya da mesaji olmayan calismalar atlanir.</summary>
    Task<IReadOnlyList<AgentRunWork>> GetAgentWorkAsync(string agentKey, int runLimit, CancellationToken ct);

    /// <summary>Kac is var, ne durumda, kaci kullanicidan bir sey bekliyor (docs/DOMAIN.md → Gelen kutusu).</summary>
    Task<RunsOverview> GetOverviewAsync(CancellationToken ct);
}

/// <summary>runs/ altindaki kayitlari <see cref="RunDetail"/> olarak toplar. Yazmaz, LLM cagirmaz, sahneye dokunmaz.</summary>
public sealed class RunReader(IRunStore runs) : IRunReader
{
    public Task<IReadOnlyList<Run>> ListAsync(int limit, CancellationToken ct) => runs.ListAsync(Math.Clamp(limit, 1, 200), ct);

    public async Task<Run> GetAsync(string runId, CancellationToken ct)
        => await runs.GetAsync(runId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(ErrorCodes.RunNotFound, $"Calisma yok: '{runId}'.");

    public async Task<RunDetail> GetDetailAsync(string runId, CancellationToken ct)
    {
        var run = await GetAsync(runId, ct).ConfigureAwait(false);
        var wf = await runs.ReadWorkflowAsync(runId, ct).ConfigureAwait(false);
        var spec = await runs.ReadSpecAsync(runId, ct).ConfigureAwait(false);
        var messages = await runs.ReadMessagesAsync(runId, ct).ConfigureAwait(false);
        var tasks = new List<TaskPhases>();
        foreach (var id in await runs.ListTasksAsync(runId, ct).ConfigureAwait(false))
        {
            tasks.Add(new TaskPhases(id, await runs.ReadPhasesAsync(runId, id, ct).ConfigureAwait(false)));
        }

        var order = spec is null ? [] : TaskGraph.Order(spec.Tasks).Select(t => t.Id).ToList();
        return new RunDetail(
            run.Id, run.Label, run.Brief, run.Sensitivity, run.Workflow, run.Status, run.StartedAt, run.FinishedAt,
            run.TotalCostUsd, run.Detail, run.MaxCostUsd, run.Retries, wf is null ? null : WorkflowMapping.ToDetail(wf), spec, order, tasks, messages);
    }

    public async Task<IReadOnlyList<AgentRunWork>> GetAgentWorkAsync(string agentKey, int runLimit, CancellationToken ct)
    {
        var key = Identifiers.Require(agentKey, ErrorCodes.AgentInvalidKey, "ajan");
        var result = new List<AgentRunWork>();
        foreach (var run in await runs.ListAsync(Math.Clamp(runLimit, 1, 200), ct).ConfigureAwait(false))
        {
            var turns = await runs.ReadTurnsAsync(run.Id, key, ct).ConfigureAwait(false);
            var messages = (await runs.ReadMessagesAsync(run.Id, ct).ConfigureAwait(false))
                .Where(m => m.From == key || m.To == key)
                .ToList();
            var phases = new List<Phase>();
            foreach (var id in await runs.ListTasksAsync(run.Id, ct).ConfigureAwait(false))
            {
                phases.AddRange((await runs.ReadPhasesAsync(run.Id, id, ct).ConfigureAwait(false)).Where(p => p.Agent == key));
            }

            if (turns.Count > 0 || messages.Count > 0 || phases.Count > 0)
            {
                result.Add(new AgentRunWork(run, turns, messages, phases.OrderBy(p => p.Ts).ToList()));
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<Turn>> GetTurnsAsync(string runId, string? agent, CancellationToken ct)
    {
        await GetAsync(runId, ct).ConfigureAwait(false); // yoksa run.not_found
        var agents = string.IsNullOrWhiteSpace(agent)
            ? await runs.ListConversationsAsync(runId, ct).ConfigureAwait(false)
            : [agent.Trim()];
        var all = new List<Turn>();
        foreach (var a in agents)
        {
            all.AddRange(await runs.ReadTurnsAsync(runId, a, ct).ConfigureAwait(false));
        }

        return all.OrderBy(t => t.Ts).ToList();
    }

    public async Task<RunsOverview> GetOverviewAsync(CancellationToken ct)
    {
        var list = await runs.ListAsync(200, ct).ConfigureAwait(false);
        var inbox = new List<InboxItem>();
        foreach (var run in list)
        {
            inbox.AddRange(await InboxOfAsync(run, ct).ConfigureAwait(false));
        }

        return new RunsOverview(
            list.Count,
            list.Count(r => r.Status == RunStatus.Running),
            list.Count(r => r.Status == RunStatus.AwaitingApproval),
            list.Count(r => r.Status == RunStatus.Paused),
            list.Count(r => r.Status is RunStatus.Failed or RunStatus.Interrupted or RunStatus.BudgetExceeded or RunStatus.PolicyRejected),
            list.Count(r => r.Status == RunStatus.Completed),
            list.Count(r => r.Status == RunStatus.Cancelled),
            inbox.OrderByDescending(i => i.Ts).ToList());
    }

    /// <summary>
    /// Bir calismanin kullanicidan bekledikleri. Bitmis (Completed/Cancelled) ve hic baslamamis (PolicyRejected) calisma
    /// bir sey beklemez. Mesajlar yalniz aday calismalarda okunur; liste ucuz kalir.
    /// </summary>
    private async Task<IReadOnlyList<InboxItem>> InboxOfAsync(Run run, CancellationToken ct)
    {
        if (run.Status is RunStatus.Completed or RunStatus.Cancelled or RunStatus.PolicyRejected)
        {
            return [];
        }

        var messages = await runs.ReadMessagesAsync(run.Id, ct).ConfigureAwait(false);
        var lastTs = messages.Count > 0 ? messages.Max(m => m.Ts) : run.StartedAt;
        var items = new List<InboxItem>();

        // Soru: plan onayi. Cevap = approve ya da revise.
        if (run.Status == RunStatus.AwaitingApproval)
        {
            var spec = await runs.ReadSpecAsync(run.Id, ct).ConfigureAwait(false);
            items.Add(new InboxItem(run.Id, run.Label, run.Status, InboxKind.Approval, lastTs, "Plan onayı bekliyor", spec?.Summary, null));
        }

        // Karar: calisma durdu. Paused yalniz iptal edilebilir, dusenler yeniden denenebilir; UI dugmeyi duruma gore secer.
        if (run.Status is RunStatus.Paused or RunStatus.Failed or RunStatus.Interrupted or RunStatus.BudgetExceeded)
        {
            var title = run.Status switch
            {
                RunStatus.Paused => "Durakladı: yürütücüsü olmayan adım",
                RunStatus.BudgetExceeded => "Bütçe aşıldı",
                RunStatus.Interrupted => "Yarıda kaldı",
                _ => "Başarısız oldu",
            };
            items.Add(new InboxItem(run.Id, run.Label, run.Status, InboxKind.Decision, run.FinishedAt ?? lastTs, title, run.Detail, null));
        }

        // Soru: bir ajan kullaniciya ask yazdi ve ref'i eslesen answer yok. Bugun uretilmiyor; sozlesme hazir.
        var answered = messages.Where(m => m.Kind == MessageKind.Answer && m.Ref is not null).Select(m => m.Ref!).ToHashSet(StringComparer.Ordinal);
        foreach (var ask in messages.Where(m => m.Kind == MessageKind.Ask && m.To == "user" && (m.Ref is null || !answered.Contains(m.Ref))))
        {
            items.Add(new InboxItem(run.Id, run.Label, run.Status, InboxKind.Question, ask.Ts, $"{ask.From} soruyor", ask.Body, ask.Task));
        }

        return items;
    }
}
