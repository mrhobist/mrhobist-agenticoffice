namespace MrHobist.AITeam.Domain;

/// <summary>
/// Sinirda <c>errorCode</c> olarak gecen kodlar. Yayindan sonra yeniden adlandirilmaz.
/// UI eslemesi <c>ui/app/api/errors.ts</c>, aciklama <c>docs/error-codes.md</c>.
/// </summary>
public static class ErrorCodes
{
    public const string AgentInvalidKey = "agent.invalid_key";
    public const string AgentNotFound = "agent.not_found";
    public const string AgentPromptEmpty = "agent.prompt_empty";
    public const string AgentInvalidProvider = "agent.invalid_provider";
    public const string AgentUnknownInclude = "agent.unknown_include";
    public const string AgentUnknownCanAsk = "agent.unknown_can_ask";
    public const string AgentExists = "agent.exists";
    public const string AgentInUse = "agent.in_use";
    public const string AgentInvalidEffort = "agent.invalid_effort";

    public const string KnowledgeInvalidKey = "knowledge.invalid_key";

    public const string WorkflowNotFound = "workflow.not_found";
    public const string WorkflowUnknownRole = "workflow.unknown_role";
    public const string WorkflowDefaultProtected = "workflow.default_protected";
    public const string WorkflowAnalyzeCount = "workflow.analyze_count";
    public const string WorkflowAnalyzeFirst = "workflow.analyze_first";
    public const string WorkflowNoImplement = "workflow.no_implement";
    public const string WorkflowReviewBeforeImplement = "workflow.review_before_implement";
    public const string WorkflowRoundsMin = "workflow.rounds_min";
    public const string WorkflowDuplicateStage = "workflow.duplicate_stage";
    public const string WorkflowInvalidStage = "workflow.invalid_stage";

    public const string RunPolicyViolation = "run.policy_violation";
    public const string RunBudgetExceeded = "run.budget_exceeded";
    public const string RunNotFound = "run.not_found";
    public const string RunBriefEmpty = "run.brief_empty";
    public const string RunNoteEmpty = "run.note_empty";
    public const string RunNotAwaitingApproval = "run.not_awaiting_approval";
    public const string RunPlanInvalid = "run.plan_invalid";
    public const string RunProjectRequired = "run.project_required";
    public const string RunNotRetryable = "run.not_retryable";

    public const string ProjectInvalidKey = "project.invalid_key";
    public const string ProjectNotFound = "project.not_found";
    public const string ProjectExists = "project.exists";
    public const string ProjectInUse = "project.in_use";
    public const string ProjectTitleEmpty = "project.title_empty";
    public const string ProjectTargetDirInvalid = "project.target_dir_invalid";
    public const string RunNotCancellable = "run.not_cancellable";
    public const string RunBudgetInvalid = "run.budget_invalid";

    public const string ConfigFileMissing = "config.file_missing";
    public const string ConfigFileInvalid = "config.file_invalid";
    public const string RuntimeUnavailable = "runtime.unavailable";
    public const string RuntimeError = "runtime.error";
}
