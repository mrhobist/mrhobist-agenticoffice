using System.Text.Json;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary>
/// <c>config/models.json</c>: <c>{ "anthropic": ["claude-opus-5-5", ...], "openai": [...], "prices": { model: {...} } }</c>.
/// <c>_</c> ile baslayan anahtarlar insan icindir; <c>prices</c> saglayici degil fiyat tablosudur. Depo durum tutmaz: her cagri diski okur, dosya degisince yeniden baslatma gerekmez.
/// </summary>
public sealed class JsonModelCatalog(StoragePaths paths) : IModelCatalog
{
    public const string FileName = "models.json";

    public const string PricesKey = "prices";

    public async Task<IReadOnlyDictionary<string, ModelPrice>> LoadPricesAsync(CancellationToken ct)
    {
        using var doc = await ReadAsync(ct).ConfigureAwait(false);
        var prices = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(PricesKey, out var table) || table.ValueKind != JsonValueKind.Object)
        {
            return prices;
        }

        foreach (var entry in table.EnumerateObject().Where(p => !p.Name.StartsWith('_')))
        {
            try
            {
                var v = entry.Value;
                prices[entry.Name] = new ModelPrice(v.GetProperty("input").GetDecimal(), v.GetProperty("output").GetDecimal(), v.GetProperty("cacheRead").GetDecimal(), v.GetProperty("cacheWrite").GetDecimal(),
                    v.TryGetProperty("cacheWrite5m", out var shortWrite) ? shortWrite.GetDecimal() : null);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new DomainException(ErrorCodes.ConfigFileInvalid, $"{FileName}: '{entry.Name}' fiyati input/output/cacheRead/cacheWrite sayilarini icermeli.");
            }
        }

        return prices;
    }

    private async Task<JsonDocument?> ReadAsync(CancellationToken ct)
    {
        var file = paths.ConfigFile(FileName);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(file);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new DomainException(ErrorCodes.ConfigFileInvalid, $"{FileName}: gecersiz JSON ({ex.Message}).");
        }
    }

    public async Task<IReadOnlyList<CatalogModel>> LoadAsync(CancellationToken ct)
    {
        using var doc = await ReadAsync(ct).ConfigureAwait(false);
        if (doc is null)
        {
            return [];
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new DomainException(ErrorCodes.ConfigFileInvalid, $"{FileName}: kok bir nesne olmali.");
        }

        var models = new List<CatalogModel>();
        foreach (var entry in doc.RootElement.EnumerateObject().Where(p => !p.Name.StartsWith('_') && p.Name != PricesKey))
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
