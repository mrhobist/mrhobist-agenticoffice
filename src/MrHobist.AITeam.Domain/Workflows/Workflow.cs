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
public sealed record Workflow(
    string Key,
    string Title,
    int MaxReviewRounds,
    string? HandoffRole,
    IReadOnlyList<Stage> Stages,
    /// <summary>
    /// Takilan ajanin sorusunu KIM cevaplar (docs/DOMAIN.md -> Takilma). Bu AKISIN karari:
    /// <c>null</c> = ajanin kendi <c>can_ask</c>'i (eski davranis) · <see cref="UserRole"/> = kullanici ·
    /// ajan anahtari = o ajan. Akis duzeyinde olmasinin sebebi: manager'i olmayan bir akista developer'in
    /// manager'a sormasi, akista olmayan bir ajani (ve maliyetini) ise sokardi (acik karar #4).
    /// </summary>
    string? AskRole = null,
    /// <summary>
    /// Analistin planini KIM onaylar: <c>null</c> ya da <see cref="UserRole"/> = kullanici (varsayilan) ·
    /// ajan anahtari = o ajan onaylar, reddederse revize notu yazilir ve analiz yeniden kosar.
    /// Bu bir ADIM degil akis ozelligidir: <c>analyze</c> calisma basina bir kez kosar (gorev basina degil),
    /// dolayisiyla gorev duzeyindeki <c>review</c> ile ifade edilemez.
    /// </summary>
    string? PlanApprover = null)
{
    public const string DefaultKey = "default";

    /// <summary>Ajan degil kullanici: <see cref="AskRole"/> ve <see cref="PlanApprover"/> icin ayrilmis deger.</summary>
    public const string UserRole = "user";

    /// <summary>
    /// <see cref="PlanApprover"/> icin ayrilmis deger: onay kapisi YOK, plan uretilir uretilmez dagitima gecilir.
    /// Kullanici karari 2026-09-21: "insan sadece sorulara cevapta olacak" -- plan icin durdurulmaz.
    /// </summary>
    public const string AutoApprove = "auto";

    public static readonly IReadOnlySet<string> ValidOfficeRoles =
        new HashSet<string>(StringComparer.Ordinal) { "pm", "arch", "dev", "qa", "ops", "res", "gate", "designer" };

    public bool IsDefault => Key == DefaultKey;

    /// <summary>Akista gecen ajanlar: adim rolleri + devir rolu, sirali ve tekil.</summary>
    public IReadOnlyList<string> Roles
        => Stages.Select(s => s.Role)
            .Concat(HandoffRole is null ? [] : [HandoffRole])
            .Concat(AgentOrNone(AskRole))
            .Concat(AgentOrNone(PlanApprover))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Ajan anahtari ise tek elemanli, kullanici/bos ise bos: ekip denetimi yalniz gercek ajanlara bakar.</summary>
    private static IEnumerable<string> AgentOrNone(string? role)
        => role is null || role == UserRole || role == AutoApprove ? [] : [role];

    /// <summary>Plan uretilince kullaniciya sorulacak mi: <c>auto</c> ve ajan onayinda HAYIR.</summary>
    public bool PlanNeedsUser => PlanApprover != AutoApprove && PlanApproverAgent is null;

    /// <summary>Plani ajan mi onayliyor; null ise kullanici onaylar (bugunku davranis).</summary>
    public string? PlanApproverAgent => AgentOrNone(PlanApprover).FirstOrDefault();

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

        // Bir review adiminin geri gonderecegi bir URETICI adim bulunmali (design ya da implement).
        // 2026-09-21'de genellestirildi: onceden yalniz implement kabul ediliyordu, dolayisiyla "tasarimi
        // onayla, reddedersen tasarimciya don" ifade edilemiyordu. `analyze` uretici SAYILMAZ: o calisma
        // basina bir kez kosar, gorev duzeyinde geri donulecek bir adim degildir (plan onayi icin
        // <see cref="PlanApprover"/> vardir).
        var seenProducer = false;
        foreach (var s in Stages)
        {
            if (IsProducer(s.Kind))
            {
                seenProducer = true;
            }
            else if (s.Kind == StageKind.Review && !seenProducer)
            {
                throw new DomainException(
                    ErrorCodes.WorkflowReviewBeforeImplement,
                    $"{s.Id}: 'review' adimi, kendinden once bir 'design' ya da 'implement' adimi olmadan duramaz.");
            }
        }

        if (MaxReviewRounds < 1)
        {
            throw new DomainException(ErrorCodes.WorkflowRoundsMin, "maxReviewRounds en az 1 olmali.");
        }

        RequireRoleOrUser(AskRole, "askRole");
        RequireRoleOrUser(PlanApprover, "planApprover");

        void RequireRoleOrUser(string? value, string field)
        {
            if (value is not null && value != UserRole && value != AutoApprove && !Identifiers.IsValidKey(value))
            {
                throw new DomainException(ErrorCodes.WorkflowInvalidStage, $"{Key}: gecersiz {field} '{value}'.");
            }
        }

        if (AskRole == AutoApprove)
        {
            throw new DomainException(ErrorCodes.WorkflowInvalidStage, $"{Key}: askRole '{AutoApprove}' olamaz; soruyu bir yerin cevaplamasi gerekir.");
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

    /// <summary>Gorev ureten adim turleri: reddedilen is bunlardan birine geri doner.</summary>
    public static bool IsProducer(StageKind kind) => kind is StageKind.Design or StageKind.Implement;

    /// <summary>Bir review adiminin reddettigi isin geri donecegi uretici adim; <see cref="Validate"/> varligini garanti eder.</summary>
    public Stage ProducerBefore(Stage review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return ProducerBefore(Stages, review.Id)
            ?? throw new DomainException(ErrorCodes.WorkflowReviewBeforeImplement, $"{review.Id}: oncesinde uretici adim yok.");
    }

    /// <summary>
    /// Geri donus kuralinin TEK kaynagi (docs/DOMAIN.md → Geri donus kurali): verilen adimdan geriye en yakin
    /// URETICI adim (design ya da implement); yoksa null. Dagitici (Dispatcher.NextStage) ve pano hedefi
    /// (RunService) buradan okur. Boylece tasarimi reddeden bir kapi isi tasarimciya, kodu reddeden kapi
    /// developer'a geri gonderir -- kural tek yerde.
    /// </summary>
    public static Stage? ProducerBefore(IReadOnlyList<Stage> stages, string stageId)
    {
        ArgumentNullException.ThrowIfNull(stages);
        for (var i = IndexOf(stages, stageId) - 1; i >= 0; i--)
        {
            if (IsProducer(stages[i].Kind))
            {
                return stages[i];
            }
        }

        return null;
    }

    /// <summary>Adimin listedeki sirasi; yoksa -1.</summary>
    public static int IndexOf(IReadOnlyList<Stage> stages, string stageId)
    {
        ArgumentNullException.ThrowIfNull(stages);
        for (var i = 0; i < stages.Count; i++)
        {
            if (stages[i].Id == stageId)
            {
                return i;
            }
        }

        return -1;
    }
}
