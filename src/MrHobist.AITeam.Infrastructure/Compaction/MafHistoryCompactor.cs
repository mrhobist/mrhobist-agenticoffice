using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MrHobist.AITeam.Application.Abstractions;

namespace MrHobist.AITeam.Infrastructure.Compaction;

/// <summary>
/// Microsoft Agent Framework'un Compaction kutuphanesiyle gecmis kirpma. MAF burada bir KUTUPHANEDIR, cerceve degil:
/// orkestrasyon <c>RunService</c>'te kalir, buradan yalniz <see cref="CompactionProvider.CompactAsync"/> statik
/// metodu cagrilir (2026-09-21 arastirma karari; LLM cagrisi ve orkestrasyon icin MAF'e hayir).
///
/// Modelsiz stratejiler kullanilir: <see cref="SlidingWindowCompactionStrategy"/> (mesaj sayisi) ve
/// <see cref="TruncationCompactionStrategy"/> (token). Ozetleme (<see cref="SummarizationCompactionStrategy"/>)
/// bir model turu ister; CLAUDE.md §4 gereği her tur kayda ve butceye girmeli -- o yuzden burada YOK, ayri karar.
///
/// Token olcusu MAF'in degil bizimdir: MAF tokenizer'siz UTF-8 bayt / 4 sayar, Turkce harf 2 bayttir ve erken cikis
/// karakterle sayiyordu -- iki esik birbirini tutmuyordu. Tetik kalibre orandan (<see cref="CompactionBudget.CharsPerToken"/>)
/// karakterle hesaplanir, ayni olcu hem erken cikista hem kirpmada kullanilir.
/// </summary>
public sealed class MafHistoryCompactor(ILoggerFactory? loggerFactory = null) : IHistoryCompactor
{
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<MafHistoryCompactor>();

    public async Task<IReadOnlyList<RuntimeMessage>> CompactAsync(IReadOnlyList<RuntimeMessage> history, CompactionBudget budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(budget);
        if (history.Count <= budget.MaxMessages && budget.EstimateTokens(history.Sum(m => (long)m.Content.Length)) <= budget.MaxTokens)
        {
            return history;
        }

        // Once token tavanina kadar eski gruplari dusur, sonra mesaj penceresini uygula: siralama onemli.
        CompactionTrigger overTokens = index => budget.EstimateTokens(IncludedChars(index)) > budget.MaxTokens;
        var strategy = new PipelineCompactionStrategy(
        [
            new TruncationCompactionStrategy(overTokens, minimumPreservedGroups: 2),
            new SlidingWindowCompactionStrategy(CompactionTriggers.MessagesExceed(budget.MaxMessages), minimumPreservedTurns: 1),
        ]);

        var messages = history.Select(m => new ChatMessage(m.Role == "assistant" ? ChatRole.Assistant : ChatRole.User, m.Content)).ToList();
        var compacted = await CompactionProvider.CompactAsync(strategy, messages, _logger, ct).ConfigureAwait(false);
        return [.. compacted.Select(m => new RuntimeMessage(m.Role == ChatRole.Assistant ? "assistant" : "user", m.Text ?? ""))];
    }

    private static long IncludedChars(CompactionMessageIndex index)
        => index.Groups.Where(g => !g.IsExcluded).Sum(g => g.Messages.Sum(m => (long)(m.Text?.Length ?? 0)));
}
