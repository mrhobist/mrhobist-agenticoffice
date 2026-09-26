using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Runs;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>AgentCaller'in tur bekcisi ve kesilen denemenin harcamasi (2026-09-25 inceleme bulgulari).</summary>
[Collection(Collection)]
public sealed class AgentCallerTests : IDisposable
{
    /// <summary>
    /// AgentCaller'in ajan kilitleri (AgentLocks, BusyAgents) statiktir: "developer" ile tur kosan siniflar paralel kosarsa
    /// biri kilidi tutarken digerinin dagitimi "ajan mesgul" sanip Running'de kalir. Ayni koleksiyon = sirayla.
    /// </summary>
    public const string Collection = "agent-locks";

    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    /// <summary>Yalniz <see cref="TurnAsync"/>'i olan runtime; davranis testte verilir.</summary>
    private sealed class Runtime(Func<int, RuntimeTurnRequest, CancellationToken, Task<RuntimeTurnResponse>> turn) : IAgentRuntimeService
    {
        private int _calls;

        public Task<RuntimeTurnResponse> TurnAsync(RuntimeTurnRequest request, CancellationToken ct) => turn(Interlocked.Increment(ref _calls) - 1, request, ct);

        public Task<IReadOnlyList<RuntimeModelInfo>> ListModelsAsync(Provider? provider, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<RuntimeAuthStatus>> ListAuthAsync(Provider? provider, bool refresh, CancellationToken ct) => throw new NotSupportedException();

        public Task<RuntimeLoginStarted> LoginAsync(Provider provider, string mode, string? email, string? apiKey, CancellationToken ct) => throw new NotSupportedException();

        public Task<RuntimeAuthStatus> LogoutAsync(Provider provider, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<RuntimeProviderLimits>> ListLimitsAsync(Provider? provider, bool refresh, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<RuntimeLocalUsage>> ListLocalUsageAsync(DateTimeOffset since, DateTimeOffset? until, CancellationToken ct) => throw new NotSupportedException();

        public Task<RuntimeMcpProbe> ProbeMcpAsync(RuntimeMcpServer server, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NullScene : ISceneEventPublisher
    {
        public void Publish(string type, string json)
        {
        }
    }

    private sealed class FixedPrices(ModelPrice price) : IModelCatalog
    {
        public Task<IReadOnlyList<CatalogModel>> LoadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CatalogModel>>([]);

        public Task<IReadOnlyDictionary<string, ModelPrice>> LoadPricesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, ModelPrice>>(new Dictionary<string, ModelPrice> { [RunDefaults.Model] = price });
    }

    private static RuntimeTurnResponse Ok(decimal cost)
        => new("tamam", null, Provider.Anthropic, RunDefaults.Model, Destination.Anthropic, new RuntimeUsage(10, 5, 0), cost, 0.1, 1);

    private static string TokenOf(RuntimeTurnRequest r) => r.ProgressUrl![(r.ProgressUrl!.LastIndexOf('/') + 1)..];

    private async Task<Run> NewRunAsync()
    {
        var run = new Run($"20260925-{Guid.NewGuid():N}"[..24], "test", "brief", Sensitivity.Anthropic, DateTimeOffset.UtcNow, RunStatus.Running);
        await _fx.Runs.CreateAsync(run, Ct);
        return run;
    }

    private ToolAccess Tools() => new(ToolAccess.ReadOnly, _fx.Root, 5);

    /// <summary>
    /// Ajan baska bir turda mesgulken bekleyen ikinci cagri: kilit beklemesi hareketsizlik sayilmamali. Once belirtec kilitten
    /// ONCE kaydediliyordu; bekleme esigi asinca ikinci tur daha hicbir sey yapmadan ilk yoklamada kesiliyordu.
    /// </summary>
    [Fact]
    public async Task Ajan_kilidini_beklemek_hareketsizlik_sayilmaz()
    {
        var progress = new ProgressRegistry(new NullScene()) { BaseUrl = "http://127.0.0.1:5080/api/v1/progress" };
        var firstStarted = new TaskCompletionSource();
        var runtime = new Runtime(async (i, r, ct) =>
        {
            if (i == 0)
            {
                firstStarted.SetResult();
                // Ilk tur canli: 700 ms boyunca hareket bildirir (esik 300 ms), sonra biter.
                for (var n = 0; n < 14; n++)
                {
                    progress.Report(TokenOf(r), new ProgressEvent("Read", "a.cs"));
                    await Task.Delay(50, ct);
                }
            }

            return Ok(0.1m);
        });
        var caller = new AgentCaller(new MarkdownAgentStore(_fx.Paths), runtime, _fx.Runs, new NullScene(), RetryPolicy.None, progress: progress,
            watch: new TurnWatch(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50)));
        var run = await NewRunAsync();

        var first = caller.CallAsync(run, "developer", [new RuntimeMessage("user", "bir")], null, null, "t1", null, Ct, Tools());
        await firstStarted.Task;
        var second = caller.CallAsync(run, "developer", [new RuntimeMessage("user", "iki")], null, null, "t2", null, Ct, Tools());

        Assert.Equal("tamam", (await first).Text);
        Assert.Equal("tamam", (await second).Text); // eskiden: RuntimeTimeoutException ("hiç hareket etmedi")
    }

    /// <summary>
    /// Gecici hatayla dusen deneme harcamisti; tekrar basarili olunca yalniz son denemenin maliyeti yaziliyordu (CLAUDE.md §4).
    /// Artik dusen deneme kendi (kesilmis) turu olarak kayda girer ve maliyeti yanitla birlikte calismaya tasinir; ayni harcama
    /// ikinci denemede yeniden sayilmaz.
    /// </summary>
    [Fact]
    public async Task Tekrar_edilen_denemenin_harcamasi_kaybolmaz_iki_kez_de_sayilmaz()
    {
        var progress = new ProgressRegistry(new NullScene()) { BaseUrl = "http://127.0.0.1:5080/api/v1/progress" };
        var runtime = new Runtime((i, r, ct) =>
        {
            if (i == 0)
            {
                progress.Report(TokenOf(r), new ProgressEvent(Kind: "usage", MessageId: "m1", Usage: new RuntimeUsage(1_110_000, 50_000, 0, 1_000_000, 100_000)));
                throw new RuntimeUnavailableException("runtime kapali");
            }

            return Task.FromResult(Ok(0.5m));
        });
        var caller = new AgentCaller(new MarkdownAgentStore(_fx.Paths), runtime, _fx.Runs, new NullScene(), new RetryPolicy(2, TimeSpan.Zero), progress: progress,
            catalog: new FixedPrices(new ModelPrice(4m, 20m, 0.2m, 8m)));
        var run = await NewRunAsync();

        var reply = await caller.CallAsync(run, "developer", [new RuntimeMessage("user", "yap")], null, null, "t1", null, Ct, Tools());

        Assert.Equal(2.54m, reply.CostUsd); // 2.04 (dusen deneme, fiyattan) + 0.5 (basarili)
        Assert.Equal(1_110_010, reply.InputTokens);
        var turns = await _fx.Runs.ReadTurnsAsync(run.Id, "developer", Ct);
        Assert.Equal([(true, 2.04m), (false, 0.5m)], turns.Select(t => (t.CutShort == true, t.CostUsd ?? 0m)));
        Assert.Empty(progress.Snapshot(run.Id));
    }
}
