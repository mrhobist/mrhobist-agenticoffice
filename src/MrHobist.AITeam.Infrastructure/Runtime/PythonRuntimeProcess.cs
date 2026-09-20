using System.Diagnostics;

namespace MrHobist.AITeam.Infrastructure.Runtime;

/// <summary>
/// <c>runtime/</c> (FastAPI) surecini bu makinede baslatir ve durdurur. <b>Is kurali tasimaz</b>: yalniz
/// yorumlayici bulma ve surec yonetimi (CLAUDE.md §1 — Python'a mantik sizmaz, burada da Python'un ne
/// yaptigina dair bilgi yoktur).
/// <para>
/// Yorumlayici <c>runtime/.venv</c> icindedir ve <b>cihaza baglidir</b>: shim, onu kuran makinenin (ve
/// Windows kullanicisinin) <c>python.exe</c>'sini mutlak yolla cagirir. Ayni calisma dizinini baska bir
/// kullanici actiginda dosya <b>durur ama calismaz</b> (<c>No Python at '...'</c>, cikis kodu 103).
/// Bu yuzden "dosya var mi" yetmez; yorumlayici kosturularak yoklanir (<c>scripts/verify.ps1</c> ile ayni kural).
/// </para>
/// </summary>
public static class PythonRuntimeProcess
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <c>runtime/.venv</c> icindeki yorumlayici; yoksa ya da bu cihazda calismiyorsa <c>null</c>.
    /// Sistem Python'ina dusulmez: runtime'in bagimliliklari (fastapi, uvicorn, claude-agent-sdk)
    /// sanal ortamdadir, sistem Python'inda yoktur.
    /// </summary>
    public static string? FindInterpreter(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        string[] candidates =
        [
            Path.Combine(repoRoot, "runtime", ".venv", "Scripts", "python.exe"),
            Path.Combine(repoRoot, "runtime", ".venv", "bin", "python"),
        ];

        return candidates.FirstOrDefault(c => File.Exists(c) && Runs(c));
    }

    /// <summary>Yorumlayici fiilen kosuyor mu. Bozuk shim sifirdan farkli doner (103).</summary>
    private static bool Runs(string interpreter)
    {
        try
        {
            var psi = new ProcessStartInfo(interpreter)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("pass");

            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            if (!p.WaitForExit(ProbeTimeout))
            {
                p.Kill(entireProcessTree: true);
                return false;
            }

            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// uvicorn'u <paramref name="url"/> uzerinde baslatir. Cikti akislari cagirana verilir (bos birakilirsa
    /// boru dolar ve cocuk surec kilitlenir — cagiran <see cref="Process.BeginOutputReadLine"/> cagirmalidir).
    /// </summary>
    public static Process Start(string repoRoot, string interpreter, Uri url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(interpreter);
        ArgumentNullException.ThrowIfNull(url);

        var psi = new ProcessStartInfo(interpreter)
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[]
                 {
                     "-m", "uvicorn", "app.main:app",
                     "--host", url.Host,
                     "--port", url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--app-dir", "runtime",
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        return Process.Start(psi) ?? throw new InvalidOperationException("uvicorn sureci baslamadi.");
    }
}
