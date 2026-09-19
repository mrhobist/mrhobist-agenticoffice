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

/// <summary>Is akisinin bir adimi. <see cref="Role"/> bir ajan anahtaridir, <see cref="OfficeRole"/> sahnedeki karakter.</summary>
public sealed record Stage(string Id, string Title, StageKind Kind, string Role, string OfficeRole, string Description);

/// <summary>
/// <c>config/workflow.json</c>: scrum board'un koddaki karsiligi. Adim eklemek/silmek kod degisikligi gerektirmez;
/// bozuk bir board calisma ortasinda degil, yuklenirken patlar.
/// </summary>
public sealed record Workflow(int MaxReviewRounds, IReadOnlyList<Stage> Stages)
{
    public static readonly IReadOnlySet<string> ValidOfficeRoles =
        new HashSet<string>(StringComparer.Ordinal) { "pm", "arch", "dev", "qa", "ops", "res", "gate", "designer" };

    public Stage AnalyzeStage => Stages.First(s => s.Kind == StageKind.Analyze);

    /// <summary>Her gorev icin sirayla calisan adimlar (analiz haric).</summary>
    public IReadOnlyList<Stage> TaskStages => Stages.Where(s => s.Kind != StageKind.Analyze).ToList();

    public void Validate()
    {
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

            if (string.IsNullOrWhiteSpace(s.Role))
            {
                throw new DomainException(ErrorCodes.WorkflowInvalidStage, $"{s.Id}: 'role' bos.");
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
