using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary>Depo koku = <c>config/</c>'in ustu; proje hedef dizini ona gore cozulur. Yol depo disina cikamaz (Project.Validate).</summary>
public sealed class WorkspaceLocator(StoragePaths paths) : IWorkspaceLocator
{
    /// <summary>Klasor secicide gosterilmeyenler: bagimlilik ve derleme ciktilari, calisma gecmisi (noktayla baslayanlar da gizli).</summary>
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "__pycache__", "dist", "TestResults", "data",
    };

    public string RootOf(Domain.Projects.Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var full = Resolve(project.TargetDir, project.Key);
        Directory.CreateDirectory(full);
        EnsureMsBuildBarrier(full);
        return full;
    }

    /// <summary>
    /// MSBuild yalitimi. Hedef dizin depo ICINDE oldugu icin (<see cref="Domain.Projects.Project.Validate"/>),
    /// ajanin urettigi her .NET projesi bu deponun <c>Directory.Build.props</c> ve <c>Directory.Packages.props</c>
    /// dosyalarini miras alir: <c>TreatWarningsAsErrors</c>, merkezi paket surumleri, hedef framework...
    /// Bunlar uretilen isin kararlari DEGILDIR ve tasinabilirligini bozar.
    ///
    /// MSBuild yukari dogru ararken BULDUGU ILK dosyada durur; bu yuzden proje kokune bos birer dosya koymak
    /// zinciri temiz keser. 2026-09-21 olcumu: bu olmadan ajan mirasi deneyerek kesfetti ve 6 LLM turu harcadi
    /// (gecici bir iskele proje kurup <c>dotnet msbuild -getProperty:</c> ile sorguladi).
    ///
    /// Var olan dosyanin ustune YAZILMAZ: kullanici ya da ajan bilerek koyduysa onunki gecerlidir.
    /// </summary>
    private static void EnsureMsBuildBarrier(string projectRoot)
    {
        Write("Directory.Build.props", """
            <Project>
              <!-- Bu proje bagimsizdir: ust klasorlerdeki derleme ayarlari miras ALINMAZ.
                   MSBuild yukari dogru ararken bu dosyada durur. Kendi ayarlarini buraya ekleyebilirsin. -->
            </Project>
            """);

        Write("Directory.Packages.props", """
            <Project>
              <!-- Merkezi paket surumu KAPALI: paket surumleri kendi csproj'larinda durur. -->
              <PropertyGroup>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """);

        void Write(string name, string content)
        {
            var path = Path.Combine(projectRoot, name);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, content + Environment.NewLine);
            }
        }
    }

    public IReadOnlyList<WorkspaceDirectory> ListDirectories(string? relativePath)
    {
        var repo = Repo();
        var rel = (relativePath ?? "").Replace('\\', '/').Trim().Trim('/');
        var full = rel.Length == 0 ? repo : Resolve(rel, "klasor");
        if (!Directory.Exists(full))
        {
            return [];
        }

        return Directory.EnumerateDirectories(full)
            .Select(d => Path.GetFileName(d)!)
            .Where(n => !n.StartsWith('.') && !Hidden.Contains(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new WorkspaceDirectory(n, rel.Length == 0 ? n : rel + "/" + n))
            .ToList();
    }

    public bool DeleteRoot(Domain.Projects.Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var full = Resolve(project.TargetDir, project.Key);
        if (!Directory.Exists(full))
        {
            return false;
        }

        Directory.Delete(full, recursive: true);
        return true;
    }

    private string Repo() => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(paths.ConfigRoot))!;

    /// <summary>Depo kokune gore yolu mutlaklar; kokun kendisi ya da disina cikan yol reddedilir.</summary>
    private string Resolve(string relative, string subject)
    {
        var repo = Repo();
        var full = Path.GetFullPath(Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException(ErrorCodes.ProjectTargetDirInvalid, $"{subject}: hedef dizin depo disina cikiyor.");
        }

        return full;
    }
}
