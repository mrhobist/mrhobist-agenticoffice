using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain.Workflows;

namespace MrHobist.AITeam.Application.Workflows;

public sealed record StageModel(string Id, string Title, StageKind Kind, string Role, string OfficeRole, string Description);

public sealed record WorkflowModel(int MaxReviewRounds, IReadOnlyList<StageModel> Stages);

public interface IWorkflowService
{
    Task<WorkflowModel> GetAsync(CancellationToken ct);

    /// <summary>Degismezler tutmazsa <see cref="Domain.DomainException"/>; dosya yazilmaz.</summary>
    Task<WorkflowModel> UpdateAsync(WorkflowModel model, CancellationToken ct);
}

public sealed class WorkflowService(IWorkflowStore store) : IWorkflowService
{
    public async Task<WorkflowModel> GetAsync(CancellationToken ct)
        => ToModel(await store.LoadAsync(ct).ConfigureAwait(false));

    public async Task<WorkflowModel> UpdateAsync(WorkflowModel model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        var workflow = new Workflow(
            model.MaxReviewRounds,
            model.Stages.Select(s => new Stage(s.Id, s.Title, s.Kind, s.Role, s.OfficeRole, s.Description ?? "")).ToList());
        workflow.Validate();
        await store.SaveAsync(workflow, ct).ConfigureAwait(false);
        return ToModel(workflow);
    }

    private static WorkflowModel ToModel(Workflow wf)
        => new(wf.MaxReviewRounds, wf.Stages.Select(s => new StageModel(s.Id, s.Title, s.Kind, s.Role, s.OfficeRole, s.Description)).ToList());
}
