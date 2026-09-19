using System.Text.Json;
using System.Text.Json.Serialization;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Workflows;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary><c>config/workflow.json</c>. <c>_comment</c> alani korunur; enum'lar adiyla, camelCase.</summary>
public sealed class JsonWorkflowStore(StoragePaths paths) : IWorkflowStore
{
    private sealed record StageDto(string Id, string Title, string Kind, string Role, string OfficeRole, string? Description);

    private sealed record WorkflowDto(
        [property: JsonPropertyName("_comment")] string? Comment,
        int MaxReviewRounds,
        IReadOnlyList<StageDto> Stages);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string? _comment;

    public async Task<Workflow> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(paths.WorkflowFile))
        {
            throw new DomainException(ErrorCodes.ConfigFileMissing, $"Is akisi dosyasi yok: {paths.WorkflowFile}");
        }

        WorkflowDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<WorkflowDto>(await File.ReadAllTextAsync(paths.WorkflowFile, ct).ConfigureAwait(false), Json);
        }
        catch (JsonException ex)
        {
            throw new DomainException(ErrorCodes.ConfigFileInvalid, $"workflow.json gecersiz JSON: {ex.Message}");
        }

        if (dto is null || dto.Stages is null)
        {
            throw new DomainException(ErrorCodes.WorkflowInvalidStage, "'stages' bos olamaz.");
        }

        _comment = dto.Comment;
        var stages = dto.Stages.Select(s => new Stage(
            s.Id ?? "",
            s.Title ?? s.Id ?? "",
            Enum.TryParse<StageKind>(s.Kind, ignoreCase: true, out var kind) ? kind : (StageKind)(-1),
            s.Role ?? "",
            s.OfficeRole ?? "",
            s.Description ?? "")).ToList();
        var workflow = new Workflow(dto.MaxReviewRounds, stages);
        workflow.Validate();
        return workflow;
    }

    public Task SaveAsync(Workflow workflow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        workflow.Validate();
        var dto = new WorkflowDto(
            _comment,
            workflow.MaxReviewRounds,
            workflow.Stages.Select(s => new StageDto(s.Id, s.Title, s.Kind.ToString().ToLowerInvariant(), s.Role, s.OfficeRole, s.Description)).ToList());
        return AtomicFile.WriteAsync(paths.WorkflowFile, JsonSerializer.Serialize(dto, Json) + "\n", ct);
    }
}
