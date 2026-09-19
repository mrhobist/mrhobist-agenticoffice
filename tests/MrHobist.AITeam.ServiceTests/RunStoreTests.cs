using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

public sealed class RunStoreTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static Run NewRun(string id) => new(id, "test", "brief", Sensitivity.Anthropic, DateTimeOffset.UtcNow, RunStatus.Running);

    [Fact]
    public async Task Calisma_gidis_donus_kayipsiz()
    {
        var store = new JsonlRunStore(_fx.Paths);
        var run = NewRun("20260919-000000-test");
        await store.CreateAsync(run, CancellationToken.None);
        await store.WriteSpecAsync(run.Id, new Spec("ozet", "mimari", ["kural"], [new RunTask("t1", "T1", "yap", ["a.py"], ["calisir"], [])]), CancellationToken.None);
        await store.AppendTurnAsync(run.Id, new Turn(DateTimeOffset.UtcNow, "developer", "gelistirme", "t1", 1, "nvidia", "m", Destination.Nvidia, 1.5, 100, 200, 0.001m), CancellationToken.None);
        await store.AppendTurnAsync(run.Id, new Turn(DateTimeOffset.UtcNow, "developer", "gelistirme", "t1", 2, "nvidia", "m", Destination.Nvidia, 1.5, 100, 200), CancellationToken.None);
        await store.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Ask, "developer", "manager", "i mi l mi?", "t1", "gelistirme", "q1"), CancellationToken.None);
        await store.AppendPhaseAsync(run.Id, new Phase(DateTimeOffset.UtcNow, "t1", "gelistirme", "Geliştirme", "implement", "developer", 1, PhaseStatus.Started), CancellationToken.None);
        await store.UpdateAsync(run with { Status = RunStatus.Completed, TotalCostUsd = 0.001m }, CancellationToken.None);

        var loaded = await store.GetAsync(run.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(RunStatus.Completed, loaded.Status);
        Assert.Equal(Sensitivity.Anthropic, loaded.Sensitivity);
        Assert.Equal("t1", (await store.ReadSpecAsync(run.Id, CancellationToken.None))!.Tasks[0].Id);
        Assert.Equal(2, (await store.ReadTurnsAsync(run.Id, "developer", CancellationToken.None)).Count);
        Assert.Equal(MessageKind.Ask, (await store.ReadMessagesAsync(run.Id, CancellationToken.None))[0].Kind);
        Assert.Equal(PhaseStatus.Started, (await store.ReadPhasesAsync(run.Id, "t1", CancellationToken.None))[0].Status);
        Assert.Equal(["t1"], await store.ListTasksAsync(run.Id, CancellationToken.None));
        Assert.Single(await store.ListAsync(10, CancellationToken.None));

        // Enum'lar adiyla yazilir (CLAUDE.md §5).
        var raw = await File.ReadAllTextAsync(Path.Combine(_fx.Paths.RunsRoot, run.Id, "run.json"));
        Assert.Contains("\"status\": \"Completed\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Yarim_son_satir_yok_sayilir()
    {
        var store = new JsonlRunStore(_fx.Paths);
        var run = NewRun("20260919-000001-yarim");
        await store.CreateAsync(run, CancellationToken.None);
        await store.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, "a", "b", "tam"), CancellationToken.None);
        var path = Path.Combine(_fx.Paths.RunsRoot, run.Id, "messages.jsonl");
        await File.AppendAllTextAsync(path, "{\"ts\":\"2026-09-19T00:00:00Z\",\"kind\":\"Note\",\"from\":\"a\"");

        var messages = await store.ReadMessagesAsync(run.Id, CancellationToken.None);
        Assert.Single(messages);
        Assert.Equal("tam", messages[0].Body);
    }

    [Fact]
    public async Task Runs_disina_cikilmaz_kimlik_temizlenir()
    {
        var store = new JsonlRunStore(_fx.Paths);
        await store.CreateAsync(NewRun("../../kacis"), CancellationToken.None);
        var created = Assert.Single(Directory.GetDirectories(_fx.Paths.RunsRoot));
        Assert.Equal("kacis", Path.GetFileName(created));
        Assert.False(Directory.Exists(Path.Combine(_fx.Root, "kacis")));
    }

    [Fact]
    public async Task Atomik_yazim_yarim_dosya_birakmaz()
    {
        var path = Path.Combine(_fx.Root, "atomic", "a.json");
        await AtomicFile.WriteAsync(path, "{\"a\":1}", CancellationToken.None);
        await AtomicFile.WriteAsync(path, "{\"a\":2}", CancellationToken.None);
        Assert.Equal("{\"a\":2}", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }
}
