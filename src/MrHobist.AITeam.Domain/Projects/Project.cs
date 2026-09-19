namespace MrHobist.AITeam.Domain.Projects;

/// <summary>
/// Proje: islerin yasadigi kap (<c>config/projects/{Key}.json</c>). Bir is yalniz bir projenin icinde baslar
/// (docs/DOMAIN.md → Projeler). <see cref="Workflow"/> projenin varsayilan akisi (is formunda degistirilebilir);
/// <see cref="TargetDir"/> developer'in dosya yazacagi dizin (depo kokune gore). <see cref="OwnerId"/> giris
/// hazirligidir: bugun sabit <c>local</c>, JWT gelince claim'den dolar. Butce is basinadir, projede yoktur (kullanici karari).
/// </summary>
public sealed record Project(
    string Key,
    string Title,
    string Description,
    string Workflow,
    string TargetDir,
    string OwnerId,
    DateTimeOffset CreatedAt)
{
    public const string LocalOwner = "local";

    /// <summary>Kendi basina tutarli mi: anahtar, baslik, akis anahtari, hedef dizin (depo icinde, ust dizine cikmaz).</summary>
    public void Validate()
    {
        Identifiers.Require(Key, ErrorCodes.ProjectInvalidKey, "proje");
        if (string.IsNullOrWhiteSpace(Title))
        {
            throw new DomainException(ErrorCodes.ProjectTitleEmpty, $"{Key}: 'title' bos.");
        }

        Identifiers.Require(Workflow, ErrorCodes.WorkflowInvalidStage, "akis");
        var dir = (TargetDir ?? "").Replace('\\', '/').Trim();
        if (dir.Length == 0 || dir.StartsWith('/') || dir.Contains("..", StringComparison.Ordinal) || dir.Contains(':', StringComparison.Ordinal))
        {
            throw new DomainException(ErrorCodes.ProjectTargetDirInvalid, $"{Key}: 'targetDir' depo icinde goreli bir yol olmali ('{TargetDir}').");
        }
    }

    /// <summary>Varsayilan hedef dizin: <c>projects/{key}</c>.</summary>
    public static string DefaultTargetDir(string key) => $"projects/{key}";
}
