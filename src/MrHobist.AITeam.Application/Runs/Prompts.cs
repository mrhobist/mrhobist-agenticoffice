using System.Text;
using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Domain.Workflows;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>
/// Ajanlara giden kullanici mesajlari. Sistem promptu ajan md'sinden gelir; burasi calismaya ozel baglamdir.
/// Metin degisikligi orkestrasyona dokunmadan yapilir.
/// </summary>
public static class Prompts
{
    /// <summary>Analistin ilk mesaji: brief, akis, beklenen sema.</summary>
    public static string AnalystBrief(Run run, Workflow wf)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(wf);
        var sb = new StringBuilder();
        sb.AppendLine("# Brief").AppendLine(run.Brief).AppendLine();
        sb.AppendLine("# İş akışı");
        sb.AppendLine("Her görev sırayla şu adımlardan geçer: " + string.Join(" → ", wf.TaskStages.Select(s => $"{s.Title} ({s.Role})")) + ".");
        sb.AppendLine("Sen yalnız planı üretirsin; kod yazmazsın.").AppendLine();
        sb.AppendLine("# Beklenen çıktı");
        sb.AppendLine("Verilen JSON şemasına birebir uyan bir plan: summary, architecture, rules[], tasks[].");
        sb.AppendLine("Görev kimlikleri kısa ve küçük harf (t1, api-ucu). Görevler tek bir ajanın tek oturumda bitirebileceği büyüklükte olsun.");
        sb.AppendLine("dependsOn yalnız gerçek bağımlılıkları içersin; sıralama bundan türetilir.");
        sb.AppendLine("Bu plan bir insanın onayına sunulacak; onaylanmadan hiçbir iş başlamaz. Belirsizlikte en makul varsayımı seç ve rules içinde açıkça yaz.");
        return sb.ToString();
    }

    /// <summary>Kullanicinin revize notu, analistin gecmisine kullanici mesaji olarak eklenir.</summary>
    public static string RevisionNote(string note)
        => $"# Revize notu\n{note}\n\nPlanı bu nota göre güncelle; değişmeyen kısımları koru. Aynı şemayla planın tamamını yeniden ver.";

    /// <summary>Organizatore devir notu istegi: gorev, biten/sonraki adim, plan baglami.</summary>
    public static string HandoffRequest(Spec spec, Assignment a, string toName)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(a);
        var sb = new StringBuilder();
        sb.AppendLine("# Devir").AppendLine($"Görev: {a.Task.Id} — {a.Task.Title}").AppendLine(a.Task.Description).AppendLine();
        sb.AppendLine("Kabul ölçütleri:").AppendJoin('\n', a.Task.Acceptance.Select(x => "- " + x)).AppendLine();
        sb.AppendLine($"Dosyalar: {string.Join(", ", a.Task.Files)}");
        sb.AppendLine($"Bağımlılıklar: {(a.Task.DependsOn.Count == 0 ? "yok" : string.Join(", ", a.Task.DependsOn))}").AppendLine();
        sb.AppendLine("Biten adım: Analiz — plan insan tarafından onaylandı.");
        sb.AppendLine($"Sonraki adım: {a.Stage.Title} — {toName} ({a.Agent}).").AppendLine();
        sb.AppendLine("# Plan özeti").AppendLine(spec.Summary).AppendLine();
        sb.AppendLine("# Mimari").AppendLine(spec.Architecture).AppendLine();
        sb.AppendLine("# Kurallar").AppendJoin('\n', spec.Rules.Select(x => "- " + x)).AppendLine();
        sb.AppendLine().AppendLine($"{toName} için kısa bir devir notu yaz.");
        return sb.ToString();
    }
}
