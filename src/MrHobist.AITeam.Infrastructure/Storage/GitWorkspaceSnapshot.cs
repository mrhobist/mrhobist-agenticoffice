using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MrHobist.AITeam.Application.Abstractions;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary>
/// Golge git deposuyla anlik goruntu (ornek: opencode <c>snapshot</c>). Depo proje dizininin DISINDA durur --
/// <c>data/snapshots/{dizinin hash'i}</c> -- ve git'e <c>--git-dir</c> ile verilir; <c>--work-tree</c> proje kokudur.
/// Sonuc: projenin kendi <c>.git</c>'i, dallari, staging alani ve <c>.gitignore</c>'u ETKILENMEZ; proje hic git
/// deposu olmasa da calisir.
///
/// Bu sinif <see cref="IWorkspaceSnapshot"/>'in tamamidir: yama/diff/budama alinmadi (opencode'da 800 satir).
/// Gereken iki islem var: bir turdan once hali kaydet, kullanici isterse o hale don.
/// </summary>
public sealed class GitWorkspaceSnapshot(StoragePaths paths, ILogger<GitWorkspaceSnapshot>? logger = null) : IWorkspaceSnapshot
{
    /// <summary>Git cagrilari kisa surer; asilan bir surec calismayi kilitlemesin.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ILogger _log = logger ?? NullLogger<GitWorkspaceSnapshot>.Instance;

    public async Task<string?> TrackAsync(string workDir, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
        {
            return null;
        }

        try
        {
            var gitDir = GitDirFor(workDir);
            if (!Directory.Exists(Path.Combine(gitDir, "objects")))
            {
                Directory.CreateDirectory(gitDir);
                if (!await GitAsync(gitDir, workDir, ct, "init", "--quiet").ConfigureAwait(false))
                {
                    return null;
                }
            }

            // -A: silmeler de girsin. --force: projenin .gitignore'u anlik goruntuyu delmesin (bin/obj disarida kalirsa
            // geri donus yarim olur). Bos degisiklikte bile commit uretilsin diye --allow-empty.
            if (!await GitAsync(gitDir, workDir, ct, "add", "-A", "--force").ConfigureAwait(false))
            {
                return null;
            }

            var stamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            if (!await GitAsync(gitDir, workDir, ct, "commit", "--quiet", "--allow-empty", "-m", stamp).ConfigureAwait(false))
            {
                return null;
            }

            var head = await GitOutputAsync(gitDir, workDir, ct, "rev-parse", "HEAD").ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(head) ? null : head.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            SnapshotLog.TrackFailed(_log, ex, workDir);
            return null; // Anlik goruntu bir kolayliktir; alinamamasi calismayi durdurmaz.
        }
    }

    public async Task<bool> RestoreAsync(string workDir, string snapshot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workDir) || string.IsNullOrWhiteSpace(snapshot) || !Directory.Exists(workDir))
        {
            return false;
        }

        try
        {
            var gitDir = GitDirFor(workDir);
            if (!Directory.Exists(Path.Combine(gitDir, "objects")))
            {
                return false; // Hic goruntu alinmamis: donulecek hal yok.
            }

            // Once izlenen dosyalari o haline getir, sonra o gorunturde olmayanlari sil (-x: gitignore'dakiler de).
            return await GitAsync(gitDir, workDir, ct, "reset", "--hard", "--quiet", snapshot).ConfigureAwait(false)
                && await GitAsync(gitDir, workDir, ct, "clean", "-fdx", "--quiet").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            SnapshotLog.RestoreFailed(_log, ex, workDir, snapshot);
            return false;
        }
    }

    /// <summary>Dizin yolundan tureyen sabit klasor adi: farkli projeler ayri depolarda, ayni proje hep ayni depoda.</summary>
    private string GitDirFor(string workDir)
    {
        var full = Path.GetFullPath(workDir).TrimEnd(Path.DirectorySeparatorChar);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..16];
        return Path.Combine(paths.DataRoot, "snapshots", hash);
    }

    private async Task<bool> GitAsync(string gitDir, string workTree, CancellationToken ct, params string[] args)
        => await GitOutputAsync(gitDir, workTree, ct, args).ConfigureAwait(false) is not null;

    /// <summary>Basarisizsa <c>null</c>, basarililiysa stdout. Kimlik ayarla verilir: makinenin git kimligi gerekmez.</summary>
    private async Task<string?> GitOutputAsync(string gitDir, string workTree, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workTree,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("--git-dir");
        psi.ArgumentList.Add(gitDir);
        psi.ArgumentList.Add("--work-tree");
        psi.ArgumentList.Add(workTree);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("user.name=AITeam");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("user.email=aiteam@localhost");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.autocrlf=false"); // Satir sonu cevrimi anlik goruntuyu bozmasin.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.longpaths=true");
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return null; // git kurulu degil.
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var stdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(proc);
            SnapshotLog.GitTimedOut(_log, string.Join(' ', args), Timeout.TotalSeconds);
            return null;
        }

        if (proc.ExitCode == 0)
        {
            return await stdout.ConfigureAwait(false);
        }

        SnapshotLog.GitFailed(_log, string.Join(' ', args), proc.ExitCode, (await stderr.ConfigureAwait(false)).Trim());
        return null;
    }

    private static void TryKill(Process proc)
    {
        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Zaten bitmis.
        }
    }
}

/// <summary>Kaynak uretimli log mesajlari (CA1848).</summary>
internal static partial class SnapshotLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "anlik goruntu alinamadi: {WorkDir}")]
    public static partial void TrackFailed(ILogger logger, Exception ex, string workDir);

    [LoggerMessage(Level = LogLevel.Warning, Message = "anlik goruntuye donulemedi: {WorkDir} -> {Snapshot}")]
    public static partial void RestoreFailed(ILogger logger, Exception ex, string workDir, string snapshot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "git {Args} {Seconds} sn icinde bitmedi")]
    public static partial void GitTimedOut(ILogger logger, string args, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "git {Args} basarisiz ({Code}): {Error}")]
    public static partial void GitFailed(ILogger logger, string args, int code, string error);
}
