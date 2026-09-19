using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Agents;
using MrHobist.AITeam.Application.Workflows;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.Api.Config;

/// <summary>Ajan, bilgi, model ve is akisi uclari (docs/API.md). Ince adaptor: is kurali Application'da.</summary>
public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfig(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1");

        g.MapGet("/agents", (IAgentService s, CancellationToken ct) => s.ListAsync(ct));
        g.MapGet("/agents/{key}", (string key, IAgentService s, CancellationToken ct) => s.GetAsync(key, ct));
        g.MapPut("/agents/{key}", (string key, UpdateAgentRequest body, IAgentService s, CancellationToken ct) => s.UpdateAsync(key, body, ct));
        g.MapGet("/knowledge", (IAgentService s, CancellationToken ct) => s.ListKnowledgeAsync(ct));

        // Sorgu parametresi adiyla ve kucuk harfle gelir ("nvidia"); enum baglayici buyuk/kucuk harfe duyarlidir.
        g.MapGet("/models", (string? provider, IAgentRuntimeService runtime, CancellationToken ct) =>
        {
            Provider? p = null;
            if (!string.IsNullOrWhiteSpace(provider))
            {
                if (!Enum.TryParse<Provider>(provider, ignoreCase: true, out var parsed))
                {
                    throw new DomainException(ErrorCodes.AgentInvalidProvider, $"Bilinmeyen provider '{provider}' (anthropic | nvidia | ollama).");
                }

                p = parsed;
            }

            return runtime.ListModelsAsync(p, ct);
        });

        g.MapGet("/workflow", (IWorkflowService s, CancellationToken ct) => s.GetAsync(ct));
        g.MapPut("/workflow", (WorkflowModel body, IWorkflowService s, CancellationToken ct) => s.UpdateAsync(body, ct));

        return app;
    }
}
