using Microsoft.EntityFrameworkCore;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Settings;

namespace MrHobist.AITeam.Infrastructure.Persistence;

/// <summary>
/// <c>app_settings</c>: tek satir (<c>id = 1</c>), govde JSON. Satir yoksa varsayilan doner.
/// Her LLM cagrisi oncesi okunur; <c>updated_at</c> damgasi degismediyse onbellekten verilir
/// (dosya doneminde de boyleydi: stat ucuz, JSON ayristirma degil).
/// </summary>
internal sealed class SqliteSettingsStore(IDbContextFactory<AiTeamContext> factory) : ISettingsStore
{
    private sealed record Dto(Dictionary<string, int>? LimitGuards, string? CacheTtl = null);

    private (DateTimeOffset Stamp, AppSettings Value)? _cache;

    public async Task<AppSettings> LoadAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct).ConfigureAwait(false);
        if (row is null)
        {
            return AppSettings.Default;
        }

        if (_cache is { } c && c.Stamp == row.UpdatedAt)
        {
            return c.Value;
        }

        var dto = PersistenceJson.Read<Dto>(row.Data)
            ?? throw new DomainException(ErrorCodes.ConfigFileInvalid, "app_settings satiri gecersiz JSON.");

        var guards = new Dictionary<Provider, int>(AppSettings.Default.LimitGuards);
        foreach (var (key, value) in dto.LimitGuards ?? [])
        {
            if (Providers.Parse(key) is { } p)
            {
                guards[p] = value;
            }
        }

        var settings = new AppSettings(guards, string.IsNullOrWhiteSpace(dto.CacheTtl) ? null : dto.CacheTtl);
        settings.Validate();
        _cache = (row.UpdatedAt, settings);
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var dto = new Dto(settings.LimitGuards.ToDictionary(kv => Providers.Wire(kv.Key), kv => kv.Value), settings.CacheTtl);

        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Settings.FirstOrDefaultAsync(x => x.Id == 1, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new SettingsRow { Id = 1 };
            db.Settings.Add(row);
        }

        row.Data = PersistenceJson.Write(dto);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _cache = (row.UpdatedAt, settings);
    }
}
