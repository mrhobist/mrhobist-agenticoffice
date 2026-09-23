using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Agents;
using MrHobist.AITeam.Application.Projects;
using MrHobist.AITeam.Application.Runs;
using MrHobist.AITeam.Application.Workflows;
using MrHobist.AITeam.Infrastructure.Compaction;
using MrHobist.AITeam.Infrastructure.Persistence;
using MrHobist.AITeam.Infrastructure.Runtime;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Depo katmani + uzerine oturan uygulama servisleri. Api ve ServiceTests ayni kaydi kullanir.
    /// Ikili yerlesim (CLAUDE.md §2): ajan/bilgi/akis/sahne <c>config/</c> altinda dosyada, calisma zamani
    /// durumu (proje, calisma, tur, mesaj, faz, ayarlar) SQLite'ta. Semayi <see cref="MigrateDatabase"/>
    /// uygular -- bu metot yalniz kayit yapar, I/O yapmaz.
    /// </summary>
    public static IServiceCollection AddAiTeamStorage(this IServiceCollection services, StoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);
        services.AddSingleton(paths);

        // Depolar singleton, DbContext degil: her islem kendi baglamini fabrikadan alir.
        services.AddDbContextFactory<AiTeamContext>(o => o.UseSqlite(ConnectionString(paths)));

        services.AddSingleton<IAgentStore, MarkdownAgentStore>();
        services.AddSingleton<IWorkflowStore, JsonWorkflowStore>();
        services.AddSingleton<ISceneLayout, JsonSceneLayoutStore>();

        services.AddSingleton<IProjectStore, SqliteProjectStore>();
        services.AddSingleton<IRunStore, SqliteRunStore>();
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();

        services.AddSingleton<IProjectService, ProjectService>();
        services.AddSingleton<IAgentService, AgentService>();
        services.AddSingleton<IWorkflowService, WorkflowService>();
        services.AddSingleton<IRunReader, RunReader>();
        services.AddSingleton<IUsageReader, UsageReader>();
        services.AddSingleton<IWorkspaceLocator, WorkspaceLocator>();
        services.AddSingleton<IProjectLauncher, WindowsProjectLauncher>();
        services.AddSingleton<LimitGuard>();
        services.AddSingleton(RetryPolicy.Default);
        services.AddSingleton<ProgressRegistry>();
        services.AddSingleton<AgentCaller>();
        services.AddSingleton<IHistoryCompactor, MafHistoryCompactor>();
        services.AddSingleton<IWorkspaceSnapshot, GitWorkspaceSnapshot>();
        services.AddSingleton<IRunService, RunService>();
        return services;
    }

    /// <summary>
    /// Uygulanmamis sema betiklerini kosar ve kosulan betik sayisini doner (ARCHITECTURE.md §8).
    /// Kalkista, servisler ayaga kalkmadan once cagrilir: sema yoksa ilk sorgu degil bu adim patlar.
    /// </summary>
    public static int MigrateDatabase(StoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return SchemaMigrator.Apply(paths.DatabaseFile);
    }

    /// <summary>
    /// Sapma (drift) denetimi (ARCHITECTURE.md §8.5): her tablodan <c>Take(0)</c> ile bir sorgu kosar.
    /// Betikteki sutun adi ile EF eslemesi ayrisirsa burada patlar -- kullanicinin karsisinda, ilk gercek sorguda degil.
    /// Denenen tablo sayisini doner.
    /// </summary>
    public static async Task<int> ProbeDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var factory = services.GetRequiredService<IDbContextFactory<AiTeamContext>>();
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        await db.Projects.Take(0).ToListAsync(ct).ConfigureAwait(false);
        await db.Runs.Take(0).ToListAsync(ct).ConfigureAwait(false);
        await db.Turns.Take(0).ToListAsync(ct).ConfigureAwait(false);
        await db.Messages.Take(0).ToListAsync(ct).ConfigureAwait(false);
        await db.Phases.Take(0).ToListAsync(ct).ConfigureAwait(false);
        await db.Settings.Take(0).ToListAsync(ct).ConfigureAwait(false);
        return 6;
    }

    private static string ConnectionString(StoragePaths paths) => new SqliteConnectionStringBuilder
    {
        DataSource = paths.DatabaseFile,
        // SQLite'ta yabanci anahtarlar varsayilan KAPALI: acik yazilmazsa calisma silinince alt satirlar kalir.
        ForeignKeys = true,
    }.ToString();

    /// <summary>Python runtime istemcisi (CLAUDE.md §1). Varsayilan <c>http://127.0.0.1:5090</c>.</summary>
    public static IServiceCollection AddPythonRuntime(this IServiceCollection services, Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpClient<IAgentRuntimeService, PythonAgentRuntimeClient>(client =>
        {
            client.BaseAddress = baseAddress;
            // Bir LLM turu dakikalar surebilir (LESSONS: NVIDIA 180 s x 3 deneme runtime icinde).
            client.Timeout = TimeSpan.FromMinutes(12);
        });
        return services;
    }
}
