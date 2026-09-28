using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Runs;
using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>Kim ne harcadi (2026-09-23): kota ortak; CLI kayitlarinin giris noktasi kaynagi ayirir, $ payi kotayi boler.</summary>
public sealed class SpendReaderTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class Runtime(IReadOnlyList<RuntimeLocalUsage> usage, double? weekly) : IAgentRuntimeService
    {
        public DateTimeOffset? Since { get; private set; }

        public Task<RuntimeMcpProbe> ProbeMcpAsync(RuntimeMcpServer server, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<RuntimeLocalUsage>> ListLocalUsageAsync(DateTimeOffset since, DateTimeOffset? until, CancellationToken ct)
        {
            Since = since;
            return Task.FromResult(usage);
        }

        public Task<IReadOnlyList<RuntimeProviderLimits>> ListLimitsAsync(Provider? provider, bool refresh, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RuntimeProviderLimits>>(weekly is null ? [] :
                [new RuntimeProviderLimits(Provider.Anthropic, true, "", null, null, [new RuntimeUsageLimit("weekly_all", null, weekly.Value, null, new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero), null, true)])]);

        public Task<RuntimeTurnResponse> TurnAsync(RuntimeTurnRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<RuntimeModelInfo>> ListModelsAsync(Provider? provider, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<RuntimeAuthStatus>> ListAuthAsync(Provider? provider, bool refresh, CancellationToken ct) => throw new NotSupportedException();

        public Task<RuntimeLoginStarted> LoginAsync(Provider provider, string mode, string? email, string? apiKey, CancellationToken ct) => throw new NotSupportedException();

        public Task<RuntimeAuthStatus> LogoutAsync(Provider provider, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Prices : IModelCatalog
    {
        public Task<IReadOnlyList<CatalogModel>> LoadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CatalogModel>>([]);

        public Task<IReadOnlyDictionary<string, ModelPrice>> LoadPricesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, ModelPrice>>(new Dictionary<string, ModelPrice> { ["claude-opus-5-5"] = new(4m, 20m, 0.2m, 8m) });
    }

    [Fact]
    public async Task Ofis_ve_oturumlar_ayrilir_kota_dolar_payina_bolunur_pencere_haftanin_basindan()
    {
        using var fx = new StorageFixture();
        var store = fx.Runs;
        var runtime = new Runtime(
        [
            // 1M cikti = 20 $
            new("sdk-py", "C--Hedef", "claude-opus-5-5", 10, 0, 1_000_000, 0, 0),
            // 0.5M cikti = 10 $ (iki ayri etkilesimli giris noktasi, tek kaynakta toplanir)
            new("claude-desktop", "C--Ofis", "claude-opus-5-5", 5, 0, 300_000, 0, 0),
            new("cli", "C--Baska", "claude-opus-5-5", 5, 0, 200_000, 0, 0),
        ], weekly: 90);
        var report = await new SpendReader(runtime, store, new Prices()).GetAsync(null, null, Ct);

        Assert.Equal(new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero), runtime.Since); // sifirlanma − 7 gun
        var office = Assert.Single(report.Sources, s => s.Key == "office");
        var sessions = Assert.Single(report.Sources, s => s.Key == "sessions");
        Assert.Equal((20m, 60.0), (office.CostUsd, office.QuotaPoints));
        Assert.Equal((10m, 30.0), (sessions.CostUsd, sessions.QuotaPoints));
        Assert.Equal(2, sessions.Lines.Count);
    }

    [Fact]
    public async Task Fiyati_olmayan_model_haric_tutulur_ve_ayrica_soylenir_tamami_fiyatsizsa_dolar_yok()
    {
        using var fx = new StorageFixture();
        var runtime = new Runtime(
        [
            new("sdk-py", "C--Hedef", "claude-opus-5-5", 1, 0, 1_000_000, 0, 0),
            new("sdk-py", "C--Hedef", "claude-sonnet-5", 1, 10, 10, 0, 0),
            new("cli", "C--Baska", "claude-sonnet-5", 1, 10, 10, 0, 0),
        ], weekly: 50);
        var report = await new SpendReader(runtime, fx.Runs, new Prices()).GetAsync(null, null, Ct);

        var office = Assert.Single(report.Sources, s => s.Key == "office");
        var sessions = Assert.Single(report.Sources, s => s.Key == "sessions");
        Assert.Equal((20m, 50.0), (office.CostUsd, office.QuotaPoints));
        Assert.Equal(["claude-sonnet-5"], office.UnpricedModels);
        Assert.Null(sessions.CostUsd);
        Assert.Equal(0.0, sessions.QuotaPoints);
        Assert.Contains(report.Notes, n => n.Contains("claude-sonnet-5", StringComparison.Ordinal));
    }
}
