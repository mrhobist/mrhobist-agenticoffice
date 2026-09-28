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
    private sealed class Prices(IReadOnlyDictionary<string, ModelPrice> prices) : IModelCatalog
    {
        public Task<IReadOnlyList<CatalogModel>> LoadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CatalogModel>>([]);

        public Task<IReadOnlyDictionary<string, ModelPrice>> LoadPricesAsync(CancellationToken ct) => Task.FromResult(prices);
    }

    private const string Haiku = "claude-haiku-4-5-20251001";

    private async Task<MarkdownAgentStore> DeveloperWithExplorerAsync()
    {
        var store = new MarkdownAgentStore(_fx.Paths);
        var team = await store.LoadTeamAsync(Ct);
        await store.SaveAgentAsync(team.Agents["developer"] with { ExploreModel = Haiku }, Ct);
        return store;
    }

    /// <summary>
    /// Kesif alt ajani (2026-09-26): karar .NET'te. md'de <c>explore_model</c> olan ajanin ARACLI turuna salt okunur alt ajan ve
    /// <c>Agent</c> araci eklenir; aracsiz turda ve md istemiyorsa istek eskisi gibidir. Model basina kirilim tur kaydina girer.
    /// </summary>
    [Fact]
    public async Task Kesif_alt_ajani_yalniz_aracli_turda_ve_md_isterse_acilir_kirilim_kayda_girer()
    {
        var store = await DeveloperWithExplorerAsync();
        var seen = new List<RuntimeTurnRequest>();
        var runtime = new Runtime((i, r, ct) =>
        {
            seen.Add(r);
            return Task.FromResult(Ok(0.6m) with
            {
                ModelUsage = [new RuntimeModelUsage(RunDefaults.Model, 100, 5, 0, 0, 0.5m), new RuntimeModelUsage(Haiku, 900, 40, 0, 0, 0.1m)],
            });
        });
        var caller = new AgentCaller(store, runtime, _fx.Runs, new NullScene(), RetryPolicy.None);
        var run = await NewRunAsync();

        await caller.CallAsync(run, "developer", [new RuntimeMessage("user", "ara")], null, null, "t1", null, Ct, Tools());
        await caller.CallAsync(run, "developer", [new RuntimeMessage("user", "plan")], null, null, "t1", null, Ct);
        await caller.CallAsync(run, "manager", [new RuntimeMessage("user", "incele")], null, null, "t1", null, Ct, Tools());

        var sub = Assert.Single(seen[0].Subagents!);
        Assert.Equal((Explorer.Name, Haiku, ToolAccess.ReadOnly), (sub.Key, sub.Value.Model, sub.Value.Tools));
        Assert.Contains(Explorer.AgentTool, seen[0].Tools!);
        Assert.DoesNotContain("Write", sub.Value.Tools);
        Assert.Null(seen[1].Subagents); // aracsiz tur: alt ajan yok
        Assert.Null(seen[2].Subagents); // md istemiyor
        Assert.DoesNotContain(Explorer.AgentTool, seen[2].Tools!);

        var turn = (await _fx.Runs.ReadTurnsAsync(run.Id, "developer", Ct))[0];
        Assert.Equal(0.6m, turn.CostUsd); // toplam ustte, pay kirilimda
        Assert.Equal([(RunDefaults.Model, 0.5m), (Haiku, 0.1m)], turn.ModelUsage!.Select(m => (m.Model, m.CostUsd ?? 0m)));

        // md'ye yazilir ve geri okunur (UI'dan kaydetmede korunur: Compose `with` kullanir).
        Assert.Equal(Haiku, (await store.LoadTeamAsync(Ct)).Agents["developer"].ExploreModel);
    }

    /// <summary>Kesilen turda alt ajanin mesajlari kendi modelinin fiyatiyla sayilir: Opus fiyatiyla sayilsaydi 4 kat yazilirdi.</summary>
    [Fact]
    public async Task Kesilen_turda_alt_ajanin_harcamasi_kendi_fiyatiyla_sayilir()
    {
        var store = await DeveloperWithExplorerAsync();
        var progress = new ProgressRegistry(new NullScene()) { BaseUrl = "http://127.0.0.1:5080/api/v1/progress" };
        var runtime = new Runtime((i, r, ct) =>
        {
            progress.Report(TokenOf(r), new ProgressEvent(Kind: "usage", MessageId: "m1", Usage: new RuntimeUsage(1_000_000, 0, 0), Model: RunDefaults.Model));
            progress.Report(TokenOf(r), new ProgressEvent(Kind: "usage", MessageId: "m2", Usage: new RuntimeUsage(1_000_000, 0, 0), Model: Haiku));
            throw new InvalidOperationException("saglayici hatasi");
        });
        var prices = new Dictionary<string, ModelPrice> { [RunDefaults.Model] = new(4m, 20m, 0.2m, 8m), [Haiku] = new(1m, 5m, 0.1m, 2m) };
        var caller = new AgentCaller(store, runtime, _fx.Runs, new NullScene(), RetryPolicy.None, progress: progress, catalog: new Prices(prices));
        var run = await NewRunAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => caller.CallAsync(run, "developer", [new RuntimeMessage("user", "yap")], null, null, "t1", null, Ct, Tools()));

        var turn = Assert.Single(await _fx.Runs.ReadTurnsAsync(run.Id, "developer", Ct));
        Assert.True(turn.CutShort);
        Assert.Equal(5m, turn.CostUsd); // 1M girdi × 4 $ + 1M girdi × 1 $ (tek fiyatla 8 $ olurdu)
        Assert.Equal(2, turn.ModelUsage!.Count);
    }

    /// <summary>
    /// Kopyalar (docs/DOMAIN.md → Kopyalar): kilit kopya basinadir; ayni ajanin iki kopyasi ayni anda LLM cagrisinda olur. Tur kaydinda
    /// ajan md anahtari kalir, kopya ayri alanda. Baska ajanin kopya kimligi reddedilir.
    /// </summary>
    [Fact]
    public async Task Ayni_ajanin_iki_kopyasi_ayni_anda_calisir_kayitta_kopya_ayri()
    {
        var bothIn = new TaskCompletionSource();
        var entered = 0;
        var runtime = new Runtime(async (i, r, ct) =>
        {
            if (Interlocked.Increment(ref entered) == 2)
            {
                bothIn.SetResult();
            }

            // Ikisi de iceride olmadan kimse cikmaz: kilit ajan basina olsaydi ikinci hic giremez, test zaman asimina duserdi.
            await bothIn.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return Ok(0.1m);
        });
        var caller = new AgentCaller(new MarkdownAgentStore(_fx.Paths), runtime, _fx.Runs, new NullScene(), RetryPolicy.None);
        var run = await NewRunAsync();

        var a = caller.CallAsync(run, "developer", [new RuntimeMessage("user", "bir")], null, null, "t1", null, Ct, Tools());
        var b = caller.CallAsync(run, "developer", [new RuntimeMessage("user", "iki")], null, null, "t2", null, Ct, Tools(), worker: "developer~2");
        await Task.WhenAll(a, b);

        var turns = await _fx.Runs.ReadTurnsAsync(run.Id, "developer", Ct);
        Assert.Equal([null, "developer~2"], turns.Select(t => t.Worker).Order());
        await Assert.ThrowsAsync<ArgumentException>(() => caller.CallAsync(run, "developer", [new RuntimeMessage("user", "x")], null, null, "t3", null, Ct, worker: "manager~2"));
    }

    [Fact]
    public void Kopya_ayirma_atomik_ayni_kopya_iki_kez_ayrilmaz()
    {
        var w = $"ayirma-{Guid.NewGuid():N}~2";
        Assert.True(AgentCaller.TryReserve(w));
        Assert.False(AgentCaller.TryReserve(w));
        Assert.Contains(w, AgentCaller.BusyAgents);
        AgentCaller.Unreserve(w);
        Assert.DoesNotContain(w, AgentCaller.BusyAgents);
    }
}
