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
        // Sabit yol {key}'den once: "reorder" diye proje olamaz mi? Olabilir; bu yuzden anahtar olarak yasaklanmaz ama rota once eslesir.
        g.MapPost("/reorder", (ReorderRequest body, IProjectService s, CancellationToken ct) => s.ReorderAsync(body, ct));
        // Klasor secici (docs/DOMAIN.md → Projeler): hedef dizin yazilmaz, depo icinden secilir. Sabit yol {key}'den once eslesir.
        g.MapGet("/dirs", (string? path, IProjectService s, CancellationToken ct) => s.ListDirectoriesAsync(path, ct));
        g.MapGet("/{key}", (string key, IProjectService s, CancellationToken ct) => s.GetAsync(key, ct));
        g.MapPut("/{key}", (string key, ProjectModel body, IProjectService s, CancellationToken ct) => s.UpdateAsync(key, body, ct));
        // Silme: suren calisma varsa 409; gecmis projeyle gider; ?deleteFiles=true hedef dizini de siler (UI iki adimda onaylatir).
        g.MapDelete("/{key}", (string key, bool? deleteFiles, IProjectService s, CancellationToken ct) => s.DeleteAsync(key, deleteFiles ?? false, ct));
        g.MapGet("/{key}/runs", (string key, int? limit, IRunReader reader, CancellationToken ct) => reader.ListAsync(limit ?? 50, ct, key));
        // Projeyi baslat: kokteki run.cmd yeni konsolda (docs/DOMAIN.md → Projeyi baslatma). 202: surec basladi, sonucu kullanici pencerede gorur.
        g.MapPost("/{key}/launch", async (string key, IProjectService s, CancellationToken ct)
            => Results.Accepted($"/api/v1/projects/{key}", await s.LaunchAsync(key, ct).ConfigureAwait(false)));

        return app;
    }
}
