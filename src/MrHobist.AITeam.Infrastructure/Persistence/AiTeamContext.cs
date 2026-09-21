using Microsoft.EntityFrameworkCore;

namespace MrHobist.AITeam.Infrastructure.Persistence;

/// <summary>
/// Tek veritabani siniri: <c>data/aiteam.db</c> (SQLite). Sema EF tarafindan URETILMEZ -- database-first:
/// <c>scripts/sql/changes/*.sql</c> betikleri <see cref="SchemaMigrator"/> ile uygulanir, EF yalniz esler
/// (ARCHITECTURE.md §8). Bu yuzden burada <c>EnsureCreated</c>/migration yoktur ve sutun adlari ELLE verilir:
/// betikteki ad ile buradaki ad ayrisirsa kalkista degil, ilk sorguda patlar -- drift testi bunu yakalar.
/// </summary>
internal sealed class AiTeamContext(DbContextOptions<AiTeamContext> options) : DbContext(options)
{
    public DbSet<ProjectRow> Projects => Set<ProjectRow>();

    public DbSet<RunRow> Runs => Set<RunRow>();

    public DbSet<TurnRow> Turns => Set<TurnRow>();

    public DbSet<MessageRow> Messages => Set<MessageRow>();

    public DbSet<PhaseRow> Phases => Set<PhaseRow>();

    public DbSet<SettingsRow> Settings => Set<SettingsRow>();

    /// <summary>Butun zaman damgalari tek bicimde: siralanabilir UTC metni (<see cref="UtcTextConverter"/>). Nullable olanlar da kapsanir.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTextConverter>().HaveColumnType("TEXT");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ProjectRow>(e =>
        {
            e.ToTable("project");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("key");
            e.Property(x => x.Title).HasColumnName("title");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.Workflow).HasColumnName("workflow");
            e.Property(x => x.TargetDir).HasColumnName("target_dir");
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.Color).HasColumnName("color");
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<RunRow>(e =>
        {
            e.ToTable("run");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProjectKey).HasColumnName("project_key");
            e.Property(x => x.Label).HasColumnName("label");
            e.Property(x => x.Brief).HasColumnName("brief");
            e.Property(x => x.Sensitivity).HasColumnName("sensitivity").HasConversion<string>();
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.Step).HasColumnName("step").HasConversion<string>();
            e.Property(x => x.WorkflowKey).HasColumnName("workflow_key");
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.Detail).HasColumnName("detail");
            e.Property(x => x.Question).HasColumnName("question");
            e.Property(x => x.TotalCostUsd).HasColumnName("total_cost_usd").HasColumnType("TEXT");
            e.Property(x => x.MaxCostUsd).HasColumnName("max_cost_usd").HasColumnType("TEXT");
            e.Property(x => x.Retries).HasColumnName("retries");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.FinishedAt).HasColumnName("finished_at");
            e.Property(x => x.ResumeAt).HasColumnName("resume_at");
            e.Property(x => x.WaitingSince).HasColumnName("waiting_since");
            e.Property(x => x.Spec).HasColumnName("spec");
            e.Property(x => x.WorkflowSnapshot).HasColumnName("workflow_snapshot");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<TurnRow>(e =>
        {
            e.ToTable("run_turn");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.RunId).HasColumnName("run_id");
            e.Property(x => x.Agent).HasColumnName("agent");
            e.Property(x => x.Ts).HasColumnName("ts");
            e.Property(x => x.Stage).HasColumnName("stage");
            e.Property(x => x.Task).HasColumnName("task");
            e.Property(x => x.Round).HasColumnName("round");
            e.Property(x => x.Provider).HasColumnName("provider");
            e.Property(x => x.Model).HasColumnName("model");
            e.Property(x => x.Destination).HasColumnName("destination").HasConversion<string>();
            e.Property(x => x.DurationS).HasColumnName("duration_s");
            e.Property(x => x.CostUsd).HasColumnName("cost_usd").HasColumnType("TEXT");
            e.Property(x => x.InputTokens).HasColumnName("input_tokens");
            e.Property(x => x.OutputTokens).HasColumnName("output_tokens");
            e.Property(x => x.CacheReadTokens).HasColumnName("cache_read_tokens");
            e.Property(x => x.CacheWriteTokens).HasColumnName("cache_write_tokens");
            e.Property(x => x.Data).HasColumnName("data");
        });

        modelBuilder.Entity<MessageRow>(e =>
        {
            e.ToTable("run_message");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.RunId).HasColumnName("run_id");
            e.Property(x => x.Ts).HasColumnName("ts");
            e.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>();
            e.Property(x => x.FromAgent).HasColumnName("from_agent");
            e.Property(x => x.ToAgent).HasColumnName("to_agent");
            e.Property(x => x.Task).HasColumnName("task");
            e.Property(x => x.Data).HasColumnName("data");
        });

        modelBuilder.Entity<PhaseRow>(e =>
        {
            e.ToTable("run_phase");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.RunId).HasColumnName("run_id");
            e.Property(x => x.Task).HasColumnName("task");
            e.Property(x => x.Ts).HasColumnName("ts");
            e.Property(x => x.Stage).HasColumnName("stage");
            e.Property(x => x.Agent).HasColumnName("agent");
            e.Property(x => x.Round).HasColumnName("round");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.Cause).HasColumnName("cause").HasConversion<string>();
            e.Property(x => x.Data).HasColumnName("data");
        });

        modelBuilder.Entity<SettingsRow>(e =>
        {
            e.ToTable("app_settings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.Data).HasColumnName("data");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });
    }
}
