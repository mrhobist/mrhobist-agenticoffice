using MrHobist.AITeam.Domain.Agents;

namespace MrHobist.AITeam.Domain.Workflows;

/// <summary>Adim turu. JSON'da adiyla tasinir; yeni uye sona eklenir.</summary>
public enum StageKind
{
    /// <summary>Bir kez calisir, brief'ten gorev grafigini uretir. Tam olarak 1 tane, ilk sirada.</summary>
    Analyze,
    /// <summary>Kod yazmaz; sonraki adimlara rehberlik metni uretir.</summary>
    Design,
    /// <summary>Gorevi kodlar, dosyalari yazar.</summary>
    Implement,
    /// <summary>Denetler; reddederse is kendinden ONCEKI implement adimina doner.</summary>
    Review,
    /// <summary>Devir notu uretir, bloklamaz.</summary>
    Handoff,
}

/// <summary>Is akisinin bir adimi. <see cref="Role"/> bir ajan anahtaridir, <see cref="OfficeRole"/> sahnedeki karakter tipi.</summary>
public sealed record Stage(string Id, string Title, StageKind Kind, string Role, string OfficeRole, string Description);

/// <summary>
/// <c>config/workflows/{Key}.json</c>: ekipten kurulan bir akis. <c>default</c> her zaman vardir ve silinemez;
/// calisma baslatilirken akis secilir. Adim eklemek/silmek kod degisikligi gerektirmez; bozuk bir akis
/// calisma ortasinda degil, kaydedilirken/yuklenirken patlar. <see cref="HandoffRole"/> verilmisse o ajan
/// her adim gecisinde devir notu uretir (organizator).
/// </summary>
public sealed record Workflow(string Key, string Title, int MaxReviewRounds, string? HandoffRole, IReadOnlyList<Stage> Stages)
{
    public const string DefaultKey = "default";

    public static readonly IReadOnlySet<string> ValidOfficeRoles =
        new HashSet<string>(StringComparer.Ordinal) { "pm", "arch", "dev", "qa", "ops", "res", "gate", "designer" };

    public bool IsDefault => Key == DefaultKey;

    /// <summary>Akista gecen ajanlar: adim rolleri + devir rolu, sirali ve tekil.</summary>
    public IReadOnlyList<string> Roles
        => Stages.Select(s => s.Role).Concat(HandoffRole is null ? [] : [HandoffRole]).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Her gorev icin sirayla calisan adimlar (analiz haric).</summary>
    public IReadOnlyList<Stage> TaskStages => Stages.Where(s => s.Kind != StageKind.Analyze).ToList();

    /// <summary>Kendi basina tutarli mi: anahtar, baslik, adim degismezleri. Ekibe bakmaz (<see cref="ValidateAgainst"/>).</summary>
    public void Validate()
    {
        if (!Identifiers.IsValidKey(Key))
        {
            throw new DomainException(ErrorCodes.WorkflowInvalidStage, $"Gecersiz akis anahtari: '{Key}'.");
        }

        if (string.IsNullOrWhiteSpace(Title))
        {
            throw new DomainException(ErrorCodes.WorkflowInvalidStage, $"{Key}: 'title' bos.");
        }

        if (HandoffRole is not null && !Identifiers.IsValidKey(HandoffRole))
        {
            throw new DomainException(ErrorCodes.WorkflowInvalidStage, $"{Key}: gecersiz handoffRole '{HandoffRole}'.");
        }

        if (Stages.Count == 0)
        {
            throw new DomainException(ErrorCodes.WorkflowInvalidStage, "'stages' bos olamaz.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in Stages)
        {
            if (!Identifiers.IsValidKey(s.Id))
            {
                throw new DomainException(ErrorCodes.WorkflowInvalidStage, $"Gecersiz adim kimligi: '{s.Id}'.");
            }

            if (!seen.Add(s.Id))
            {
                throw new DomainException(ErrorCodes.WorkflowDuplicateStage, $"Yinelenen adim kimligi: '{s.Id}'.");
            }

            if (!Enum.IsDefined(s.Kind))
            {
                throw new DomainException(ErrorCodes.WorkflowInvalidStage, $"{s.Id}: gecersiz kind.");
            }

            if (!ValidOfficeRoles.Contains(s.OfficeRole))
            {
                throw new DomainException(ErrorCodes.WorkflowInvalidStage, $"{s.Id}: gecersiz officeRole '{s.OfficeRole}'.");
            }

            if (!Identifiers.IsValidKey(s.Role))
            {
                throw new DomainException(ErrorCodes.WorkflowInvalidStage, $"{s.Id}: 'role' bos ya da gecersiz.");
            }
        }

        var analyzeCount = Stages.Count(s => s.Kind == StageKind.Analyze);
        if (analyzeCount != 1)
        {
            throw new DomainException(ErrorCodes.WorkflowAnalyzeCount, $"Tam olarak 1 'analyze' adimi olmali, {analyzeCount} var.");
        }

        if (Stages[0].Kind != StageKind.Analyze)
        {
            throw new DomainException(ErrorCodes.WorkflowAnalyzeFirst, "'analyze' adimi ilk sirada olmali.");
        }

        if (Stages.All(s => s.Kind != StageKind.Implement))
        {
            throw new DomainException(ErrorCodes.WorkflowNoImplement, "En az bir 'implement' adimi olmali.");
        }

        // Bir review adiminin geri gonderecegi bir implement adimi bulunmali.
        var seenImplement = false;
        foreach (var s in Stages)
        {
            if (s.Kind == StageKind.Implement)
            {
                seenImplement = true;
            }
            else if (s.Kind == StageKind.Review && !seenImplement)
            {
                throw new DomainException(
                    ErrorCodes.WorkflowReviewBeforeImplement,
                    $"{s.Id}: 'review' adimi, kendinden once bir 'implement' adimi olmadan duramaz.");
            }
        }

        if (MaxReviewRounds < 1)
        {
            throw new DomainException(ErrorCodes.WorkflowRoundsMin, "maxReviewRounds en az 1 olmali.");
        }
    }

    /// <summary>Akista gecen her rol ekipte tanimli bir ajan olmali; degilse <c>workflow.unknown_role</c>.</summary>
    public void ValidateAgainst(Team team)
    {
        ArgumentNullException.ThrowIfNull(team);
        var missing = Roles.Where(r => !team.Agents.ContainsKey(r)).ToList();
        if (missing.Count > 0)
        {
            throw new DomainException(ErrorCodes.WorkflowUnknownRole, $"{Key}: ekipte olmayan ajan(lar): {string.Join(", ", missing)}.");
        }
    }

    /// <summary>Bir review adiminin reddettigi isin geri donecegi implement adimi.</summary>
    public Stage ImplementBefore(Stage review)
    {
        var index = Stages.ToList().IndexOf(review);
        for (var i = index - 1; i >= 0; i--)
        {
            if (Stages[i].Kind == StageKind.Implement)
            {
                return Stages[i];
            }
        }

        throw new DomainException(ErrorCodes.WorkflowReviewBeforeImplement, $"{review.Id}: oncesinde implement adimi yok.");
    }
}
