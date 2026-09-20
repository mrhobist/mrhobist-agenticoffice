using System.Diagnostics;
using MrHobist.AITeam.Infrastructure.Runtime;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.Api.Runtime;

/// <summary>
/// Api ayaga kalkarken Python runtime'i (<c>runtime/</c>, FastAPI) da baslatir; Api kapanirken durdurur.
/// Tek makinede iki terminal acmak zorunda kalmamak icin (CLAUDE.md → Sapmalar: tek kullanici, tek makine).
/// <para>
/// <b>Sinirlar.</b> Sahiplik: 5090'da zaten saglikli bir runtime varsa ona <b>dokunulmaz</b> ve kapanista
/// oldurulmez (elle baslatilmis olabilir). Python yoksa ya da <c>.venv</c> bu cihazda calismiyorsa Api
/// <b>yine de kalkar</b>: uyari gunluge yazilir, UI zaten "Runtime kapali" der. Bekleme startup'i bloklamaz;
/// saglik yoklamasi arka planda kosar ki <c>dotnet run</c> gecikmesin.
/// </para>
/// Kapatmak icin <c>AITeam:AutoStartRuntime=false</c>.
/// </summary>
public sealed partial class RuntimeSupervisor(
    IConfiguration config,
    StoragePaths paths,
    IHttpClientFactory httpFactory,
    ILogger<RuntimeSupervisor> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan HealthBudget = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(400);

    private Process? owned;
    private CancellationTokenSource? confirm;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!config.GetValue("AITeam:AutoStartRuntime", true))
        {
            LogDisabled(logger);
            return;
        }

        var url = new Uri(config["AITeam:RuntimeUrl"] ?? "http://127.0.0.1:5090");

        if (await IsHealthyAsync(url, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false))
        {
            LogAlreadyUp(logger, url);
            return;
        }

        var interpreter = PythonRuntimeProcess.FindInterpreter(paths.RepoRoot);
        if (interpreter is null)
        {
            LogNoInterpreter(logger, Path.Combine(paths.RepoRoot, "runtime", ".venv"));
            return;
        }

        try
        {
            var p = PythonRuntimeProcess.Start(paths.RepoRoot, interpreter, url);
            p.OutputDataReceived += (_, e) => LogRuntimeLine(logger, e.Data ?? "");
            p.ErrorDataReceived += (_, e) => LogRuntimeLine(logger, e.Data ?? "");
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            owned = p;
            LogStarting(logger, url, interpreter);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            LogStartFailed(logger, ex.Message);
            return;
        }

        // Saglik beklemesi startup'i bloklamaz: sonucu yalniz gunluge yazariz.
        confirm = new CancellationTokenSource();
        _ = ConfirmAsync(url, confirm.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (confirm is not null)
        {
            await confirm.CancelAsync().ConfigureAwait(false);
        }

        if (owned is null)
        {
            return;
        }

        try
        {
            if (!owned.HasExited)
            {
                owned.Kill(entireProcessTree: true);
                owned.WaitForExit(5000);
            }

            LogStopped(logger);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            LogStopFailed(logger, ex.Message);
        }
    }

    private async Task ConfirmAsync(Uri url, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + HealthBudget;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (owned?.HasExited == true)
            {
                LogExitedEarly(logger, owned.ExitCode);
                return;
            }

            if (await IsHealthyAsync(url, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
            {
                LogReady(logger, url);
                return;
            }

            try
            {
                await Task.Delay(PollDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (!ct.IsCancellationRequested)
        {
            LogNotReady(logger, HealthBudget.TotalSeconds);
        }
    }

    private async Task<bool> IsHealthyAsync(Uri url, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var client = httpFactory.CreateClient();
            client.Timeout = timeout;
            using var res = await client.GetAsync(new Uri(url, "/health"), ct).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or UriFormatException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        confirm?.Dispose();
        owned?.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Runtime otomatik baslatma kapali (AITeam:AutoStartRuntime=false).")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Runtime {Url} zaten ayakta; baslatilmadi, kapanista da durdurulmayacak.")]
    private static partial void LogAlreadyUp(ILogger logger, Uri url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runtime baslatilamadi: {VenvDir} yok ya da bu cihazda calismiyor. Onarim: powershell -ExecutionPolicy Bypass -File scripts/verify.ps1 -SetupRuntime. Api calismaya devam ediyor; model cagrisi yapilamaz.")]
    private static partial void LogNoInterpreter(ILogger logger, string venvDir);

    [LoggerMessage(Level = LogLevel.Information, Message = "Runtime baslatiliyor: {Url} ({Interpreter})")]
    private static partial void LogStarting(ILogger logger, Uri url, string interpreter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runtime sureci baslatilamadi: {Reason}")]
    private static partial void LogStartFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Runtime hazir: {Url}")]
    private static partial void LogReady(ILogger logger, Uri url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runtime {Seconds} sn icinde saglikli olmadi; gunlukteki runtime satirlarina bakin.")]
    private static partial void LogNotReady(ILogger logger, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runtime sureci hemen kapandi (cikis kodu {ExitCode}). Port dolu olabilir.")]
    private static partial void LogExitedEarly(ILogger logger, int exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Runtime durduruldu.")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runtime durdurulamadi: {Reason}")]
    private static partial void LogStopFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "runtime: {Line}")]
    private static partial void LogRuntimeLine(ILogger logger, string line);
}
