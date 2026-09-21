using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MrHobist.AITeam.Api.Jobs;

/// <summary>Kuyruga konan is: hangi calisma (kilit anahtari), ad (log icin), govde.</summary>
public sealed record Job(string RunId, string Name, Func<CancellationToken, Task> Body);

/// <summary>
/// Is kanali (CLAUDE.md §2): uzun isler (analiz, dagitim) burada kosar; uclar hizli dogrulamayi yapip isi birakir, 202 doner.
/// Paralellik (kullanici karari 2026-09-19): farkli calismalar ayni anda ilerler (<see cref="JobWorker"/> havuzu),
/// ama <b>calisma basina tek is</b> (ayni run.json'a iki yazan olmaz) — kilit burada; <b>ajan basina tek LLM cagrisi</b>
/// AgentCaller'da. Iptal: <see cref="Cancel"/> suren isin belirtecini keser; RunService yarim sonucu yazmaz.
/// </summary>
public sealed class JobChannel
{
    private readonly Channel<Job> _channel = Channel.CreateUnbounded<Job>();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _runLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private int _pending;

    /// <summary>Kuyrukta bekleyen + kosan is sayisi (/jobs/health).</summary>
    public int Pending => Volatile.Read(ref _pending);

    public void Enqueue(string runId, string name, Func<CancellationToken, Task> body)
    {
        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(new Job(runId, name, body)))
        {
            Interlocked.Decrement(ref _pending);
            throw new InvalidOperationException("Kuyruk kapali.");
        }
    }

    /// <summary>Calismanin suren isini keser. Kuyrukta bekleyen isi RunService durumu Cancelled gorunce kendisi atlar.</summary>
    public bool Cancel(string runId)
    {
        if (_running.TryGetValue(runId, out var cts))
        {
            cts.Cancel();
            return true;
        }

        return false;
    }

    internal ChannelReader<Job> Reader => _channel.Reader;

    /// <summary>Isi calisma kilidi altinda kosar; ayni calismanin ikinci isi ilki bitene kadar bekler.</summary>
    internal async Task RunAsync(Job job, CancellationToken stoppingToken)
    {
        var gate = _runLocks.GetOrAdd(job.RunId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(stoppingToken).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _running[job.RunId] = cts;
        try
        {
            await job.Body(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(job.RunId, out _);
            gate.Release();
            Interlocked.Decrement(ref _pending);
        }
    }
}

/// <summary>
/// Havuz: <see cref="MaxParallel"/> tuketici ayni kanali okur. Bir isin hatasi digerlerini durdurmaz; iptal edilen is
/// (OperationCanceled, kullanici) sessizce biter — RunService durumu zaten Cancelled yazmistir.
/// </summary>
public sealed class JobWorker(JobChannel channel, ILogger<JobWorker> logger, IConfiguration config) : BackgroundService
{
    /// <summary><c>AITeam:MaxParallelJobs</c>; varsayilan 3. 1 = eski sirali davranis.</summary>
    public int MaxParallel { get; } = Math.Clamp(config.GetValue("AITeam:MaxParallelJobs", 3), 1, 16);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(Enumerable.Range(0, MaxParallel).Select(i => ConsumeAsync(i, stoppingToken)));

    private async Task ConsumeAsync(int slot, CancellationToken stoppingToken)
    {
        await foreach (var job in channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                Log.JobStarted(logger, job.Name, slot);
                await channel.RunAsync(job, stoppingToken).ConfigureAwait(false);
                Log.JobFinished(logger, job.Name, slot);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                Log.JobCancelled(logger, job.Name);
            }
            catch (Exception ex)
            {
                Log.JobFailed(logger, ex, job.Name);
            }
        }
    }
}

/// <summary>Kaynak uretimli log mesajlari (CA1848).</summary>
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "is basladi: {Job} (slot {Slot})")]
    public static partial void JobStarted(ILogger logger, string job, int slot);

    [LoggerMessage(Level = LogLevel.Information, Message = "is bitti: {Job} (slot {Slot})")]
    public static partial void JobFinished(ILogger logger, string job, int slot);

    [LoggerMessage(Level = LogLevel.Information, Message = "is iptal edildi: {Job}")]
    public static partial void JobCancelled(ILogger logger, string job);

    [LoggerMessage(Level = LogLevel.Error, Message = "is hata verdi: {Job}")]
    public static partial void JobFailed(ILogger logger, Exception ex, string job);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} yarim kalan calisma Interrupted isaretlendi.")]
    public static partial void Interrupted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} sema betigi uygulandi: {Database}")]
    public static partial void SchemaApplied(ILogger logger, int count, string database);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Eski dosya deposu bulundu ({Path}); calisma gecmisi artik veritabaninda, bu klasor okunmaz. Icerigini sakla ya da sil.")]
    public static partial void LegacyRunsFound(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AITeam:RunsRoot ayari artik kullanilmiyor; veritabani konumu AITeam:DataRoot ile verilir.")]
    public static partial void LegacyRunsRootSetting(ILogger logger);
}
