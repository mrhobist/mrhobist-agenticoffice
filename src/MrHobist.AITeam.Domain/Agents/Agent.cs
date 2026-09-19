namespace MrHobist.AITeam.Domain.Agents;

/// <summary>LLM saglayicisi. JSON'da adiyla tasinir; yeni uye sona eklenir.</summary>
public enum Provider
{
    Anthropic,
    Nvidia,
    Ollama,
}

/// <summary>
/// Bir ajan: <c>config/agents/{Key}.md</c>. Frontmatter ustveri, govde sistem promptu.
/// <see cref="Provider"/> ve <see cref="Model"/> bossa varsayilan kullanilir (CLAUDE.md §4).
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
    string Prompt)
{
    /// <summary>Kendi basina tutarli mi: anahtar, bos prompt, include anahtarlari.</summary>
    public void Validate()
    {
        Identifiers.Require(Key, ErrorCodes.AgentInvalidKey, "ajan");
        if (string.IsNullOrWhiteSpace(Prompt))
        {
            throw new DomainException(ErrorCodes.AgentPromptEmpty, $"{Key}: govde bos; sistem promptu olmadan rol calisamaz.");
        }

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

/// <summary>Butun ekip: ajanlar + bilgi dosyalari. Capraz referanslar burada dogrulanir.</summary>
public sealed record Team(IReadOnlyDictionary<string, Agent> Agents, IReadOnlyDictionary<string, Knowledge> Knowledge)
{
    /// <summary>Boru hattinin tanidigi roller; hepsi tanimli olmali.</summary>
    public static readonly IReadOnlyList<string> KnownRoles =
        ["analyst", "designer", "developer", "tester", "manager", "organizer"];

    /// <summary>Eksik include ya da gecersiz can_ask calisma aninda degil, yuklenirken yakalanir.</summary>
    public void Validate()
    {
        var missing = KnownRoles.Where(r => !Agents.ContainsKey(r)).ToList();
        if (missing.Count > 0)
        {
            throw new DomainException(ErrorCodes.TeamMissingRole, $"Eksik ajan tanimi: {string.Join(", ", missing)}.");
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
}
