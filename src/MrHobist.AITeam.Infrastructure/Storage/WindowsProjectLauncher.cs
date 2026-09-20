using System.Diagnostics;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary>
/// Proje kokundeki <c>run.cmd</c>'yi yeni bir konsol penceresinde acar (<c>cmd /c start</c>). Surece baglanmaz: ciktiyi
/// kullanici pencerede gorur, kapatmak onun elinde. Yalniz bu makine, yalniz proje koku (CLAUDE.md §3 loopback ile ayni sinir).
/// </summary>
public sealed class WindowsProjectLauncher : IProjectLauncher
{
    private const string Launcher = "run.cmd";

    public bool CanLaunch(string projectRoot) => File.Exists(Path.Combine(projectRoot, Launcher));

    public int Launch(string projectRoot)
    {
        var file = Path.Combine(projectRoot, Launcher);
        if (!File.Exists(file))
        {
            throw new DomainException(ErrorCodes.ProjectLaunchMissing, $"{Launcher} yok: {projectRoot}");
        }

        try
        {
            // start "baslik" /D "kok" cmd /k run.cmd → yeni pencere, kok dizinde, bitince acik kalir (kullanici ciktiyi okur).
            var psi = new ProcessStartInfo("cmd.exe")
            {
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add($"start \"{Path.GetFileName(projectRoot)}\" /D \"{projectRoot}\" cmd /k \"{file}\"");
            using var p = Process.Start(psi) ?? throw new DomainException(ErrorCodes.ProjectLaunchFailed, "surec baslamadi");
            return p.Id;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new DomainException(ErrorCodes.ProjectLaunchFailed, $"{Launcher} kosamadi: {ex.Message}");
        }
    }
}
