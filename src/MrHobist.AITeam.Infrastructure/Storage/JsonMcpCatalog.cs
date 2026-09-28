using System.Text.Json;
using System.Text.Json.Serialization;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Mcp;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary>
/// <c>config/mcp-catalog.json</c>: <c>{ "servers": [McpCatalogEntry...] }</c>, camelCase, enum'lar adiyla. Her cagri diski okur;
/// yuklenirken <see cref="McpCatalogBuilder.Validate"/> ile denetlenir (bilinmeyen alana basvuran sablon kurulumda degil burada patlar).
/// </summary>
public sealed class JsonMcpCatalog(StoragePaths paths) : IMcpCatalog
{
    public const string FileName = "mcp-catalog.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record Dto(List<McpCatalogEntry>? Servers);

    public async Task<IReadOnlyList<McpCatalogEntry>> LoadAsync(CancellationToken ct)
    {
        var file = paths.ConfigFile(FileName);
        if (!File.Exists(file))
        {
            return [];
        }

        Dto? dto;
        try
        {
            await using var stream = File.OpenRead(file);
            dto = await JsonSerializer.DeserializeAsync<Dto>(stream, Json, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new DomainException(ErrorCodes.ConfigFileInvalid, $"{FileName}: gecersiz JSON ({ex.Message}).");
        }

        var list = dto?.Servers ?? [];
        McpCatalogBuilder.Validate(list);
        return list;
    }
}
