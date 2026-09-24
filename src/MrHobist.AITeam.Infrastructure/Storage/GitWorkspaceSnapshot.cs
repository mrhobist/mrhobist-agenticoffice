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
/// Bu sinif <see cref="IWorkspaceSnapshot"/>'in tamamidir: yama/budama alinmadi (opencode'da 800 satir).
/// Gereken islemler: bir turdan once hali kaydet, kullanici isterse o hale don; ve salt okunur iki islem --
/// iki hal arasindaki dosya farki ile bir dosyanin o haldeki metni (gorev basinda yazilan kodun ozeti, 2026-09-24).
/// </summary>
public sealed class GitWorkspaceSnapshot(StoragePaths paths, ILogger<GitWorkspaceSnapshot>? logger = null) : IWorkspaceSnapshot
{
    /// <summary>Git cagrilari kisa surer; asilan bir surec calismayi kilitlemesin.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <c>add</c> icin: ilk goruntu node_modules dahil her dosyayi hash'ler (canli projede ~20 bin dosya, ~260 MB) ve 30 sn'yi
    /// asabilir. Sonrakiler artimlidir (degismeyen dosya stat'la gecer). Oldurulen add'in biraktigi kilit bundan eskiyse sahipsizdir.
    /// </summary>
    private static readonly TimeSpan AddTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Goruntuye alinmayan ve geri donuste silinmeyen dizinler: IDE durumu (acik IDE dosyalari kilitler), kod degil.</summary>
    private static readonly string[] NeverTracked = [".vs"];

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

            // Zaman asiminda oldurulen add index.lock birakir; kalirsa sonraki HER goruntu sessizce duser.
            var indexLock = Path.Combine(gitDir, "index.lock");
            if (File.Exists(indexLock) && File.GetLastWriteTimeUtc(indexLock) < DateTime.UtcNow - AddTimeout)
            {
                File.Delete(indexLock);
            }

            // -A: silmeler de girsin. --force: projenin .gitignore'u anlik goruntuyu delmesin (bin/obj disarida kalirsa
            // geri donus yarim olur). Bos degisiklikte bile commit uretilsin diye --allow-empty. IDE durum dizinleri HARIC:
            // proje Visual Studio'da acikken .vs/…/*.vsidx kilitli, git add "Permission denied" ile TAMAMEN duser
            // (2026-09-24: canli projede 23 Eylul'den beri hic goruntu alinamamisti; geri al secenegi hic sunulmadi).
            if (await RunGitAsync(gitDir, workDir, AddTimeout, ct, ["add", "-A", "--force", "--", ".", .. NeverTracked.Select(d => $":(exclude,glob)**/{d}/**")]).ConfigureAwait(false) is null)
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

            // Once izlenen dosyalari o haline getir, sonra o gorunturde olmayanlari sil (-x: gitignore'dakiler de). Goruntuye
            // hic girmeyen IDE dizinleri (-e) silinmez: izlenmedikleri icin "goruntude yok" sayilirlardi.
            return await GitAsync(gitDir, workDir, ct, "reset", "--hard", "--quiet", snapshot).ConfigureAwait(false)
                && await GitAsync(gitDir, workDir, ct, ["clean", "-fdx", "--quiet", .. NeverTracked.SelectMany(d => new[] { "-e", d + "/" })]).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            SnapshotLog.RestoreFailed(_log, ex, workDir, snapshot);
            return false;
        }
    }

    public async Task<IReadOnlyList<WorkspaceChange>> DiffAsync(string workDir, string fromSnapshot, string toSnapshot, IReadOnlyList<string> include, IReadOnlyList<string> exclude, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(include);
        ArgumentNullException.ThrowIfNull(exclude);
        if (string.IsNullOrWhiteSpace(workDir) || string.IsNullOrWhiteSpace(fromSnapshot) || string.IsNullOrWhiteSpace(toSnapshot) || fromSnapshot == toSnapshot || !Directory.Exists(workDir))
        {
            return [];
        }

        try
        {
            var gitDir = GitDirFor(workDir);
            if (!Directory.Exists(Path.Combine(gitDir, "objects")))
            {
                return [];
            }

            // --raw durumu (A/M/D), --numstat satir sayilarini verir; ikisi ayni yol sirasiyla gelir. Desenler pathspec glob'u:
            // disarida birakilanlar (node_modules, bin, obj) git'e hic yuklenmez -- golge depo onlari da izliyor (--force).
            var args = new List<string> { "diff", "--no-renames", "--raw", "--numstat", fromSnapshot, toSnapshot, "--" };
            args.AddRange(include.Count == 0 ? [":(glob)**"] : include.Select(p => ":(glob)" + p));
            args.AddRange(exclude.Select(p => ":(exclude,glob)" + p));
            var output = await GitOutputAsync(gitDir, workDir, ct, [.. args]).ConfigureAwait(false);
            return output is null ? [] : ParseDiff(output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            SnapshotLog.DiffFailed(_log, ex, workDir);
            return [];
        }
    }

    public async Task<string?> ReadAsync(string workDir, string snapshot, string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workDir) || string.IsNullOrWhiteSpace(snapshot) || string.IsNullOrWhiteSpace(path) || !Directory.Exists(workDir))
        {
            return null;
        }

        try
        {
            var gitDir = GitDirFor(workDir);
            return Directory.Exists(Path.Combine(gitDir, "objects"))
                ? await GitOutputAsync(gitDir, workDir, ct, "show", $"{snapshot}:{path.Replace('\\', '/')}").ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            SnapshotLog.DiffFailed(_log, ex, workDir);
            return null;
        }
    }

    /// <summary><c>--raw</c> satirlari (<c>:100644 100644 abc def M\tyol</c>) durumu, <c>--numstat</c> satirlari (<c>12\t3\tyol</c>, ikilide <c>-</c>) sayilari verir.</summary>
    private static List<WorkspaceChange> ParseDiff(string output)
    {
        var status = new Dictionary<string, char>(StringComparer.Ordinal);
        var counts = new Dictionary<string, (int Added, int Deleted)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0)
            {
                continue;
            }

            if (line[0] == ':')
            {
                var path = Unquote(line[(tab + 1)..]);
                var head = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (head.Length >= 5 && head[4].Length > 0)
                {
                    status[path] = head[4][0];
                    order.Add(path);
                }
            }
            else
            {
                var parts = line.Split('\t', 3);
                if (parts.Length == 3)
                {
                    counts[Unquote(parts[2])] = (int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var a) ? a : 0,
                                                 int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : 0);
                }
            }
        }

        return [.. order.Select(p => new WorkspaceChange(p, status[p], counts.GetValueOrDefault(p).Added, counts.GetValueOrDefault(p).Deleted))];

        // core.quotepath=false ile Turkce harf tirnaklanmaz; tirnak yalniz ozel karakterde kalir, kacislari cozmeye degmez.
        static string Unquote(string p) => p.Length >= 2 && p[0] == '"' && p[^1] == '"' ? p[1..^1] : p;
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

    private Task<string?> GitOutputAsync(string gitDir, string workTree, CancellationToken ct, params string[] args)
        => RunGitAsync(gitDir, workTree, Timeout, ct, args);

    /// <summary>Basarisizsa <c>null</c>, basarililiysa stdout. Kimlik ayarla verilir: makinenin git kimligi gerekmez.</summary>
    private async Task<string?> RunGitAsync(string gitDir, string workTree, TimeSpan limit, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workTree,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, // yol ve dosya metni: konsol kod sayfasiyla okunursa Turkce harf bozulur
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
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=false"); // fark ciktisinda Turkce harfli yol tirnaklanip kacislanmasin
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
        timeout.CancelAfter(limit);
        var stdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(proc);
            SnapshotLog.GitTimedOut(_log, string.Join(' ', args), limit.TotalSeconds);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "anlik goruntu farki okunamadi: {WorkDir}")]
    public static partial void DiffFailed(ILogger logger, Exception ex, string workDir);

    [LoggerMessage(Level = LogLevel.Warning, Message = "git {Args} {Seconds} sn icinde bitmedi")]
    public static partial void GitTimedOut(ILogger logger, string args, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "git {Args} basarisiz ({Code}): {Error}")]
    public static partial void GitFailed(ILogger logger, string args, int code, string error);
}
