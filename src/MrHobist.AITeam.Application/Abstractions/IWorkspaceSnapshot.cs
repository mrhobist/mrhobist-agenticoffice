namespace MrHobist.AITeam.Application.Abstractions;

/// <summary>
/// Calisma alaninin bir andaki hali. Ajanlar dosyalari kendileri yazar (CLAUDE.md §1); red turunda yarim kalan
/// dosyalar bir sonraki tura KALIR ve geri alma yolu yoktu. Ornek: opencode'un <c>snapshot</c> modulu -- proje
/// dizininin disinda duran bir GOLGE git deposu (<c>--git-dir</c> ayri, <c>--work-tree</c> proje koku), boylece
/// kullanicinin kendi git gecmisine, dallarina ve staging alanina hic dokunulmaz.
///
/// Geri alma ASLA kendiliginden olmaz: yarim is cogu zaman dogruya yakindir ve red geri bildirimi "sunu duzelt"
/// der, "bastan yap" demez. Yalnizca kullanici acikca sectiginde geri donulur (docs/DOMAIN.md → Geri donus kurali).
/// </summary>
public interface IWorkspaceSnapshot
{
    /// <summary>
    /// Dizinin o anki halini kaydeder ve tanitici dondurur. Kayit tutulamazsa (git yok, dizin yok, depo bozuk)
    /// <c>null</c> doner: anlik goruntu bir KOLAYLIKTIR, calismayi durdurmaz.
    /// </summary>
    Task<string?> TrackAsync(string workDir, CancellationToken ct);

    /// <summary>
    /// Dizini verilen tanitica geri dondurur. YIKICI: o andan sonra yazilan dosyalar kaybolur, bu yuzden yalniz
    /// kullanicinin acik secimiyle cagrilir. Basarisizsa <c>false</c> doner; cagiran kullaniciya soyler.
    /// </summary>
    Task<bool> RestoreAsync(string workDir, string snapshot, CancellationToken ct);
}

/// <summary>Anlik goruntu yok: her cagri bos doner. Testlerde ve ozelligin kapali oldugu kurulumda.</summary>
public sealed class NoWorkspaceSnapshot : IWorkspaceSnapshot
{
    public Task<string?> TrackAsync(string workDir, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<bool> RestoreAsync(string workDir, string snapshot, CancellationToken ct) => Task.FromResult(false);
}
