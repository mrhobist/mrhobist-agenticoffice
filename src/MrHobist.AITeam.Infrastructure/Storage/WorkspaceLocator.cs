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
        // Bariyer yalniz depo ICINDE gerekir (bu deponun props'lari miras kalmasin); depo disi projeye dosya eklemez.
        if (!Domain.Projects.Project.IsExternalDir(project.TargetDir))
        {
            EnsureMsBuildBarrier(full);
        }

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
        var rel = Domain.Projects.Project.NormalizeDir(relativePath);
        if (Domain.Projects.Project.IsDriveRoot(rel) || Domain.Projects.Project.IsExternalDir(rel))
        {
            return ListExternal(rel);
        }

        var repo = Repo();
        rel = rel.TrimStart('/');
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

    /// <summary>
    /// Depo disi gezinme (2026-09-26, iceri alma): suruculu tam yol, surucu koku dahil. Erisilemeyen klasor sessizce atlanir
    /// (<c>C:/Users/baskasi</c>), sistem/gizli klasorler (<c>$Recycle.Bin</c>, <c>System Volume Information</c>) listelenmez.
    /// Yalniz ADLAR doner, dosya okunmaz; tek kullanicili yerel aracta (loopback + giris) bu kabul edildi.
    /// </summary>
    private static List<WorkspaceDirectory> ListExternal(string dir)
    {
        var full = Domain.Projects.Project.IsDriveRoot(dir) ? dir[..2] + Path.DirectorySeparatorChar : Path.GetFullPath(dir.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(full))
        {
            return [];
        }

        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, RecurseSubdirectories = false };
        var prefix = full.Replace('\\', '/').TrimEnd('/');
        return Directory.EnumerateDirectories(full, "*", options)
            .Select(d => Path.GetFileName(d)!)
            .Where(n => !n.StartsWith('.') && !n.StartsWith('$') && !Hidden.Contains(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new WorkspaceDirectory(n, prefix + "/" + n))
            .ToList();
    }

    public string PathOf(string targetDir) => Resolve(Domain.Projects.Project.NormalizeDir(targetDir), "hedef dizin");

    /// <summary>
    /// Ag surucusunde <see cref="DriveInfo.IsReady"/> sorulmaz: kopuk eslenmis surucude saniyelerce bekler ve bu liste klasor
    /// secicinin HER tiklamasinda doner. Hazir olmayan ag surucusu listede kalir, gezilince bos gelir.
    /// </summary>
    public IReadOnlyList<string> Drives()
        => DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Network || (d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady))
            .Select(d => d.Name.Replace('\\', '/'))
            .ToList();

    public bool DeleteRoot(Domain.Projects.Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        // Depo disi dizin uygulamadan ASLA silinmez: kullanicinin kendi klasoru, "dosyalar da silinsin" isareti
        // verilse bile bir tikla gitmesin. Proje kaydi silinir, dosyalar yerinde kalir.
        if (Domain.Projects.Project.IsExternalDir(project.TargetDir))
        {
            return false;
        }

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
        // Suruculu tam yol: depo disi hedef (Project.IsExternalDir); klasor secici bunu gezmez, yalniz proje kokunde kullanilir.
        if (Domain.Projects.Project.IsExternalDir(relative))
        {
            var external = Path.TrimEndingDirectorySeparator(Path.GetFullPath(relative.Replace('/', Path.DirectorySeparatorChar)));
            EnsureNotOfficeRepo(external, subject);
            return external;
        }

        // "C:" ya da "C:klasor" (surucuye goreli): Path.Combine bunu kok sayar ve CALISMA DIZININE cozer; Api depo icinde
        // kosuyorsa "depo icinde" gorunup kabul edilirdi. Goreli yol iki nokta ust uste tasiyamaz (Project.Validate ile ayni kural).
        if (relative.Contains(':', StringComparison.Ordinal))
        {
            throw new DomainException(ErrorCodes.ProjectTargetDirInvalid, $"{subject}: '{relative}' ne depo icinde goreli ne suruculu tam yol.");
        }

        var repo = Repo();
        var full = Path.GetFullPath(Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException(ErrorCodes.ProjectTargetDirInvalid, $"{subject}: hedef dizin depo disina cikiyor.");
        }

        return full;
    }

    /// <summary>
    /// Suruculu tam yol ofisin KENDI deposu olamaz (2026-09-27, inceleme bulgusu): depo kokunun kendisi, bir ic klasoru ya da
    /// depoyu iceren ust klasor. Aksi halde ajanin <c>cwd</c>'si ofis olur ve <c>config/</c> (canli yeniden yuklenir), <c>src/</c>,
    /// <c>data/aiteam.db</c> yazilabilir. Depo ici hedef zaten goreli yazilir (<c>projects/{key}</c>).
    /// </summary>
    private void EnsureNotOfficeRepo(string external, string subject)
    {
        var repo = Repo();
        if (string.Equals(external, repo, StringComparison.OrdinalIgnoreCase)
            || external.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || repo.StartsWith(external + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException(ErrorCodes.ProjectDirReserved, $"{subject}: '{external}' ofisin kendi deposu ya da onun ic/dis klasoru.");
        }
    }
}
