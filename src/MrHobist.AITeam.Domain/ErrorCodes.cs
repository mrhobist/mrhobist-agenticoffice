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

    public const string ConfigFileMissing = "config.file_missing";
    public const string ConfigFileInvalid = "config.file_invalid";
    public const string RuntimeUnavailable = "runtime.unavailable";
}
