using System.Text.Json;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary>
/// <c>config/models.json</c>: <c>{ "anthropic": ["claude-opus-5-5", ...], "openai": [...] }</c>. <c>_</c> ile baslayan
/// anahtarlar insan icindir. Depo durum tutmaz: her cagri diski okur, dosya degisince yeniden baslatma gerekmez.
/// </summary>
public sealed class JsonModelCatalog(StoragePaths paths) : IModelCatalog
{
    public const string FileName = "models.json";

    public async Task<IReadOnlyList<CatalogModel>> LoadAsync(CancellationToken ct)
    {
        var file = paths.ConfigFile(FileName);
        if (!File.Exists(file))
        {
            return [];
        }

        JsonDocument doc;
        try
        {
            await using var stream = File.OpenRead(file);
            doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new DomainException(ErrorCodes.ConfigFileInvalid, $"{FileName}: gecersiz JSON ({ex.Message}).");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DomainException(ErrorCodes.ConfigFileInvalid, $"{FileName}: kok bir nesne olmali.");
            }

            var models = new List<CatalogModel>();
            foreach (var entry in doc.RootElement.EnumerateObject().Where(p => !p.Name.StartsWith('_')))
            {
                if (!Enum.TryParse<Provider>(entry.Name, ignoreCase: true, out var provider))
                {
                    throw new DomainException(ErrorCodes.ConfigFileInvalid, $"{FileName}: bilinmeyen saglayici '{entry.Name}'.");
                }

                if (entry.Value.ValueKind != JsonValueKind.Array)
                {
                    throw new DomainException(ErrorCodes.ConfigFileInvalid, $"{FileName}: '{entry.Name}' bir dizi olmali.");
                }

                models.AddRange(entry.Value.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    .Select(v => new CatalogModel(provider, v.GetString()!.Trim())));
            }

            return models;
        }
    }
}
