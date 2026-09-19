namespace MrHobist.AITeam.Domain.Agents;

/// <summary>LLM saglayicisi. JSON'da adiyla tasinir; yeni uye sona eklenir.</summary>
public enum Provider
{
    Anthropic,
    Nvidia,
    Ollama,
}

/// <summary>Akil yurutme eforu. Tel adi kucuk harf; bos = varsayilan (<see cref="Efforts.Default"/>).</summary>
public static class Efforts
{
    public const string Default = "high";

    public static readonly IReadOnlyList<string> All = ["low", "medium", "high", "max"];

    /// <summary>Bos → null (varsayilan). Bilinmeyen deger → <c>agent.invalid_effort</c>.</summary>
    public static string? Parse(string? text, string? context = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim().ToLowerInvariant();
        if (All.Contains(value, StringComparer.Ordinal))
        {
            return value;
        }

        var prefix = context is null ? "" : context + ": ";
        throw new DomainException(ErrorCodes.AgentInvalidEffort, $"{prefix}bilinmeyen effort '{text}' (low | medium | high | max).");
    }
}

/// <summary>
/// Bir ajan: <c>config/agents/{Key}.md</c>. Frontmatter ustveri, govde sistem promptu.
/// <see cref="Provider"/>, <see cref="Model"/> ve <see cref="Effort"/> bossa varsayilan kullanilir (CLAUDE.md §4).
/// </summary>
public sealed record Agent(
    string Key,
    string Name,
    string Summary,
    IReadOnlyList<string> OfficeRoles,
    Provider? Provider,
    string? Model,
    IReadOnlyList<string> Includes,
    string? CanAsk,
    string Prompt,
    string? Effort = null)
{
    /// <summary>Kendi basina tutarli mi: anahtar, bos prompt, include anahtarlari, efor.</summary>
    public void Validate()
    {
        Identifiers.Require(Key, ErrorCodes.AgentInvalidKey, "ajan");
        if (string.IsNullOrWhiteSpace(Prompt))
        {
            throw new DomainException(ErrorCodes.AgentPromptEmpty, $"{Key}: govde bos; sistem promptu olmadan rol calisamaz.");
        }

        Efforts.Parse(Effort, Key);

        foreach (var include in Includes)
        {
            Identifiers.Require(include, ErrorCodes.AgentUnknownInclude, "bilgi dosyasi");
        }

        if (CanAsk is not null)
        {
            Identifiers.Require(CanAsk, ErrorCodes.AgentUnknownCanAsk, "can_ask");
        }
    }

    /// <summary>Govde + alt md'ler. Modele fiilen giden metin budur.</summary>
    public string ComposePrompt(IReadOnlyDictionary<string, Knowledge> knowledge)
    {
        var parts = new List<string> { Prompt.Trim() };
        foreach (var include in Includes)
        {
            if (!knowledge.TryGetValue(include, out var k))
            {
                throw new DomainException(ErrorCodes.AgentUnknownInclude, $"{Key}: '{include}' bilgi dosyasi yok.");
            }

            parts.Add($"\n\n---\n\n# {k.Title}\n\n{k.Body.Trim()}");
        }

        return string.Concat(parts);
    }
}

/// <summary>Alt md: birden cok ajanin paylastigi bilgi dosyasi (<c>config/knowledge/{Key}.md</c>).</summary>
public sealed record Knowledge(string Key, string Title, string Body);

/// <summary>
/// Butun ekip: ajanlar + bilgi dosyalari. Ekip ACIKTIR: zorunlu rol yoktur, hangi ajanlarin calisacagini
/// is akisi belirler (<see cref="Workflows.Workflow.ValidateAgainst"/>). Capraz referanslar burada dogrulanir.
/// </summary>
public sealed record Team(IReadOnlyDictionary<string, Agent> Agents, IReadOnlyDictionary<string, Knowledge> Knowledge)
{
    /// <summary>Eksik include ya da gecersiz can_ask calisma aninda degil, yuklenirken yakalanir.</summary>
    public void Validate()
    {
        foreach (var k in Knowledge.Keys)
        {
            Identifiers.Require(k, ErrorCodes.KnowledgeInvalidKey, "bilgi dosyasi");
        }

        foreach (var agent in Agents.Values)
        {
            agent.Validate();
            agent.ComposePrompt(Knowledge);
            if (agent.CanAsk is not null && !Agents.ContainsKey(agent.CanAsk))
            {
                throw new DomainException(ErrorCodes.AgentUnknownCanAsk, $"{agent.Key}: can_ask='{agent.CanAsk}' diye bir ajan yok.");
            }
        }
    }

    /// <summary>Bu ajani <c>can_ask</c> ile gosteren diger ajanlar (silme oncesi denetim).</summary>
    public IReadOnlyList<string> AskersOf(string key)
        => Agents.Values.Where(a => a.CanAsk == key && a.Key != key).Select(a => a.Key).Order(StringComparer.Ordinal).ToList();
}
