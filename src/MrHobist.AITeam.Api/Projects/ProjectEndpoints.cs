using MrHobist.AITeam.Application.Projects;
using MrHobist.AITeam.Application.Runs;

namespace MrHobist.AITeam.Api.Projects;

/// <summary>Proje uclari (docs/API.md → Projeler). Kartlar islerin ozetini tasir; is yalniz bir projenin icinde baslar.</summary>
public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjects(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/projects");

        g.MapGet("", (IProjectService s, CancellationToken ct) => s.ListAsync(ct));
        g.MapPost("", async (CreateProjectRequest body, IProjectService s, CancellationToken ct)
            => Results.Created($"/api/v1/projects/{body.Key}", await s.CreateAsync(body, ct).ConfigureAwait(false)));
        g.MapGet("/{key}", (string key, IProjectService s, CancellationToken ct) => s.GetAsync(key, ct));
        g.MapPut("/{key}", (string key, ProjectModel body, IProjectService s, CancellationToken ct) => s.UpdateAsync(key, body, ct));
        g.MapDelete("/{key}", async (string key, IProjectService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(key, ct).ConfigureAwait(false);
            return Results.NoContent();
        });
        g.MapGet("/{key}/runs", (string key, int? limit, IRunReader reader, CancellationToken ct) => reader.ListAsync(limit ?? 50, ct, key));

        return app;
    }
}
