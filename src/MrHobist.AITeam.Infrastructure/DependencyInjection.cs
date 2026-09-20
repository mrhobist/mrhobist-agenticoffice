using Microsoft.Extensions.DependencyInjection;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Agents;
using MrHobist.AITeam.Application.Projects;
using MrHobist.AITeam.Application.Runs;
using MrHobist.AITeam.Application.Workflows;
using MrHobist.AITeam.Infrastructure.Runtime;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Dosya tabanli depo + uzerine oturan uygulama servisleri. Iki host da ayni kaydi kullanir.</summary>
    public static IServiceCollection AddFileStorage(this IServiceCollection services, StoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(paths);
        services.AddSingleton<IAgentStore, MarkdownAgentStore>();
        services.AddSingleton<IWorkflowStore, JsonWorkflowStore>();
        services.AddSingleton<IProjectStore, JsonProjectStore>();
        services.AddSingleton<IProjectService, ProjectService>();
        services.AddSingleton<IRunStore, JsonlRunStore>();
        services.AddSingleton<IAgentService, AgentService>();
        services.AddSingleton<IWorkflowService, WorkflowService>();
        services.AddSingleton<IRunReader, RunReader>();
        services.AddSingleton<IUsageReader, UsageReader>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IWorkspaceLocator, WorkspaceLocator>();
        services.AddSingleton<IProjectLauncher, WindowsProjectLauncher>();
        services.AddSingleton<ISceneLayout, JsonSceneLayoutStore>();
        services.AddSingleton<LimitGuard>();
        services.AddSingleton(RetryPolicy.Default);
        services.AddSingleton<ProgressRegistry>();
        services.AddSingleton<AgentCaller>();
        services.AddSingleton<IRunService, RunService>();
        return services;
    }

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
