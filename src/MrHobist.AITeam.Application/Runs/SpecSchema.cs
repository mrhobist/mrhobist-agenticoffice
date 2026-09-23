using System.Text.Json;
using System.Text.Json.Serialization;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>
/// Analistin yapisal ciktisi: JSON semasi (runtime'a <c>schema</c> olarak gider) ve <see cref="Spec"/>'e cozumleme.
/// Sema ile C# kaydi birebir: alan eklenirse ikisi birden degisir.
/// </summary>
public static class SpecSchema
{
    public const string Json = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["summary", "architecture", "rules", "tasks"],
          "properties": {
            "summary": { "type": "string", "description": "Isin tek paragraf ozeti" },
            "architecture": { "type": "string", "description": "Mimari kararlar ve yapi" },
            "rules": { "type": "array", "items": { "type": "string" }, "description": "Baglayici kurallar" },
            "knowledge": { "type": "array", "items": { "type": "string" }, "description": "Gorevlerin ihtiyac duydugu bilgi dosyasi anahtarlari; bos = hepsi" },
            "tasks": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["id", "title", "description", "files", "acceptance", "dependsOn"],
                "properties": {
                  "id": { "type": "string", "description": "kisa, kucuk harf, tire: t1, api-ucu" },
                  "title": { "type": "string" },
                  "description": { "type": "string" },
                  "files": { "type": "array", "items": { "type": "string" } },
                  "acceptance": { "type": "array", "items": { "type": "string" } },
                  "dependsOn": { "type": "array", "items": { "type": "string" } },
                  "ruleRefs": { "type": "array", "items": { "type": "integer" }, "description": "Bu gorevi baglayan kurallarin 0 tabanli sirasi; bos = hepsi" }
                }
              }
            }
          }
        }
        """;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Once yapisal cikti, yoksa metin (kod citi soyulur). Bos/gecersiz plan <see cref="DomainException"/>.</summary>
    public static Spec Parse(string? structuredJson, string text)
    {
        var raw = !string.IsNullOrWhiteSpace(structuredJson) ? structuredJson : StripFences(text);
        Spec? spec;
        try
        {
            spec = JsonSerializer.Deserialize<Spec>(raw, Options);
        }
        catch (JsonException ex)
        {
            throw new DomainException(ErrorCodes.RunPlanInvalid, $"Analist ciktisi JSON degil: {ex.Message}");
        }

        if (spec is null)
        {
            throw new DomainException(ErrorCodes.RunPlanInvalid, "Analist bos plan dondu.");
        }

        Validate(spec);
        return spec;
    }

    private static void Validate(Spec spec)
    {
        if (spec.Tasks is null || spec.Tasks.Count == 0)
        {
            throw new DomainException(ErrorCodes.RunPlanInvalid, "Planda gorev yok.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in spec.Tasks)
        {
            if (string.IsNullOrWhiteSpace(t.Id))
            {
                throw new DomainException(ErrorCodes.RunPlanInvalid, "Kimliksiz gorev var.");
            }

            if (!ids.Add(t.Id.Trim()))
            {
                throw new DomainException(ErrorCodes.RunPlanInvalid, $"Yinelenen gorev kimligi: '{t.Id}'.");
            }
        }
    }

    internal static string StripFences(string text)
    {
        var s = text.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = s.IndexOf('\n');
            s = firstBreak < 0 ? "" : s[(firstBreak + 1)..];
            var end = s.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0)
            {
                s = s[..end];
            }
        }

        return s.Trim();
    }
}
