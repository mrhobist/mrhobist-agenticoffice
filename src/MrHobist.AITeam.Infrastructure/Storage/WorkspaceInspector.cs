using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain.Projects;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary>
/// Dizin tarayici (<see cref="IWorkspaceInspector"/>, 2026-09-26). Dosya listesi GitHub'in saydigina yakin olsun diye git
/// deposunda <c>git ls-files --cached --others --exclude-standard</c>'tan gelir (izlenen + izlenmeyen ama yok sayilmayan:
/// devam eden isin yeni dosyalari da gorunsun); git yoksa ya da basarisizsa klasor yurunur ve <see cref="Codebase.SkippedDirs"/>
/// budanir. Dal ve uzak depo git surecine gerek kalmadan <c>.git</c> dosyalarindan okunur.
///
/// Proje dizinine YAZMAZ, git'e yalniz okuma komutu verir. Sonuc kisa sure (<see cref="CacheFor"/>) bellekte tutulur: ray
/// karti ve proje paneli ayni dizini art arda sorar. Onbellek YALNIZ disk taramasini (git sureci + dosya basina stat)
/// tekrarlamamak icindir; token maliyetiyle ilgisi yoktur -- istem metni ayni olcuden uretilir, onbellekten gelse de
/// gelmese de ayni. Tutulan olculmus ozettir (diller, manifestler), ham dosya listesi degil; suresi dolan her yazimda atilir.
/// </summary>
public sealed partial class WorkspaceInspector(ILogger<WorkspaceInspector>? logger = null) : IWorkspaceInspector
{
    /// <summary>Monorepo koruyucusu: bundan fazla dosya sayilmaz, olcu ilk parcaya gore (<see cref="WorkspaceScan.Truncated"/>).</summary>
    public const int MaxFiles = 50_000;

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    /// <summary>README'nin ve package.json'in okunan basi: oneri icin ilk baslik/paragraf ve name/description yeter.</summary>
    private const int ReadmeChars = 8 * 1024;
    private const long MaxPackageJsonBytes = 256 * 1024;

    private readonly ILogger _log = logger ?? NullLogger<WorkspaceInspector>.Instance;
    private readonly ConcurrentDictionary<string, (DateTime At, WorkspaceScan Scan)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<WorkspaceScan> ScanAsync(string root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return WorkspaceScan.Missing;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (_cache.TryGetValue(full, out var hit) && DateTime.UtcNow - hit.At < CacheFor)
        {
            return hit.Scan;
        }

        var gitDir = GitDirOf(full);
        var listed = gitDir is null ? null : await GitFilesAsync(full, ct).ConfigureAwait(false);
        var (files, truncated) = listed ?? Walk(full);
        var (title, description) = ProjectSuggestion.From(Path.GetFileName(full), ReadPackageJson(full), ReadReadme(full));
        var scan = new WorkspaceScan(
            true, Codebase.Measure(files), truncated, gitDir is not null,
            gitDir is null ? null : BranchOf(gitDir),
            gitDir is null ? null : RemoteOf(gitDir),
            title,
            description);
        Remember(full, scan);
        return scan;
    }

    /// <summary>Yazarken suresi dolanlar atilir: gezilen her klasor (iceri alma onizlemesi) kalici kayit birakmaz.</summary>
    private void Remember(string full, WorkspaceScan scan)
    {
        var now = DateTime.UtcNow;
        foreach (var (key, entry) in _cache)
        {
            if (now - entry.At >= CacheFor)
            {
                _cache.TryRemove(key, out _);
            }
        }

        _cache[full] = (now, scan);
    }

    // ------------------------------------------------------------------ dosya listesi

    /// <summary>Git'in listesi; git yoksa, depo "dubious ownership" derse ya da sure asilirsa null (klasor yurunur).</summary>
    private async Task<(List<SourceFile>, bool)?> GitFilesAsync(string root, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-c", "core.quotepath=false", "ls-files", "-z", "--cached", "--others", "--exclude-standard" })
        {
            psi.ArgumentList.Add(arg);
        }

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // git kurulu degil
        }

        if (proc is null)
        {
            return null;
        }

        using (proc)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(GitTimeout);
            var stdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(proc);
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                InspectorLog.GitTimedOut(_log, root, GitTimeout.TotalSeconds);
                return null;
            }

            if (proc.ExitCode != 0)
            {
                var error = await stderr.ConfigureAwait(false);
                InspectorLog.GitFailed(_log, root, proc.ExitCode, error);
                return null;
            }

            var files = new List<SourceFile>();
            var truncated = false;
            foreach (var rel in (await stdout.ConfigureAwait(false)).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (files.Count >= MaxFiles)
                {
                    truncated = true;
                    break;
                }

                // Silinmis ama izlenen dosya ve alt modul (dizin olarak listelenir) sayilmaz.
                var info = new FileInfo(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (info.Exists)
                {
                    files.Add(new SourceFile(rel, info.Length));
                }
            }

            return (files, truncated);
        }
    }

    /// <summary>
    /// Git'siz yurume: budanan klasorlere, gizli/sistem dosyalarina ve baglantilara (junction dongusu) girilmez. Tarama
    /// surerken silinen/tasinan klasor (ajan ya da kullanici derleme ciktisini temizler) o klasoru atlatir, taramayi dusurmez:
    /// <c>IgnoreInaccessible</c> yalniz erisim hatasini yutar, <see cref="DirectoryNotFoundException"/>'i degil.
    /// </summary>
    private static (List<SourceFile>, bool) Walk(string root)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        var files = new List<SourceFile>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                {
                    if (info is DirectoryInfo sub)
                    {
                        if (!sub.Name.StartsWith('.') && !Codebase.SkippedDirs.Contains(sub.Name))
                        {
                            pending.Push(sub.FullName);
                        }

                        continue;
                    }

                    if (files.Count >= MaxFiles)
                    {
                        return (files, true);
                    }

                    files.Add(new SourceFile(Path.GetRelativePath(root, info.FullName).Replace('\\', '/'), ((FileInfo)info).Length));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Klasor tarama sirasinda gitti ya da kilitlendi: sayilabilen kadariyla devam.
            }
        }

        return (files, false);
    }

    // ------------------------------------------------------------------ git meta (surecsiz)

    /// <summary><c>.git</c> dizini; worktree/alt modulde <c>.git</c> bir dosyadir (<c>gitdir: …</c>). Yoksa null.</summary>
    private static string? GitDirOf(string root)
    {
        var dotGit = Path.Combine(root, ".git");
        if (Directory.Exists(dotGit))
        {
            return dotGit;
        }

        if (!File.Exists(dotGit))
        {
            return null;
        }

        var line = SafeRead(dotGit, 1024)?.Trim();
        if (line is null || !line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var target = Path.GetFullPath(Path.Combine(root, line["gitdir:".Length..].Trim()));
        return Directory.Exists(target) ? target : null;
    }

    /// <summary><c>ref: refs/heads/main</c> → <c>main</c>; kopuk HEAD → kisa hash.</summary>
    private static string? BranchOf(string gitDir)
    {
        var head = SafeRead(Path.Combine(gitDir, "HEAD"), 1024)?.Trim();
        if (string.IsNullOrEmpty(head))
        {
            return null;
        }

        const string prefix = "ref: refs/heads/";
        return head.StartsWith(prefix, StringComparison.Ordinal) ? head[prefix.Length..] : head[..Math.Min(7, head.Length)];
    }

    /// <summary>
    /// <c>origin</c> (yoksa ilk uzak) adresi. Worktree'de ayar ortak dizindedir (<c>commondir</c>). Adresteki kullanici
    /// bilgisi (<c>https://ad:belirtec@host</c>) ATILIR: bu deger ekrana ve ajanin istemine gider.
    /// </summary>
    private static string? RemoteOf(string gitDir)
    {
        var common = SafeRead(Path.Combine(gitDir, "commondir"), 1024)?.Trim();
        var configDir = string.IsNullOrEmpty(common) ? gitDir : Path.GetFullPath(Path.Combine(gitDir, common));
        var config = SafeRead(Path.Combine(configDir, "config"), 64 * 1024);
        if (config is null)
        {
            return null;
        }

        string? section = null, first = null;
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                section = line;
                continue;
            }

            if (section is null || !section.StartsWith("[remote ", StringComparison.Ordinal) || !line.StartsWith("url", StringComparison.Ordinal))
            {
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                continue;
            }

            var url = StripCredentials(line[(eq + 1)..].Trim());
            if (section == "[remote \"origin\"]")
            {
                return url;
            }

            first ??= url;
        }

        return first;
    }

    public static string StripCredentials(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0 && uri.Scheme is "http" or "https" or "ssh")
        {
            return new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString().TrimEnd('/');
        }

        return url; // git@github.com:ad/depo.git bicimi: kullanici adi "git"tir, sir yok
    }

    // ------------------------------------------------------------------ oneri dosyalari

    private static string? ReadPackageJson(string root)
    {
        var path = Path.Combine(root, "package.json");
        var info = new FileInfo(path);
        return info.Exists && info.Length <= MaxPackageJsonBytes ? SafeRead(path, (int)info.Length) : null;
    }

    private static string? ReadReadme(string root)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = false };
        try
        {
            var readme = Directory.EnumerateFiles(root, "readme*", options)
                .OrderBy(f => Path.GetExtension(f).Equals(".md", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault(f => Path.GetExtension(f).ToLowerInvariant() is ".md" or ".markdown" or ".txt" or "");
            return readme is null ? null : SafeRead(readme, ReadmeChars);
        }
        catch (IOException)
        {
            return null; // kok tarama sirasinda gitti
        }
    }

    /// <summary>En cok <paramref name="maxChars"/> karakter; okunamazsa (kilitli, izin yok) null.</summary>
    private static string? SafeRead(string path, int maxChars)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[Math.Max(1, maxChars)];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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

    private static partial class InspectorLog
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "git ls-files {Seconds} sn icinde bitmedi, klasor yurunuyor: {Root}")]
        public static partial void GitTimedOut(ILogger logger, string root, double seconds);

        [LoggerMessage(Level = LogLevel.Information, Message = "git ls-files basarisiz ({Code}), klasor yurunuyor: {Root}: {Error}")]
        public static partial void GitFailed(ILogger logger, string root, int code, string error);
    }
}
