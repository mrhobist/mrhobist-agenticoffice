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
    /// <summary>Analistin ilk mesaji: brief, proje dizini, akis, beklenen sema.</summary>
    public static string AnalystBrief(Run run, Workflow wf, string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(wf);
        var sb = new StringBuilder();
        sb.AppendLine("# Brief").AppendLine(run.Brief).AppendLine();
        sb.AppendLine("# Proje dizini");
        sb.AppendLine($"Tüm dosyalar şu dizinin İÇİNDE yaşar: `{projectRoot}`. Plandaki dosya yolları bu dizine göre GÖRELİ yazılır (ör. `src/App/Program.cs`), dizinin adı yola eklenmez, dışına çıkılmaz.");
        sb.AppendLine("Dizinde zaten kod olabilir; okuma araçların varsa önce bak, var olanın üstüne planla.").AppendLine();
        sb.AppendLine("# İş akışı");
        sb.AppendLine("Her görev sırayla şu adımlardan geçer: " + string.Join(" → ", wf.TaskStages.Select(s => $"{s.Title} ({s.Role})")) + ".");
        sb.AppendLine("Sen yalnız planı üretirsin; kod yazmazsın.").AppendLine();
        sb.AppendLine("# Beklenen çıktı");
        sb.AppendLine("Verilen JSON şemasına birebir uyan bir plan: summary, architecture, rules[], tasks[].");
        sb.AppendLine("Görev kimlikleri kısa ve küçük harf (t1, api-ucu). Görevler tek bir ajanın tek oturumda bitirebileceği büyüklükte olsun; gereksiz parçalama yapma (hello world tek görevdir).");
        sb.AppendLine("Kabul ölçütleri çalıştırılabilir olsun (build/test komutu, beklenen çıktı). dependsOn yalnız gerçek bağımlılıkları içersin; sıralama bundan türetilir.");
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
        sb.AppendLine($"Sonraki adım: {a.Stage.Title} — {toName} ({a.Agent}).").AppendLine();
        sb.AppendLine("# Plan özeti").AppendLine(spec.Summary).AppendLine();
        sb.AppendLine("# Kurallar").AppendJoin('\n', spec.Rules.Select(x => "- " + x)).AppendLine();
        sb.AppendLine().AppendLine($"{toName} için kısa (en fazla 10 satır) bir devir notu yaz: ne bitti, ne bekleniyor, nereye dikkat.");
        return sb.ToString();
    }

    /// <summary>Gorev baglami: plan + gorev + kurallar + dizin. Uc yurutucu de bunu kullanir.</summary>
    private static StringBuilder TaskContext(Spec spec, Assignment a, string projectRoot, IReadOnlyList<Message> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Görev {a.Task.Id} — {a.Task.Title}").AppendLine(a.Task.Description).AppendLine();
        sb.AppendLine("## Kabul ölçütleri").AppendJoin('\n', a.Task.Acceptance.Select(x => "- " + x)).AppendLine();
        sb.AppendLine("## Dosyalar").AppendJoin('\n', a.Task.Files.Select(x => "- " + x)).AppendLine().AppendLine();
        sb.AppendLine("# Çalışma dizini");
        sb.AppendLine($"`{projectRoot}` — araçların bu dizinde açıldı; yollar buna göre görelidir. Bu dizinin DIŞINA yazma (engellenir).").AppendLine();
        sb.AppendLine("# Plan").AppendLine("## Özet").AppendLine(spec.Summary).AppendLine("## Mimari").AppendLine(spec.Architecture).AppendLine();
        sb.AppendLine("## Bağlayıcı kurallar").AppendJoin('\n', spec.Rules.Select(x => "- " + x)).AppendLine().AppendLine();
        if (notes.Count > 0)
        {
            sb.AppendLine("# Bu görevle ilgili notlar (eskiden yeniye)");
            foreach (var n in notes)
            {
                sb.AppendLine($"## {n.From} → {n.To} · {n.Subject} · {n.Ts:HH:mm}").AppendLine(n.Body).AppendLine();
            }
        }

        return sb;
    }

    /// <summary>Developer: araclarla dosyalari yazar, build'i kosar, sonunda rapor semasini doldurur.</summary>
    public static string ImplementTask(Spec spec, Assignment a, string projectRoot, IReadOnlyList<Message> notes, int round)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(a);
        var sb = TaskContext(spec, a, projectRoot, notes);
        sb.AppendLine("# Yapılacak");
        sb.AppendLine(round > 1
            ? $"Bu görevin {round}. turu: yukarıdaki geri bildirimi (red/hata notu) MADDE MADDE gider, sonra kabul ölçütlerini yeniden doğrula."
            : "Görevi uygula: dosyaları Write/Edit ile yaz, gerekiyorsa Bash ile build/test kos ve çıktısını kontrol et.");
        sb.AppendLine("Önce dizine bak (Glob/Read); var olan dosyayı ezmeden değiştir. Kabul ölçütlerindeki komutları FİİLEN çalıştır ve geçtiğini gör.");
        sb.AppendLine("Kural çelişkisi ya da eksik bilgi varsa TAHMİN ETME: blocked=true ve question ile sor; işi yarım bırak.");
        sb.AppendLine("Bitince verilen JSON şemasına uyan raporu ver: summary, filesChanged (göreli yollar), commandsRun, blocked, question.");
        return sb.ToString();
    }

    /// <summary>Testci / manager: kurallari denetler, testi kosar, kabul ya da gerekceli red.</summary>
    public static string ReviewTask(Spec spec, Assignment a, string projectRoot, IReadOnlyList<Message> notes, Stage stage, int round, int maxRounds)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(stage);
        var sb = TaskContext(spec, a, projectRoot, notes);
        sb.AppendLine($"# Yapılacak — {stage.Title} ({round}/{maxRounds}. tur)");
        sb.AppendLine(stage.Description);
        sb.AppendLine("Developer dosyaları bu dizine yazdı. Read/Glob/Grep ile kodu oku; kabul ölçütlerindeki ve kurallardaki komutları Bash ile FİİLEN çalıştır (build, test, çalıştırma). Tahminle karar verme.");
        sb.AppendLine("Gerekirse test dosyası yazabilirsin; uygulama kodunu DEĞİŞTİRME — düzeltme developer'ın işidir, feedback'e yaz.");
        sb.AppendLine("Her kural ve kabul ölçütünü tek tek kontrol et. İhlal varsa verdict=reject ve findings'e yaz; feedback developer'a doğrudan gider: somut, adım adım.");
        sb.AppendLine("Kozmetik tercih için reddetme. Şüphedeyken reddet. testsRun yalnız fiilen çalıştırdıysan true.");
        sb.AppendLine("Verilen JSON şemasına uyan raporu ver: verdict, testsRun, findings, feedback, commandsRun.");
        return sb.ToString();
    }

    /// <summary>Tasarimci: kod yazmaz, developer'in uyacagi rehberligi uretir.</summary>
    public static string DesignTask(Spec spec, Assignment a, string projectRoot, IReadOnlyList<Message> notes)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(a);
        var sb = TaskContext(spec, a, projectRoot, notes);
        sb.AppendLine("# Yapılacak — Tasarım");
        sb.AppendLine("Bu görev için developer'ın uyacağı tasarım rehberliğini yaz: arayüz/akış kararları, isimlendirme, hata durumları, kabul ölçütlerine nasıl ulaşılacağı. Kod yazma, dosya değiştirme. Dizini okuyabilirsin.");
        sb.AppendLine("Verilen JSON şemasına uyan çıktıyı ver: guidance, decisions.");
        return sb.ToString();
    }

    /// <summary>Kullanicinin takilma cevabi (retry notu): ilgili ajana bir sonraki turda notlar arasinda gider.</summary>
    public static string UserAnswer(string choice, string? note)
        => string.IsNullOrWhiteSpace(note) ? $"Kullanıcı kararı: {choice}." : $"Kullanıcı kararı: {choice}.\n{note.Trim()}";
}
