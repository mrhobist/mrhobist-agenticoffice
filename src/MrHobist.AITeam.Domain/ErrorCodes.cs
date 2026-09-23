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
    public const string KnowledgeNotFound = "knowledge.not_found";
    public const string KnowledgeInUse = "knowledge.in_use";
    public const string KnowledgeBodyEmpty = "knowledge.body_empty";
    public const string AgentMarkdownInvalid = "agent.markdown_invalid";

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

    public const string AuthRequired = "auth.required";
    public const string AuthInvalidCredentials = "auth.invalid_credentials";

    public const string ProjectInvalidKey = "project.invalid_key";
    public const string ProjectNotFound = "project.not_found";
    public const string ProjectExists = "project.exists";
    public const string ProjectInUse = "project.in_use";
    public const string ProjectTitleEmpty = "project.title_empty";
    public const string ProjectTargetDirInvalid = "project.target_dir_invalid";
    public const string ProjectLaunchMissing = "project.launch_missing";
    public const string ProjectLaunchFailed = "project.launch_failed";
    public const string ProjectInvalidColor = "project.invalid_color";
    public const string RunNotCancellable = "run.not_cancellable";
    public const string RunBudgetInvalid = "run.budget_invalid";
    public const string RunNotAwaitingInput = "run.not_awaiting_input";
    public const string RunInvalidChoice = "run.invalid_choice";
    public const string RunStepInvalid = "run.step_invalid";

    public const string SettingsInvalid = "settings.invalid";

    public const string ConfigFileMissing = "config.file_missing";
    public const string ConfigFileInvalid = "config.file_invalid";
    public const string RuntimeUnavailable = "runtime.unavailable";
    public const string RuntimeError = "runtime.error";

    /// <summary>Proje butcesi (<c>maxCostUsd</c> / <c>maxTokens</c>) sifir ya da negatif verildi. Sinirsiz icin alan <c>null</c> birakilir.</summary>
    public const string ProjectBudgetInvalid = "project.budget_invalid";

    /// <summary>Projenin toplam butcesi asildi; yeni tur baslamaz (docs/DOMAIN.md → Butce ve limit).</summary>
    public const string ProjectBudgetExceeded = "project.budget_exceeded";

    /// <summary>Secilen karakter sprite'i sahnenin <c>sprites[]</c> listesinde yok.</summary>
    public const string AgentUnknownSprite = "agent.unknown_sprite";

    /// <summary>Ajan md'sindeki <c>mcp</c> listesinde kayitli olmayan bir MCP sunucusu var.</summary>
    public const string AgentUnknownMcp = "agent.unknown_mcp";

    /// <summary>MCP yetkisi verilen ajanin saglayicisi MCP desteklemiyor (bugun yalniz <c>anthropic</c>).</summary>
    public const string AgentMcpUnsupported = "agent.mcp_unsupported";

    public const string McpInvalidKey = "mcp.invalid_key";
    public const string McpInvalid = "mcp.invalid";
    public const string McpNotFound = "mcp.not_found";
    public const string McpExists = "mcp.exists";
    public const string McpInUse = "mcp.in_use";

    /// <summary>Katalogda boyle bir hazir sunucu ya da baglanti secenegi yok.</summary>
    public const string McpCatalogNotFound = "mcp.catalog_not_found";

    public const string AttachmentEmpty = "attachment.empty";
    public const string AttachmentTooLarge = "attachment.too_large";
    public const string AttachmentTypeUnsupported = "attachment.type_unsupported";
    public const string AttachmentTooMany = "attachment.too_many";
    public const string AttachmentNotFound = "attachment.not_found";
}
