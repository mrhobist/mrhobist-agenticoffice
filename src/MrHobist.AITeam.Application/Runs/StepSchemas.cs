using System.Text.Json;
using System.Text.Json.Serialization;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>Developer'in adim sonu raporu (dosyalari araclarla kendisi yazmistir; burasi yalniz ozet + engel bildirimi).</summary>
public sealed record ImplementReport(
    string Summary,
    IReadOnlyList<string> FilesChanged,
    IReadOnlyList<string> CommandsRun,
    bool Blocked,
    string? Question);

/// <summary>Testci / manager karari: kabul ya da gerekceli red; developer'a giden somut geri bildirim.</summary>
public sealed record ReviewReport(
    string Verdict,
    bool TestsRun,
    IReadOnlyList<string> Findings,
    string Feedback,
    IReadOnlyList<string> CommandsRun)
{
    public bool Accepted => string.Equals(Verdict, "accept", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Tasarimcinin rehberligi: kod degil, developer'in uyacagi metin.</summary>
public sealed record DesignReport(string Guidance, IReadOnlyList<string> Decisions);

/// <summary>
/// <c>can_ask</c> hedefinin (manager) takilan ajana cevabi: tek net karar ya da kullaniciya yukseltme
/// (<see cref="Escalate"/>: yetki disi — kapsam, butce, dis erisim). Yukseltmede <see cref="Reason"/> kullaniciya baglam olur.
/// </summary>
public sealed record AskReport(string? Answer, bool Escalate, string? Reason)
{
    public bool Answered => !Escalate && !string.IsNullOrWhiteSpace(Answer);
}

/// <summary>
/// Adim yurutuculerinin yapisal cikti semalari (runtime'a <c>schema</c> olarak gider) ve cozumleme.
/// Sema ile C# kaydi birebir; alan eklenirse ikisi birden degisir (<see cref="SpecSchema"/> ile ayni kural).
/// </summary>
public static class StepSchemas
{
    public const string Implement = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["summary", "filesChanged", "commandsRun", "blocked", "question"],
          "properties": {
            "summary": { "type": "string", "description": "Ne yapildi, kisa" },
            "filesChanged": { "type": "array", "items": { "type": "string" }, "description": "Yazilan/degistirilen dosyalar (goreli yol)" },
            "commandsRun": { "type": "array", "items": { "type": "string" }, "description": "Kosulan komutlar ve sonucu (tek satir)" },
            "blocked": { "type": "boolean", "description": "Gorev bitirilemedi ve bir cevap gerekiyor" },
            "question": { "type": ["string", "null"], "description": "blocked=true ise sorulacak soru; aksi halde null" }
          }
        }
        """;

    public const string Review = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["verdict", "testsRun", "findings", "feedback", "commandsRun"],
          "properties": {
            "verdict": { "type": "string", "enum": ["accept", "reject"] },
            "testsRun": { "type": "boolean", "description": "Testler FIILEN kosuldu mu" },
            "findings": { "type": "array", "items": { "type": "string" }, "description": "Kural/kabul olcutu ihlalleri, her biri tek satir" },
            "feedback": { "type": "string", "description": "Developer'a somut, adim adim ne yapmasi gerektigi (reject ise zorunlu)" },
            "commandsRun": { "type": "array", "items": { "type": "string" } }
          }
        }
        """;

    public const string Design = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["guidance", "decisions"],
          "properties": {
            "guidance": { "type": "string", "description": "Developer'in uyacagi tasarim rehberligi" },
            "decisions": { "type": "array", "items": { "type": "string" } }
          }
        }
        """;

    public const string Ask = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["answer", "escalate", "reason"],
          "properties": {
            "answer": { "type": ["string", "null"], "description": "Soran ajanin hemen uygulayacagi TEK net karar + bir iki cumle gerekce; escalate=true ise null" },
            "escalate": { "type": "boolean", "description": "Karar senin yetkinin disinda (kapsam/butce/dis sistem/kullanici tercihi): kullaniciya sorulsun" },
            "reason": { "type": ["string", "null"], "description": "escalate=true ise neden karar veremedigin (kullaniciya gosterilir); aksi halde null" }
          }
        }
        """;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ImplementReport ParseImplement(string? structuredJson, string text)
        => Parse<ImplementReport>(structuredJson, text) ?? new ImplementReport(Clip(text), [], [], false, null);

    public static ReviewReport ParseReview(string? structuredJson, string text)
        => Parse<ReviewReport>(structuredJson, text) ?? throw new Domain.DomainException(Domain.ErrorCodes.RunStepInvalid, "Inceleme ciktisi semaya uymadi (verdict yok).");

    public static DesignReport ParseDesign(string? structuredJson, string text)
        => Parse<DesignReport>(structuredJson, text) ?? new DesignReport(text.Trim(), []);

    /// <summary>Sema tutmadiysa serbest metin cevap sayilir (manager md'si zaten "tek karar" ister); bos metin yukseltmedir.</summary>
    public static AskReport ParseAsk(string? structuredJson, string text)
        => Parse<AskReport>(structuredJson, text)
            ?? (string.IsNullOrWhiteSpace(text) ? new AskReport(null, true, "cevap bos") : new AskReport(text.Trim(), false, null));

    private static T? Parse<T>(string? structuredJson, string text)
        where T : class
    {
        var raw = !string.IsNullOrWhiteSpace(structuredJson) ? structuredJson : SpecSchema.StripFences(text);
        if (string.IsNullOrWhiteSpace(raw) || !raw.TrimStart().StartsWith('{'))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(raw, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clip(string s) => s.Length <= 400 ? s.Trim() : s[..400].Trim() + "…";
}
