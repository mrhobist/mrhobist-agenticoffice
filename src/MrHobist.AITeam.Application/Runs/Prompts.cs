using System.Text;
using MrHobist.AITeam.Domain.Agents;
using MrHobist.AITeam.Domain.Runs;
using MrHobist.AITeam.Domain.Workflows;

namespace MrHobist.AITeam.Application.Runs;

/// <summary>Bagimli olunan, bitmis bir gorevin raporu: sonraki gorev ayni dosyalari yeniden kesfetmesin.</summary>
public sealed record PriorTask(string Id, string Title, string Report);

/// <summary>
/// Ajanlara giden kullanici mesajlari. Sistem promptu ajan md'sinden gelir; burasi calismaya ozel baglamdir.
/// Metin degisikligi orkestrasyona dokunmadan yapilir.
/// </summary>
public static class Prompts
{
    /// <summary>Analistin ilk mesaji: brief, proje dizini, akis, beklenen sema.</summary>
    public static string AnalystBrief(Run run, Workflow wf, string projectRoot, IReadOnlyList<Knowledge>? knowledge = null)
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
        // Onay cumlesi akisa bagli: `auto` akista "insan onaylayacak" demek ajani yaniltiyordu (2026-09-23 incelemesi).
        sb.AppendLine(wf.PlanNeedsUser
            ? "Bu plan bir insanın onayına sunulacak; onaylanmadan hiçbir iş başlamaz."
            : wf.PlanApproverAgent is { } approver
                ? $"Bu planı `{approver}` onaylayacak; onaylanmadan hiçbir iş başlamaz."
                : "Bu plan onaya sunulmaz: üretilir üretilmez uygulanır. Kimse gözden geçirmeyecek, planı buna göre sağlam kur.");
        sb.AppendLine("Belirsizlikte en makul varsayımı seç ve rules içinde açıkça yaz.");
        // 2026-09-23 maliyet kaldiraclari: her gorev tum kurallari ve tum bilgi dosyalarini her ic turda yeniden okuyordu.
        sb.AppendLine("Her görevin ruleRefs alanına o görevi bağlayan kuralların 0 tabanlı sıra numaralarını yaz (tüm görevleri bağlayan kural her görevde yer alır); emin değilsen boş bırak, o zaman tüm kurallar gider.");
        if (knowledge is { Count: > 1 })
        {
            sb.AppendLine().AppendLine("# Bilgi dosyaları");
            sb.AppendLine("Uygulayıcının sistem istemine eklenebilecek referans belgeler. knowledge alanına bu işin GERÇEKTEN ihtiyaç duyduklarının anahtarlarını yaz (ör. iş yalnız ön yüzse yalnız ön yüz belgesi); emin değilsen boş bırak, hepsi gider.");
            foreach (var k in knowledge)
            {
                sb.Append("- `").Append(k.Key).Append("` — ").AppendLine(k.Title);
            }
        }

        return sb.ToString();
    }

    /// <summary>Kullanicinin revize notu, analistin gecmisine kullanici mesaji olarak eklenir.</summary>
    /// <summary>
    /// Plani onaylayan ajanin istemi (akis <c>planApprover</c> verdiginde insanin yerine gecer).
    /// Kod yazdirmaz, dosya okutmaz: karar PLANIN kendisi uzerine verilir.
    /// </summary>
    public static string PlanApproval(Run run, Spec spec, int round)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(spec);
        var sb = new StringBuilder();
        sb.Append("# Plan onayı (tur ").Append(round).AppendLine(")");
        sb.AppendLine().AppendLine("Analist aşağıdaki planı üretti. Senin işin PLANI onaylamak ya da gerekçeyle reddetmek.");
        sb.AppendLine("Kod yazma, dosya açma, komut çalıştırma: karar planın kendisi üzerine verilir.").AppendLine();
        sb.AppendLine("## Kullanıcının isteği").AppendLine().AppendLine(run.Brief.Trim()).AppendLine();
        sb.AppendLine("## Plan").AppendLine().Append("Özet: ").AppendLine(spec.Summary);
        sb.Append("Mimari: ").AppendLine(spec.Architecture);
        if (spec.Rules.Count > 0)
        {
            sb.AppendLine().AppendLine("Kurallar:");
            foreach (var r in spec.Rules)
            {
                sb.Append("- ").AppendLine(r);
            }
        }

        sb.AppendLine().AppendLine("Görevler:");
        foreach (var t in spec.Tasks)
        {
            sb.Append("- [").Append(t.Id).Append("] ").Append(t.Title).Append(" — ").AppendLine(t.Description);
            if (t.Acceptance.Count > 0)
            {
                sb.Append("  kabul: ").AppendLine(string.Join(" · ", t.Acceptance));
            }
        }

        sb.AppendLine().AppendLine("## Karar");
        sb.AppendLine("- Plan kullanıcının isteğini karşılıyorsa ve kabul ölçütleri ölçülebilirse: `accept`.");
        sb.AppendLine("- Eksik, fazla kapsamlı ya da ölçülemez ise: `reject` ve `feedback` alanına analistin NE değiştireceğini somut yaz.");
        sb.AppendLine("- `testsRun` false, `commandsRun` boş bırak: bu adımda komut çalıştırılmaz.");
        return sb.ToString();
    }

    public static string RevisionNote(string note)
        => $"# Revize notu\n{note}\n\nPlanı bu nota göre güncelle; değişmeyen kısımları koru. Aynı şemayla planın tamamını yeniden ver.";

    /// <summary>Organizatore devir notu istegi: gorev, biten/sonraki adim, plan baglami.</summary>
    /// <summary>
    /// Devir notu. **LLM CAGRILMAZ** (kullanici karari 2026-09-21): organizator akista ajanlar arasi
    /// aktarimi yapar, tur harcamaz. Olculmustu: her devir 14-70 s ve yaklasik $0.02-0.07 ediyordu,
    /// urettigi metin ise zaten gorev baglaminda (<see cref="TaskContext"/>) bulunan bilgilerin
    /// yeniden yazimiydi.
    ///
    /// Not KISA tutulur: sonraki rolun istemine eklenir, uzun olursa bedeli her turda odenir.
    /// Degeri bilgi katmak degil, AKTARIMI kayda gecirmek: kim kime, hangi adimda, kacinci turda.
    /// </summary>
    public static string HandoffNote(Assignment a, string toName, int round, IReadOnlyList<Message> notes)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(notes);
        var sb = new StringBuilder();
        sb.Append("Devir: ").Append(a.Task.Id).Append(" → ").Append(toName)
          .Append(" · ").Append(a.Stage.Title).Append(" · tur ").Append(round).AppendLine();

        if (round > 1)
        {
            // Red sonrasi atama: neyin duzeltilecegi notun ilk satirinda dursun.
            var last = notes.LastOrDefault(m => m.Subject == "review-feedback");
            var first = last?.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            sb.Append("Önceki tur reddedildi").Append(string.IsNullOrEmpty(first) ? "." : ": " + first).AppendLine();
        }

        if (a.Task.DependsOn.Count > 0)
        {
            sb.Append("Bağımlılıklar bitti: ").AppendJoin(", ", a.Task.DependsOn).AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// <paramref name="task"/>'in (dolayli) bagimliliklarinin son uygulama raporu, plandaki sirayla. Rapor kisaltilir: amac
    /// "hangi dosyada ne var" bilgisini vermek, tekrar okumayi kesmek (2026-09-23: gorev basina tekrar kesif ~%6 maliyet).
    /// </summary>
    public static IReadOnlyList<PriorTask> PriorTasks(Spec spec, RunTask task, IReadOnlyList<Message> messages, int maxChars = 1500)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(messages);
        var byId = spec.Tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(task.DependsOn);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (id != task.Id && ancestors.Add(id) && byId.TryGetValue(id, out var t))
            {
                foreach (var d in t.DependsOn)
                {
                    stack.Push(d);
                }
            }
        }

        var list = new List<PriorTask>();
        foreach (var t in spec.Tasks.Where(t => ancestors.Contains(t.Id)))
        {
            var report = messages.Where(m => m.Task == t.Id && m.Subject == "implement-report").OrderBy(m => m.Ts).LastOrDefault();
            if (report is not null)
            {
                var body = report.Body.Trim();
                list.Add(new PriorTask(t.Id, t.Title, body.Length <= maxChars ? body : body[..(maxChars - 1)] + "…"));
            }
        }

        return list;
    }

    /// <summary>Gorev baglami: plan + gorev + kurallar + dizin. Uc yurutucu de bunu kullanir.</summary>
    private static StringBuilder TaskContext(Spec spec, Assignment a, string projectRoot, IReadOnlyList<Message> notes, IReadOnlyList<PriorTask>? prior = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Görev {a.Task.Id} — {a.Task.Title}").AppendLine(a.Task.Description).AppendLine();
        sb.AppendLine("## Kabul ölçütleri").AppendJoin('\n', a.Task.Acceptance.Select(x => "- " + x)).AppendLine();
        sb.AppendLine("## Dosyalar").AppendJoin('\n', a.Task.Files.Select(x => "- " + x)).AppendLine().AppendLine();
        sb.AppendLine("# Çalışma dizini");
        sb.AppendLine($"`{projectRoot}` — araçların bu dizinde açıldı; yollar buna göre görelidir. Bu dizinin DIŞINA yazma (engellenir).").AppendLine();
        sb.AppendLine("# Plan").AppendLine("## Özet").AppendLine(spec.Summary).AppendLine("## Mimari").AppendLine(spec.Architecture).AppendLine();
        var refs = a.Task.RuleRefs?.Where(i => i >= 0 && i < spec.Rules.Count).Distinct().Order().ToList();
        var rules = refs is { Count: > 0 } ? refs.Select(i => spec.Rules[i]).ToList() : spec.Rules;
        sb.AppendLine("## Bağlayıcı kurallar").AppendJoin('\n', rules.Select(x => "- " + x)).AppendLine();
        if (rules.Count < spec.Rules.Count)
        {
            sb.AppendLine($"(Plandaki diğer {spec.Rules.Count - rules.Count} kural başka görevlere ait.)");
        }

        sb.AppendLine();
        if (prior is { Count: > 0 })
        {
            sb.AppendLine("# Bağımlı olduğun biten görevler");
            sb.AppendLine("Bu dosyalar yazıldı ve doğrulandı. Tamamını yeniden okuma; yalnız ihtiyacın olan imzaya/sözleşmeye bak.");
            foreach (var p in prior)
            {
                sb.AppendLine($"## {p.Id} — {p.Title}").AppendLine(p.Report).AppendLine();
            }
        }

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
    public static string ImplementTask(Spec spec, Assignment a, string projectRoot, IReadOnlyList<Message> notes, int round, bool resumed = false, IReadOnlyList<PriorTask>? prior = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(a);
        var sb = TaskContext(spec, a, projectRoot, notes, prior);
        sb.AppendLine("# Yapılacak");
        sb.AppendLine(round > 1
            ? $"Bu görevin {round}. turu: yukarıdaki geri bildirimi (red/hata notu) MADDE MADDE gider, sonra kabul ölçütlerini yeniden doğrula."
            : "Görevi uygula: dosyaları Write/Edit ile yaz, gerekiyorsa Bash ile build/test kos ve çıktısını kontrol et.");
        if (resumed)
        {
            // 2026-09-23: kesilen tur diskte bitmis is birakmisti (build temiz, testler gecer); ajan bastan yazsaydi ayni turu iki kez odenirdi.
            sb.AppendLine("DEVAM: bu görevin önceki denemesi yarıda KESİLDİ (zaman aşımı ya da süreç yeniden başladı), raporu alınamadı. Dizindeki dosyalar o denemeden kaldı ve büyük ölçüde senin işin: BAŞTAN YAZMA. "
                + "Önce durumu çıkar (dosyalara bak, build/test koş), yalnız eksik ya da bozuk olanı tamamla, sonra kabul ölçütlerini doğrula ve raporu ver.");
        }

        sb.AppendLine("Önce dizine bak (Glob/Read); var olan dosyayı ezmeden değiştir. Kabul ölçütlerindeki komutları FİİLEN çalıştır ve geçtiğini gör.");
        // Var olan bir depoya (kendi CLAUDE.md'si, kendi baslatma yolu olan) run.cmd eklemek o depoyu kirletiyordu (2026-09-23).
        sb.AppendLine("Ofisteki \"Projeyi başlat\" düğmesi proje kökündeki `run.cmd` dosyasını YENİ BİR KONSOL PENCERESİNDE çalıştırır. Uygulamayı SIFIRDAN kuruyorsan, çalıştırılabilir hâle gelince bu dosyayı yaz ya da güncelle. "
            + "Kendi başlatma yolu olan var olan bir depoda (kökte `CLAUDE.md`, betikler, launch ayarları) `run.cmd` yoksa EKLEME — o depo kullanıcının kendi yoluyla başlatılır. "
            + "İçeriği ASCII olsun; `@echo off`, `cd /d \"%~dp0\"`, sonra uygulamayı başlatan komut (konsol uygulaması: `dotnet run --project ...` ve bitince `pause`; web: sunucuyu başlat ve `start http://127.0.0.1:PORT`; masaüstü/oyun: exe). Kurulum gereken projede (npm install, restore) bunu da run.cmd yapsın.");
        sb.AppendLine("Kural çelişkisi ya da eksik bilgi varsa TAHMİN ETME: blocked=true ve question ile sor; işi yarım bırak.");
        sb.AppendLine("Bitince verilen JSON şemasına uyan raporu ver: summary, filesChanged (göreli yollar), commandsRun, blocked, question.");
        return sb.ToString();
    }

    /// <summary>
    /// Inceleme adimi. Istem, kapinin BEKLETTIGI URETICI adima gore sekillenir
    /// (<see cref="Workflow.ProducerBefore(Stage)"/>): <c>implement</c> kapisinda ortada kod vardir, komutlar fiilen
    /// kosulur; <c>design</c> kapisinda HENUZ KOD YOKTUR, degerlendirilen sey rehber metnidir.
    ///
    /// Tek istem ikisine birden uymuyordu: tasarim kapisindaki ajana "developer dosyalari yazdi, build'i kos"
    /// deniyor, kosacak sey bulamiyor, sonra "supheyle reddet" talimatini uyguluyordu. Olculdu 2026-09-21:
    /// tam-kadro ard arda 3 red verdi, $1.70 harcadi, tek satir kod uretmedi (docs/LESSONS.md).
    /// Supheyle red yonu de kapiya baglidir: ARA kapida red bedava degil, bir tur daha maliyet demek ve
    /// eksigi zaten sonraki test adimi yakalar; SON kapida ise hatali kodu gecirmek daha pahalidir.
    /// </summary>
    public static string ReviewTask(Spec spec, Assignment a, string projectRoot, IReadOnlyList<Message> notes, Stage stage, Stage producer, int round, int maxRounds, IReadOnlyList<PriorTask>? prior = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(producer);
        var sb = TaskContext(spec, a, projectRoot, notes, prior);
        sb.AppendLine($"# Yapılacak — {stage.Title} ({round}/{maxRounds}. tur)");
        sb.AppendLine(stage.Description);

        if (producer.Kind == StageKind.Design)
        {
            sb.AppendLine($"Değerlendirdiğin şey KOD DEĞİL: \"{producer.Title}\" adımının ürettiği tasarım rehberi. Bu noktada dizinde henüz kod yok — build/test koşma, komut çalıştırma: commandsRun boş kalsın, testsRun=false.");
            sb.AppendLine("Tek ölçüt şu: developer bu rehberle işe başlayıp TAHMİN ETMEDEN ilerleyebilir mi? Kabul ölçütlerinin her biri için rehberde bir karşılık var mı?");
            sb.AppendLine("Şüphedeyken KABUL ET. Burası bir ARA kapı: eksik kalanı ilerideki test adımı zaten yakalar, ama her red bir tur daha maliyet demektir. Red yalnız developer'ı GERÇEKTEN tıkayan bir boşluk için doğrudur: çelişkili karar, ya da hiç karşılığı olmayan bir kabul ölçütü.");
            sb.AppendLine($"RED verirsen iş \"{producer.Title}\" adımına döner ve feedback oraya gider: hangi kabul ölçütü karşılıksız, ne eklenmeli.");
        }
        else
        {
            sb.AppendLine($"\"{producer.Title}\" adımı dosyaları bu dizine yazdı. Read/Glob/Grep ile kodu oku; kabul ölçütlerindeki ve kurallardaki komutları Bash ile FİİLEN çalıştır (build, test, çalıştırma). Tahminle karar verme.");
            sb.AppendLine("Gerekirse test dosyası yazabilirsin; uygulama kodunu DEĞİŞTİRME — düzeltme üreten adımın işidir, feedback'e yaz.");
            sb.AppendLine("Her kural ve kabul ölçütünü tek tek kontrol et. İhlal varsa verdict=reject ve findings'e yaz; feedback doğrudan üreten ajana gider: somut, adım adım.");
            sb.AppendLine("Kozmetik tercih için reddetme. Şüphedeyken reddet. testsRun yalnız fiilen çalıştırdıysan true.");
        }

        sb.AppendLine("Kapsamla orantılı ol: brief'in ve kabul ölçütlerinin istemediği ek özellik, ek belge ya da ek mimari talep etme. Küçük bir iş küçük bir çıktı ister.");
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

    /// <summary>
    /// <c>can_ask</c> hedefine (manager) giden soru: gorev baglami + takilan ajanin raporu + sorusu. Hedef kod yazmaz;
    /// dizini okuyabilir. Tek karar ister; yetki disiysa yukseltir (docs/DOMAIN.md → Takilma, ajan → ajan sorusu).
    /// </summary>
    public static string AskColleague(Spec spec, Assignment a, string projectRoot, IReadOnlyList<Message> notes, string askerName, string question, string report)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(a);
        var sb = TaskContext(spec, a, projectRoot, notes);
        sb.AppendLine($"# Soru — {askerName} ({a.Agent}) takıldı");
        sb.AppendLine("## Raporu").AppendLine(report).AppendLine();
        sb.AppendLine("## Sorusu").AppendLine(question).AppendLine();
        sb.AppendLine("# Yapılacak");
        sb.AppendLine($"Sen bu ajanın sorabileceği kişisin; kararı SEN verirsin, kullanıcı rahatsız edilmez. TEK net karar ver: {askerName} cevabınla hemen devam edebilmeli. Bilgi eksikse en makul varsayımı seç ve varsayımı açıkça yaz. Kod yazma, dosya değiştirme; dizini okuyabilirsin.");
        sb.AppendLine("Karar yetkinin dışındaysa (kapsam daralması/genişlemesi, bütçe, dış sistem erişimi, kullanıcının kişisel tercihi) tahmin ETME: escalate=true ve reason ile kullanıcıya bırak.");
        sb.AppendLine("Verilen JSON şemasına uyan cevabı ver: answer, escalate, reason.");
        return sb.ToString();
    }

    /// <summary>Kullanicinin takilma cevabi (retry notu): ilgili ajana bir sonraki turda notlar arasinda gider.</summary>
    public static string UserAnswer(string choice, string? note)
        => string.IsNullOrWhiteSpace(note) ? $"Kullanıcı kararı: {choice}." : $"Kullanıcı kararı: {choice}.\n{note.Trim()}";
}
