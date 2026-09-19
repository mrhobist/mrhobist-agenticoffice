namespace MrHobist.AITeam.Domain.Agents;

/// <summary>
/// <see cref="Provider"/> ile tel bicimi (md frontmatter, sorgu parametresi, Python istegi) arasindaki
/// TEK cevrim yeri. Tel adi kucuk harftir: <c>anthropic | nvidia | ollama</c>. 'claude' sessizce cevrilmez.
/// </summary>
public static class Providers
{
    public static string Wire(Provider provider) => provider.ToString().ToLowerInvariant();

    /// <summary>Bos/whitespace → null (varsayilan saglayici). Bilinmeyen ad → <c>agent.invalid_provider</c>.</summary>
    public static Provider? Parse(string? text, string? context = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (Enum.TryParse<Provider>(text.Trim(), ignoreCase: true, out var p))
        {
            return p;
        }

        var prefix = context is null ? "" : context + ": ";
        throw new DomainException(ErrorCodes.AgentInvalidProvider, $"{prefix}bilinmeyen provider '{text}' (anthropic | nvidia | ollama).");
    }
}
