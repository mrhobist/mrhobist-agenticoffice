using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Agents;
using MrHobist.AITeam.Application.Workflows;
using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.Api.Config;

/// <summary>Ajan, bilgi, model ve is akisi uclari (docs/API.md). Ince adaptor: is kurali Application'da.</summary>
public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfig(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1");

        g.MapGet("/agents", (IAgentService s, CancellationToken ct) => s.ListAsync(ct));
        g.MapPost("/agents", async (CreateAgentRequest body, IAgentService s, CancellationToken ct)
            => Results.Created($"/api/v1/agents/{body.Key}", await s.CreateAsync(body, ct).ConfigureAwait(false)));
        g.MapGet("/agents/{key}", (string key, IAgentService s, CancellationToken ct) => s.GetAsync(key, ct));
        g.MapPut("/agents/{key}", (string key, UpdateAgentRequest body, IAgentService s, CancellationToken ct) => s.UpdateAsync(key, body, ct));
        g.MapDelete("/agents/{key}", async (string key, IAgentService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(key, ct).ConfigureAwait(false);
            return Results.NoContent();
        });
        g.MapGet("/knowledge", (IAgentService s, CancellationToken ct) => s.ListKnowledgeAsync(ct));

        // Sorgu parametresi tel adiyla gelir ("nvidia"); enum baglayici buyuk/kucuk harfe duyarli oldugu icin metin alinir.
        g.MapGet("/models", (string? provider, IAgentRuntimeService runtime, CancellationToken ct)
            => runtime.ListModelsAsync(Providers.Parse(provider), ct));

        g.MapGet("/workflows", (IWorkflowService s, CancellationToken ct) => s.ListAsync(ct));
        g.MapGet("/workflows/{key}", (string key, IWorkflowService s, CancellationToken ct) => s.GetAsync(key, ct));
        g.MapPut("/workflows/{key}", (string key, WorkflowModel body, IWorkflowService s, CancellationToken ct) => s.UpsertAsync(key, body, ct));
        g.MapDelete("/workflows/{key}", async (string key, IWorkflowService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(key, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        return app;
    }
}
