using MrHobist.AITeam.Domain.Workflows;

namespace MrHobist.AITeam.Application.Workflows;

/// <summary>Domain akisi → HTTP modeli. Tek esleme yeri: <c>GET /workflows/{key}</c> ve <c>GET /runs/{id}.workflowDef</c> ayni sekli doner.</summary>
public static class WorkflowMapping
{
    public static WorkflowDetail ToDetail(Workflow wf)
    {
        ArgumentNullException.ThrowIfNull(wf);
        return new WorkflowDetail(wf.Key, wf.Title, wf.MaxReviewRounds, wf.HandoffRole,
            wf.Stages.Select(s => new StageModel(s.Id, s.Title, s.Kind, s.Role, s.OfficeRole, s.Description)).ToList());
    }
}
