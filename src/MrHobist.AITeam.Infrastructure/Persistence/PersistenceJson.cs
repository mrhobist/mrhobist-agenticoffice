using System.Text.Json;
using System.Text.Json.Serialization;

namespace MrHobist.AITeam.Infrastructure.Persistence;

/// <summary>
/// <c>data</c> sutunlarinin bicimi. JSONL doneminden AYNI ayarlar: camelCase, enum adiyla, null yazilmaz.
/// Boylece eski <c>runs/*.jsonl</c> satirlari oldugu gibi tasinabilir ve govde git diff'inde okunabilir kalir.
/// </summary>
internal static class PersistenceJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Bozuk govde satiri YOK SAYILIR (JSONL'deki yarim satir kuralinin karsiligi), cagiran null gorur.</summary>
    public static T? Read<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
