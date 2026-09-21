using System.Text.Json;
using System.Text.Json.Serialization;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Workflows;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary>
/// <c>config/workflows/{key}.json</c>, dosya basina bir akis. Insan icin yazilan <c>_comment</c> korunur;
/// enum'lar adiyla, camelCase. Depo durum tutmaz: her cagri diski okur.
/// </summary>
public sealed class JsonWorkflowStore(StoragePaths paths) : IWorkflowStore
{
    private sealed record StageDto(string Id, string Title, string Kind, string Role, string OfficeRole, string? Description);

    private sealed record WorkflowDto(
        [property: JsonPropertyName("_comment")] string? Comment,
        string? Title,
        int MaxReviewRounds,
        string? HandoffRole,
        IReadOnlyList<StageDto>? Stages,
        // Yeni alanlar SONA: eski akis dosyalari alan yokken de okunur (null = eski davranis).
        string? AskRole = null,
        string? PlanApprover = null);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct)
    {
        if (!Directory.Exists(paths.WorkflowsDir))
        {
            throw new DomainException(ErrorCodes.ConfigFileMissing, $"Is akisi dizini yok: {paths.WorkflowsDir}");
        }

        IReadOnlyList<string> keys = Directory.EnumerateFiles(paths.WorkflowsDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(Identifiers.IsValidKey)
            .Select(k => k!)
            .Order(StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(keys);
    }

    public async Task<Workflow> LoadAsync(string key, CancellationToken ct)
    {
        var dto = await ReadDtoAsync(key, ct).ConfigureAwait(false);
        var stages = (dto.Stages ?? []).Select(s => new Stage(
            s.Id ?? "",
            s.Title ?? s.Id ?? "",
            Enum.TryParse<StageKind>(s.Kind, ignoreCase: true, out var kind)
                ? kind
                : throw new DomainException(ErrorCodes.WorkflowInvalidStage, $"{key}/{s.Id}: bilinmeyen kind '{s.Kind}'."),
            s.Role ?? "",
            s.OfficeRole ?? "",
            s.Description ?? "")).ToList();
        var workflow = new Workflow(key, dto.Title ?? key, dto.MaxReviewRounds, dto.HandoffRole, stages, dto.AskRole, dto.PlanApprover);
        workflow.Validate();
        return workflow;
    }

    public async Task SaveAsync(Workflow workflow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        workflow.Validate();

        var file = paths.WorkflowFile(workflow.Key);
        var comment = File.Exists(file) ? (await ReadDtoAsync(workflow.Key, ct).ConfigureAwait(false)).Comment : null;
        var dto = new WorkflowDto(
            comment,
            workflow.Title,
            workflow.MaxReviewRounds,
            workflow.HandoffRole,
            workflow.Stages.Select(s => new StageDto(s.Id, s.Title, s.Kind.ToString().ToLowerInvariant(), s.Role, s.OfficeRole, s.Description)).ToList(),
            workflow.AskRole,
            workflow.PlanApprover);
        await AtomicFile.WriteAsync(file, JsonSerializer.Serialize(dto, Json) + "\n", ct).ConfigureAwait(false);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var file = paths.WorkflowFile(Identifiers.Require(key, ErrorCodes.WorkflowInvalidStage, "akis"));
        if (!File.Exists(file))
        {
            throw new DomainException(ErrorCodes.WorkflowNotFound, $"Is akisi yok: '{key}'.");
        }

        File.Delete(file);
        return Task.CompletedTask;
    }

    private async Task<WorkflowDto> ReadDtoAsync(string key, CancellationToken ct)
    {
        var file = paths.WorkflowFile(Identifiers.Require(key, ErrorCodes.WorkflowInvalidStage, "akis"));
        if (!File.Exists(file))
        {
            throw new DomainException(ErrorCodes.WorkflowNotFound, $"Is akisi yok: '{key}'.");
        }

        try
        {
            return JsonSerializer.Deserialize<WorkflowDto>(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false), Json)
                ?? throw new DomainException(ErrorCodes.ConfigFileInvalid, $"{key}.json bos.");
        }
        catch (JsonException ex)
        {
            throw new DomainException(ErrorCodes.ConfigFileInvalid, $"{key}.json gecersiz JSON: {ex.Message}");
        }
    }
}
