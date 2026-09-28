using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>
/// Kesif alt ajani (docs/DOMAIN.md → Kesif alt ajani, 2026-09-26): genis kod aramasini ucuz bir model yapar, ana model yalniz
/// sonucu okur. Olcum: baglam CLI'nin ic tur dongusunde buyuyor, girdinin %95'i onbellek okumasi ve arac sonuclarinin %47'si
/// Bash dokumu (PHASES → Token yonetimi). Arama sonuclari alt ajanin baglaminda kalir, ana baglama yalniz ozet girer.
/// KARAR burada: yalniz aracli turda, yalniz anthropic ajaninda (SDK <c>agents</c>), md'de <c>explore_model</c> doluysa.
/// Runtime yalniz esler ve ana ajanin <c>Agent</c> aracini bu listedeki alt ajanlarla sinirlar.
/// </summary>
public static class Explorer
{
    public const string Name = "kesif";

    /// <summary>Claude Code'un alt ajan araci; ana ajanin arac listesine yalniz alt ajan verildiginde eklenir.</summary>
    public const string AgentTool = "Agent";

    public const int MaxTurns = 25;

    /// <summary>Ana ajanin "ne zaman cagiririm" karari bu metinden: tek bilinen dosya icin cagrilmasin, pahali olan tekrar okumadir.</summary>
    public const string Description =
        "Salt okunur, hızlı ve ucuz keşif ajanı. Birden çok dosyayı taramak gereken aramalarda kullan: bir sembolün ya da desenin " +
        "nerede olduğunu bulmak, bir modülün nasıl kurulduğunu özetlemek, \"X bu projede nasıl yapılıyor\" sorusu. Sonucu dosya yolu, " +
        "satır ve tek satır özle döner. Yolunu bildiğin tek bir dosyayı okumak için KULLANMA; onu kendin oku.";

    public const string Prompt =
        """
        Sen salt okunur bir keşif ajanısın. Sana verilen soruyu çalışma dizinindeki kodda araştır ve yalnız SONUCU dön.

        - Önce ara (Glob, Grep), sonra yalnız ilgili aralığı oku (Read offset/limit). Dosyanın tamamını dökme.
        - Cevap biçimi: bulduğun her şey için `yol:satır — tek satır öz`; gerekiyorsa en fazla 15 satırlık alıntı. Toplam ~400 kelimeyi aşma.
        - Emin olmadığın şeyi "doğrulanmadı" diye işaretle; tahmin etme.
        - Dosya yazamaz, komut koşamazsın. Öneri, plan ya da kod yazma: yalnız bulguları raporla.
        """;

    /// <summary>Bu turda acilacak alt ajanlar; kosul tutmuyorsa null (istek eskisi gibi gider).</summary>
    public static IReadOnlyDictionary<string, RuntimeSubagent>? For(Agent agent, AgentTarget target, ToolAccess? tools)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(target);
        if (tools is null || target.Provider != Provider.Anthropic || string.IsNullOrWhiteSpace(agent.ExploreModel))
        {
            return null;
        }

        return new Dictionary<string, RuntimeSubagent>(StringComparer.Ordinal)
        {
            [Name] = new RuntimeSubagent(Description, Prompt, ToolAccess.ReadOnly, agent.ExploreModel.Trim(), MaxTurns),
        };
    }

    /// <summary>Alt ajan varsa ana ajanin araclarina <see cref="AgentTool"/> eklenir; yoksa liste aynen doner.</summary>
    public static IReadOnlyList<string>? WithAgentTool(IReadOnlyList<string>? tools, IReadOnlyDictionary<string, RuntimeSubagent>? subagents)
        => tools is null || subagents is not { Count: > 0 } || tools.Contains(AgentTool, StringComparer.Ordinal) ? tools : [.. tools, AgentTool];
}
