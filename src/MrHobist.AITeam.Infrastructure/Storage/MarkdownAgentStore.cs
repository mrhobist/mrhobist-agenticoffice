using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Common;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary>
/// <c>config/agents/{key}.md</c>: frontmatter (<c>name, summary, office_roles, provider, model, effort, includes, can_ask, mcp</c>)
/// + govde sistem promptu. <c>config/knowledge/{key}.md</c>: <c>title</c> + govde.
/// Her yukleme diski okur; ekip butunu yuklenirken dogrulanir.
/// </summary>
public sealed class MarkdownAgentStore(StoragePaths paths) : IAgentStore
{
    public async Task<Team> LoadTeamAsync(CancellationToken ct)
    {
        if (!Directory.Exists(paths.AgentsDir))
        {
            throw new DomainException(ErrorCodes.ConfigFileMissing, $"Ajan dizini yok: {paths.AgentsDir}");
        }

        var knowledge = new Dictionary<string, Knowledge>(StringComparer.Ordinal);
        if (Directory.Exists(paths.KnowledgeDir))
        {
            foreach (var file in Directory.EnumerateFiles(paths.KnowledgeDir, "*.md").Order(StringComparer.Ordinal))
            {
                var key = Path.GetFileNameWithoutExtension(file);
                var doc = Frontmatter.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
                var title = Frontmatter.GetString(doc.Meta, "title") ?? key.Replace('-', ' ');
                knowledge[key] = new Knowledge(key, title, doc.Body);
            }
        }

        var agents = new Dictionary<string, Agent>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(paths.AgentsDir, "*.md").Order(StringComparer.Ordinal))
        {
            var key = Path.GetFileNameWithoutExtension(file);
            agents[key] = ParseAgent(key, await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
        }

        var team = new Team(agents, knowledge);
        team.Validate();
        return team;
    }

    public Task SaveAgentAsync(Agent agent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        Identifiers.Require(agent.Key, ErrorCodes.AgentInvalidKey, "ajan");
        var meta = new List<KeyValuePair<string, object?>>
        {
            new("name", agent.Name),
            new("summary", agent.Summary),
            new("office_roles", agent.OfficeRoles.Count > 0 ? agent.OfficeRoles : null),
            new("provider", agent.Provider is { } p ? Providers.Wire(p) : null),
            new("model", agent.Model),
            new("effort", agent.Effort),
            new("includes", agent.Includes.Count > 0 ? agent.Includes : null),
            new("can_ask", agent.CanAsk),
            new("mcp", agent.McpServers.Count > 0 ? agent.McpServers : null),
        };
        var target = Path.Combine(paths.AgentsDir, agent.Key + ".md");
        return AtomicFile.WriteAsync(target, Frontmatter.Render(meta, agent.Prompt), ct);
    }

    public Task DeleteAgentAsync(string key, CancellationToken ct)
    {
        var target = Path.Combine(paths.AgentsDir, Identifiers.Require(key, ErrorCodes.AgentInvalidKey, "ajan") + ".md");
        if (!File.Exists(target))
        {
            throw new NotFoundException(ErrorCodes.AgentNotFound, $"Ajan yok: '{key}'.");
        }

        File.Delete(target);
        return Task.CompletedTask;
    }

    public Task SaveKnowledgeAsync(Knowledge knowledge, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        Identifiers.Require(knowledge.Key, ErrorCodes.KnowledgeInvalidKey, "bilgi");
        Directory.CreateDirectory(paths.KnowledgeDir);
        var meta = new List<KeyValuePair<string, object?>> { new("title", knowledge.Title) };
        return AtomicFile.WriteAsync(Path.Combine(paths.KnowledgeDir, knowledge.Key + ".md"), Frontmatter.Render(meta, knowledge.Body), ct);
    }

    public Task DeleteKnowledgeAsync(string key, CancellationToken ct)
    {
        var target = Path.Combine(paths.KnowledgeDir, Identifiers.Require(key, ErrorCodes.KnowledgeInvalidKey, "bilgi") + ".md");
        if (!File.Exists(target))
        {
            throw new NotFoundException(ErrorCodes.KnowledgeNotFound, $"Bilgi dosyasi yok: '{key}'.");
        }

        File.Delete(target);
        return Task.CompletedTask;
    }

    public Agent ParseAgentMarkdown(string key, string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        try
        {
            return ParseAgent(key, markdown);
        }
        catch (DomainException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
        {
            throw new DomainException(ErrorCodes.AgentMarkdownInvalid, $"Ajan md'si cozulemedi: {ex.Message}");
        }
    }

    public Knowledge ParseKnowledgeMarkdown(string key, string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        Identifiers.Require(key, ErrorCodes.KnowledgeInvalidKey, "bilgi");
        var doc = Frontmatter.Parse(markdown);
        var title = Frontmatter.GetString(doc.Meta, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            // Frontmatter yoksa ilk "# Baslik" satiri, o da yoksa anahtar.
            var heading = doc.Body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal));
            title = heading is null ? key.Replace('-', ' ') : heading[2..].Trim();
        }

        return new Knowledge(key, title, doc.Body);
    }

    internal static Agent ParseAgent(string key, string text)
    {
        var doc = Frontmatter.Parse(text);
        var m = doc.Meta;
        var agent = new Agent(
            key,
            Frontmatter.GetString(m, "name") ?? key,
            Frontmatter.GetString(m, "summary") ?? "",
            Frontmatter.GetList(m, "office_roles"),
            Providers.Parse(Frontmatter.GetString(m, "provider"), key),
            NullIfEmpty(Frontmatter.GetString(m, "model")),
            Frontmatter.GetList(m, "includes"),
            NullIfEmpty(Frontmatter.GetString(m, "can_ask")),
            doc.Body,
            Efforts.Parse(Frontmatter.GetString(m, "effort"), key),
            Frontmatter.GetList(m, "mcp") is { Count: > 0 } mcp ? mcp : null);
        agent.Validate();
        return agent;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
