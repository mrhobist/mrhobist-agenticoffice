using MrHobist.AITeam.Application.Abstractions;
using Microsoft.Data.Sqlite;
using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Infrastructure;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

public sealed class RunStoreTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static readonly CancellationToken Ct = CancellationToken.None;

    private static Run NewRun(string id) => new(id, "test", "brief", Sensitivity.Anthropic, DateTimeOffset.UtcNow, RunStatus.Running);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _fx.Paths.DatabaseFile }.ToString());
        connection.Open();
        return connection;
    }

    private List<string> ScalarList(string sql)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read()) { list.Add(reader.GetString(0)); }
        return list;
    }

    private string? Scalar(string sql)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }

    [Fact]
    public async Task Calisma_gidis_donus_kayipsiz()
    {
        var store = _fx.Runs;
        var run = NewRun("20260919-000000-test");
        await store.CreateAsync(run, Ct);
        await store.WriteSpecAsync(run.Id, new Spec("ozet", "mimari", ["kural"], [new RunTask("t1", "T1", "yap", ["a.py"], ["calisir"], [])]), Ct);
        await store.AppendTurnAsync(run.Id, new Turn(DateTimeOffset.UtcNow, "developer", "gelistirme", "t1", 1, "nvidia", "m", Destination.Nvidia, 1.5, 100, 200, 0.001m), Ct);
        await store.AppendTurnAsync(run.Id, new Turn(DateTimeOffset.UtcNow, "developer", "gelistirme", "t1", 2, "nvidia", "m", Destination.Nvidia, 1.5, 100, 200), Ct);
        await store.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Ask, "developer", "manager", "i mi l mi?", "t1", "gelistirme", "q1"), Ct);
        await store.AppendPhaseAsync(run.Id, new Phase(DateTimeOffset.UtcNow, "t1", "gelistirme", "Geliştirme", "implement", "developer", 1, PhaseStatus.Started), Ct);
        await store.UpdateAsync(run with { Status = RunStatus.Completed, TotalCostUsd = 0.001m }, Ct);

        var loaded = await store.GetAsync(run.Id, Ct);
        Assert.NotNull(loaded);
        Assert.Equal(RunStatus.Completed, loaded.Status);
        Assert.Equal(Sensitivity.Anthropic, loaded.Sensitivity);
        Assert.Equal(0.001m, loaded.TotalCostUsd);
        Assert.Equal("t1", (await store.ReadSpecAsync(run.Id, Ct))!.Tasks[0].Id);
        Assert.Equal(2, (await store.ReadTurnsAsync(run.Id, "developer", Ct)).Count);
        Assert.Equal(["developer"], await store.ListConversationsAsync(run.Id, Ct));
        Assert.Equal(MessageKind.Ask, (await store.ReadMessagesAsync(run.Id, Ct))[0].Kind);
        Assert.Equal(PhaseStatus.Started, (await store.ReadPhasesAsync(run.Id, "t1", Ct))[0].Status);
        Assert.Equal(["t1"], await store.ListTasksAsync(run.Id, Ct));
        Assert.Single(await store.ListAsync(10, Ct));

        // Enum'lar sutunda ADIYLA durur (CLAUDE.md §5): sema sayiya donmez, yeni uye eski satiri bozmaz.
        Assert.Equal("Completed", Scalar($"SELECT status FROM run WHERE id = '{run.Id}'"));
        Assert.Equal("Nvidia", Scalar($"SELECT destination FROM run_turn WHERE run_id = '{run.Id}' LIMIT 1"));
    }

    [Fact]
    public async Task Append_edilen_satirlar_ekleme_sirasiyla_okunur()
    {
        var store = _fx.Runs;
        var run = NewRun("20260919-000002-seq");
        await store.CreateAsync(run, Ct);
        foreach (var body in new[] { "bir", "iki", "uc" })
        {
            await store.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, "a", "b", body), Ct);
        }

        // Ayni ts ile yazilsalar bile sira korunur: siralama zaman damgasina degil AUTOINCREMENT id'ye dayanir.
        Assert.Equal(["bir", "iki", "uc"], (await store.ReadMessagesAsync(run.Id, Ct)).Select(m => m.Body));
        Assert.Equal("3", Scalar($"SELECT COUNT(*) FROM run_message WHERE run_id = '{run.Id}'"));
    }

    [Fact]
    public async Task Bozuk_govde_yok_sayilir_geri_kalani_kurtarilir()
    {
        var store = _fx.Runs;
        var run = NewRun("20260919-000001-bozuk");
        await store.CreateAsync(run, Ct);
        await store.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, "a", "b", "tam"), Ct);

        // JSONL doneminde "yarim yazilmis son satir"; simdi yarim JSON govdesi. Kural ayni: yok say, kalani kurtar.
        using (var connection = Open())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO run_message (run_id, ts, kind, from_agent, to_agent, data)
                VALUES ($id, '2026-09-19T00:00:00+00:00', 'Note', 'a', 'b', '{"ts":"2026-09-19T00:00:00Z","kind":"Note"');
                """;
            cmd.Parameters.AddWithValue("$id", run.Id);
            cmd.ExecuteNonQuery();
        }

        var messages = await store.ReadMessagesAsync(run.Id, Ct);
        Assert.Single(messages);
        Assert.Equal("tam", messages[0].Body);
    }

    [Fact]
    public async Task Calisma_kimligi_dosya_adina_donmez()
    {
        // Dosya tabanli depoda bu kimlik runs/ disina cikma denemesiydi ve temizlenmesi gerekiyordu.
        // Kimlik artik bir sutun degeri: temizleme YOK, kayip da yok -- bu hata sinifi tamamen kalkti.
        var store = _fx.Runs;
        await store.CreateAsync(NewRun("../../kacis"), Ct);

        var loaded = await store.GetAsync("../../kacis", Ct);
        Assert.NotNull(loaded);
        Assert.Equal("../../kacis", loaded.Id);
        Assert.False(Directory.Exists(Path.Combine(_fx.Root, "kacis")));
    }

    [Fact]
    public async Task Calisma_silinince_alt_satirlar_da_duser()
    {
        var store = _fx.Runs;
        var run = NewRun("20260919-000003-sil");
        await store.CreateAsync(run, Ct);
        await store.AppendTurnAsync(run.Id, new Turn(DateTimeOffset.UtcNow, "developer", null, null, null, "nvidia", "m", Destination.Nvidia, 1, 1, 1), Ct);
        await store.AppendMessageAsync(run.Id, new Message(DateTimeOffset.UtcNow, MessageKind.Note, "a", "b", "x"), Ct);
        await store.AppendPhaseAsync(run.Id, new Phase(DateTimeOffset.UtcNow, "t1", "s", "S", "implement", "developer", 1, PhaseStatus.Done), Ct);

        await store.DeleteAsync(run.Id, Ct);

        Assert.Null(await store.GetAsync(run.Id, Ct));
        Assert.Equal("0", Scalar($"SELECT COUNT(*) FROM run_turn WHERE run_id = '{run.Id}'"));
        Assert.Equal("0", Scalar($"SELECT COUNT(*) FROM run_message WHERE run_id = '{run.Id}'"));
        Assert.Equal("0", Scalar($"SELECT COUNT(*) FROM run_phase WHERE run_id = '{run.Id}'"));
    }

    [Fact]
    public void Sema_betikleri_deftere_yazilir()
    {
        // Defter bos olsaydi her kalkis semayi yeniden kosmaya calisirdi (ARCHITECTURE.md §8.2).
        Assert.Equal(
            [
                "0001_create_core_tables.sql",
                "0002_add_cache_tokens_to_run_turn.sql",
                "0003_project_budget_and_run_tokens.sql",
            ],
            ScalarList("SELECT script_name FROM schema_change_log ORDER BY script_name"));
        Assert.Equal("wal", Scalar("PRAGMA journal_mode"));
    }

    [Fact]
    public void Sema_ikinci_kez_kosmaz_degismis_betik_kalkisi_durdurur()
    {
        // Idempotent: defterdeki betik yeniden kosmaz.
        Assert.Equal(0, DependencyInjection.MigrateDatabase(_fx.Paths));

        // Uygulanmis betik degistiyse (checksum uyusmazligi) kalkis DURUR -- CLAUDE.md §2'nin garantisi.
        using (var connection = Open())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE schema_change_log SET checksum = 'bozuk'";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<InvalidOperationException>(() => DependencyInjection.MigrateDatabase(_fx.Paths));
        Assert.Contains("0001_create_core_tables.sql", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Farkli_bicimde_yazilmis_zaman_damgasi_okumayi_dusurmez()
    {
        // Okuma toleransli: elle SQL ile ofsetli ya da kesirsiz yazilan damga FormatException uretmez, UTC'ye cekilir.
        var store = _fx.Runs;
        await store.CreateAsync(NewRun("20260919-000004-damga"), Ct);
        using (var connection = Open())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE run SET finished_at = '2026-09-21T10:00:00+03:00' WHERE id = '20260919-000004-damga'";
            cmd.ExecuteNonQuery();
        }

        var loaded = await store.GetAsync("20260919-000004-damga", Ct);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 7, 0, 0, TimeSpan.Zero), loaded!.FinishedAt);
        Assert.Single(await store.ListAsync(10, Ct));
    }

    [Fact]
    public async Task Kullanim_ozeti_tek_sorguda_yalniz_toplama_alanlarini_okur()
    {
        var store = _fx.Runs;
        await store.CreateAsync(NewRun("20260919-000005-k1"), Ct);
        await store.CreateAsync(NewRun("20260919-000006-k2"), Ct);
        await store.AppendTurnAsync("20260919-000005-k1", new Turn(DateTimeOffset.UtcNow, "developer", null, null, null, "anthropic", "opus", Destination.Anthropic, 1, 10, 10, 0.5m, "COK UZUN PROMPT", "COK UZUN CIKTI", 100, 200), Ct);
        await store.AppendTurnAsync("20260919-000006-k2", new Turn(DateTimeOffset.UtcNow, "tester", null, null, null, "anthropic", "opus", Destination.Anthropic, 1, 10, 10, 0.25m, null, null, 50, 60), Ct);

        var usage = await store.ReadUsageAsync(1, Ct); // yalniz en yeni calisma
        var only = Assert.Single(usage);
        Assert.Equal(("20260919-000006-k2", "anthropic", "opus", 50, 60, 0.25m), (only.RunId, only.Provider, only.Model, only.InputTokens, only.OutputTokens, only.CostUsd));
        Assert.Equal(2, (await store.ReadUsageAsync(10, Ct)).Count);
    }

    /// <summary>
    /// Kalibrasyon ornegi yalniz ARACSIZ turdur (arac tanimlari istemde gorunmeyen ~20k token ekler), ayni
    /// saglayici+model, en yeni once. Bayragi olmayan eski satir ornege girmez. Baglam olcusu de gidis-donus kayipsiz.
    /// </summary>
    [Fact]
    public async Task Kalibrasyon_ornegi_yalniz_aracsiz_turlardan_gelir()
    {
        var store = _fx.Runs;
        var run = NewRun("20260923-000001-kalibre");
        await store.CreateAsync(run, Ct);
        Turn T(string model, int chars, int tokens, bool? tools, int? turns = 2, ContextStats? ctx = null)
            => new(DateTimeOffset.UtcNow, "developer", null, null, null, "anthropic", model, Destination.Anthropic, 1, chars, 10, null, "p", "o", tokens, 5, null, turns, ToolsOffered: tools, Context: ctx);
        await store.AppendTurnAsync(run.Id, T("opus", 1_000, 5_000, false), Ct);
        await store.AppendTurnAsync(run.Id, T("opus", 2_000, 90_000, true), Ct);          // aracli: disarida
        await store.AppendTurnAsync(run.Id, T("opus", 3_000, 6_000, null), Ct);           // eski satir: disarida
        await store.AppendTurnAsync(run.Id, T("sonnet", 4_000, 6_500, false), Ct);        // baska model: disarida
        await store.AppendTurnAsync(run.Id, T("opus", 5_000, 0, false), Ct);              // token yok: disarida
        var ctx = new ContextStats(10, 40_000, 4, 9_000, 3.1, 12);
        await store.AppendTurnAsync(run.Id, T("opus", 6_000, 7_000, false, turns: null, ctx: ctx), Ct);

        var samples = await store.ReadCalibrationSamplesAsync("anthropic", "opus", 10, Ct);

        Assert.Equal([new CalibrationSample(6_000, 7_000, 1), new CalibrationSample(1_000, 5_000, 2)], samples); // en yeni once; tur yoksa 1
        Assert.Equal(ctx, (await store.ReadTurnsAsync(run.Id, "developer", Ct))[^1].Context);
    }

    [Fact]
    public async Task Onbellek_token_kirilimi_sutuna_da_yazilir()
    {
        // 0002: `input_tokens` toplamdir; kirilim ayri sutunda olmazsa "baglam bosa mi gitti" sorulamaz.
        var store = _fx.Runs;
        var run = NewRun("20260921-000001-onbellek");
        await store.CreateAsync(run, Ct);
        await store.AppendTurnAsync(run.Id, new Turn(DateTimeOffset.UtcNow, "developer", null, null, null, "anthropic", "opus",
            Destination.Anthropic, 1, 10, 10, 0.5m, null, null, 3210, 5, null, 28, CacheReadTokens: 3000, CacheWriteTokens: 200), Ct);

        var turn = Assert.Single(await store.ReadTurnsAsync(run.Id, "developer", Ct));
        Assert.Equal((3210, 3000, 200), (turn.InputTokens, turn.CacheReadTokens, turn.CacheWriteTokens));
        Assert.Equal("3000", Scalar($"SELECT cache_read_tokens FROM run_turn WHERE run_id = '{run.Id}'"));
        Assert.Equal("200", Scalar($"SELECT cache_write_tokens FROM run_turn WHERE run_id = '{run.Id}'"));
    }

    [Fact]
    public async Task Sema_ile_esleme_ayrismaz()
    {
        // Drift denetimi (ARCHITECTURE.md §8.5): betikteki sutun adi ile EF eslemesi ayrisirsa bu sorgu patlar.
        Assert.Equal(6, await _fx.Services.ProbeDatabaseAsync(Ct));
    }

    [Fact]
    public async Task Atomik_yazim_yarim_dosya_birakmaz()
    {
        // config/ tarafi (ajan md'leri, akislar, sahne) hala dosyada: atomik yazim kurali orada gecerli.
        var path = Path.Combine(_fx.Root, "atomic", "a.json");
        await AtomicFile.WriteAsync(path, "{\"a\":1}", Ct);
        await AtomicFile.WriteAsync(path, "{\"a\":2}", Ct);
        Assert.Equal("{\"a\":2}", await File.ReadAllTextAsync(path, Ct));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }
}
