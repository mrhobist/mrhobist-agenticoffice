namespace MrHobist.AITeam.Application.Abstractions;

/// <summary>
/// Tasinan gecmisin butcesi. Gecmis red turlariyla buyur (implement → review(red) → implement …); her tur bir
/// onceki ciktiyi da tasir. <see cref="MaxMessages"/> asilinca en eski turlar dusurulur (kayan pencere),
/// <see cref="MaxTokens"/> asilinca uzun mesajlar kirpilir. Ikisi de politika: Application belirler.
/// </summary>
public sealed record CompactionBudget(int MaxMessages, int MaxTokens)
{
    /// <summary>Gorev gecmisi icin varsayilan: son 2 tur (4 mesaj) tam, ustu duser; ~12k token tavan.</summary>
    public static readonly CompactionBudget TaskHistory = new(MaxMessages: 4, MaxTokens: 12_000);
}

/// <summary>
/// Gecmisi butceye sigdirir. Uygulama Infrastructure'da (MAF Compaction); Application yalniz "ne zaman, ne kadar" der.
/// Yeni istem (son mesaj) HIC dokunulmaz: sikistirma yalniz taşinan onceki turlara uygulanir.
/// </summary>
public interface IHistoryCompactor
{
    Task<IReadOnlyList<RuntimeMessage>> CompactAsync(IReadOnlyList<RuntimeMessage> history, CompactionBudget budget, CancellationToken ct);
}

/// <summary>Sikistirma yok: gecmis oldugu gibi gider. Testlerde ve sikistirmanin kapali oldugu kurulumda.</summary>
public sealed class NoCompaction : IHistoryCompactor
{
    public Task<IReadOnlyList<RuntimeMessage>> CompactAsync(IReadOnlyList<RuntimeMessage> history, CompactionBudget budget, CancellationToken ct)
        => Task.FromResult(history);
}
