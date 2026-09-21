using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Workflows;

namespace MrHobist.AITeam.Application.Workflows;

public sealed record StageModel(string Id, string Title, StageKind Kind, string Role, string OfficeRole, string Description);

/// <summary>PUT govdesi; anahtar yoldan gelir. Tum alanlar tasinir (kismi guncelleme yok).</summary>
public sealed record WorkflowModel(
    string Title,
    int MaxReviewRounds,
    string? HandoffRole,
    IReadOnlyList<StageModel> Stages,
    /// <summary>Takilma sorusunu kim cevaplar: bos/null = ajanin can_ask'i · <c>user</c> = kullanici · ajan anahtari.</summary>
    string? AskRole = null,
    /// <summary>Plani kim onaylar: bos/null ya da <c>user</c> = kullanici · ajan anahtari.</summary>
    string? PlanApprover = null);

public sealed record WorkflowListItem(string Key, string Title, bool IsDefault, int StageCount, IReadOnlyList<string> Roles);

public sealed record WorkflowDetail(
    string Key,
    string Title,
    int MaxReviewRounds,
    string? HandoffRole,
    IReadOnlyList<StageModel> Stages,
    string? AskRole = null,
    string? PlanApprover = null);

public interface IWorkflowService
{
    Task<IReadOnlyList<WorkflowListItem>> ListAsync(CancellationToken ct);

    Task<WorkflowDetail> GetAsync(string key, CancellationToken ct);

    /// <summary>Yoksa olusturur. Degismezler ve ekip uyumu tutmazsa <see cref="DomainException"/>; dosya yazilmaz.</summary>
    Task<WorkflowDetail> UpsertAsync(string key, WorkflowModel model, CancellationToken ct);

    /// <summary><c>default</c> silinemez (<c>workflow.default_protected</c>).</summary>
    Task DeleteAsync(string key, CancellationToken ct);
}

public sealed class WorkflowService(IWorkflowStore store, IAgentStore agents) : IWorkflowService
{
    public async Task<IReadOnlyList<WorkflowListItem>> ListAsync(CancellationToken ct)
    {
        var items = new List<WorkflowListItem>();
        foreach (var key in await store.ListKeysAsync(ct).ConfigureAwait(false))
        {
            var wf = await store.LoadAsync(key, ct).ConfigureAwait(false);
            items.Add(new WorkflowListItem(wf.Key, wf.Title, wf.IsDefault, wf.Stages.Count, wf.Roles));
        }

        // default her zaman basta; kalanlar anahtara gore.
        return items.OrderBy(i => i.IsDefault ? 0 : 1).ThenBy(i => i.Key, StringComparer.Ordinal).ToList();
    }

    public async Task<WorkflowDetail> GetAsync(string key, CancellationToken ct)
        => ToDetail(await store.LoadAsync(RequireKey(key), ct).ConfigureAwait(false));

    public async Task<WorkflowDetail> UpsertAsync(string key, WorkflowModel model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        var workflow = new Workflow(
            RequireKey(key),
            model.Title.Trim(),
            model.MaxReviewRounds,
            string.IsNullOrWhiteSpace(model.HandoffRole) ? null : model.HandoffRole.Trim(),
            model.Stages.Select(s => new Stage(s.Id, s.Title, s.Kind, s.Role, s.OfficeRole, s.Description ?? "")).ToList(),
            Blank(model.AskRole),
            Blank(model.PlanApprover));
        workflow.Validate();
        workflow.ValidateAgainst(await agents.LoadTeamAsync(ct).ConfigureAwait(false));
        await store.SaveAsync(workflow, ct).ConfigureAwait(false);
        return ToDetail(workflow);
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        RequireKey(key);
        if (key == Workflow.DefaultKey)
        {
            throw new DomainException(ErrorCodes.WorkflowDefaultProtected, "'default' akisi silinemez; bir calisma akis secmezse bunu kullanir.");
        }

        await store.LoadAsync(key, ct).ConfigureAwait(false); // yoksa workflow.not_found
        await store.DeleteAsync(key, ct).ConfigureAwait(false);
    }

    private static string RequireKey(string key) => Identifiers.Require(key, ErrorCodes.WorkflowInvalidStage, "akis");

    private static WorkflowDetail ToDetail(Workflow wf) => WorkflowMapping.ToDetail(wf);

    /// <summary>Bos metin = "verilmedi": UI bos secimi bos string gonderir, depoda null durmalidir.</summary>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
