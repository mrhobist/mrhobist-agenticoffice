# Alan modeli ve akış kuralları

Roller, çalışma yaşam döngüsü, plan onayı, organizatörün dağıtım kuralı, devir ve sahne eşlemesi.
Bu belge **davranış** sözleşmesidir; HTTP biçimi `docs/API.md`, sahne olayları `docs/SCENE.md`.
Kararlar tarihli alınır; "varsayımla ilerlenir" işaretli olanlar kullanıcıya sorulmadan seçildi.

## Roller

Ekip **açıktır** (zorunlu rol yok); hangi ajanın çalışacağını iş akışı belirler. Bugünkü kadro:

| Ajan | Ne yapar | Kod mu prompt mu |
|---|---|---|
| `analyst` | Brief'i çözümler; özet, mimari, bağlayıcı kurallar ve bağımlılık sıralı görev listesi üretir. Onay gelmeden plan **değişebilir**, iş açılmaz | prompt |
| `designer` | Kullanıcıya dokunan yüzeylerin yapısını ve akışını belirler; kod yazmaz | prompt |
| `developer` | Görevi kodlar. **İlk teslimde henüz çalışmaz**: iş masasına gelir, bekler (bkz. Adım yürütücüleri) | prompt |
| `tester` | Kuralları denetler, testi çalıştırır, onaylar ya da reddeder | prompt |
| `manager` | `ask` hedefi ve son karar kapısı (altı şapka) | prompt |
| `organizer` | **Dağıtıcı.** Bekleyen iş var mı, boş ajan var mı bakar, işi verir. Bu mantık **koddadır** (`Dispatcher`), sıfır token. LLM'e yalnız **devir notu** için gider | kod + prompt |

## Projeler (2026-09-19, kullanıcı kararı)

> Her işin bir projesi vardır; proje bağımsız iş başlatılamaz (`run.project_required`).

- Proje `config/projects/{key}.json`: **başlık, açıklama, varsayılan iş akışı, hedef dizin** (`projects/{key}`
  varsayılan; depo içinde göreli, `..` yok). **Bütçe projede yoktur**, iş başınadır (kullanıcı kararı).
- İş projenin akışını devralır; formda değiştirilebilir. Hedef dizin, geliştirme yürütücüsü gelince developer'ın
  dosya yazacağı yerdir.
- Ekip ve bilgi dosyaları çalışma alanı düzeyinde ortaktır; proje yalnız bir akış seçer.
- Kart özeti (`ProjectCard`): iş sayıları duruma göre, toplam maliyet, son hareket. UI'da ray kartları bunu gösterir.
- Silme: içinde çalışma varsa **409 `project.in_use`**; geçmiş silinmez.
- **Giriş hazırlığı:** proje ve çalışmada `ownerId`; bugün sabit `local`. JWT gelince claim'den dolar; giriş
  ekranı tek kullanıcıda atlanır (tasarım: Login artboard'u).
- Geçiş: 2026-09-19'a kadarki projesiz çalışmalar **silindi** (kullanıcı kararı; hepsi deneme kaydıydı).

## Çalışma yaşam döngüsü

```
POST /runs ──► Running(analiz) ──► AwaitingApproval ◄──┐
                                        │ approve      │ revise {note}
                                        ▼              │ (analist yeniden)
                                  Running(dağıtım) ────┘
                                        │
                     ┌──────────────────┼──────────────────┐
                     ▼                  ▼                  ▼
                  Paused            Completed         Failed / Interrupted /
        (yürütücüsü olmayan adım)                    BudgetExceeded / PolicyRejected
                     │                                      │ retry (kaldığı adımdan)
   cancel ───────────┴──► Cancelled ◄── cancel (Running / AwaitingApproval / Paused / Failed / Interrupted / BudgetExceeded)
                              │ retry
                              └──► Running ya da AwaitingApproval (onay beklerken iptal edilmişse)
```

`RunStatus` üyeleri (adıyla taşınır, **sona eklenir**): `Running, Completed, Failed, Interrupted,
BudgetExceeded, PolicyRejected, AwaitingApproval, Paused, Cancelled`.

- **Akış donar (2026-09-19):** çalışma başlarken seçilen akış `runs/<id>/workflow.json` olarak
  kopyalanır; `run.json` içinde `workflow` anahtarı tutulur. `config/workflows/` sonradan değişse de
  çalışma kendi kopyasını okur; pano da onu kurar (`workflow.set { key }` olayı).
- **Hassasiyet** çalışma başında verilir; boşsa `anthropic` (varsayımla ilerlenir: bugün tek
  sağlayıcı Anthropic). Politikaya aykırı bir ajan varsa çalışma **hiç başlamaz** (`PolicyRejected`).
- **Süreç ölürse** yeniden başlatmada `Running` olan çalışmalar `Interrupted` işaretlenir.

## Gelen kutusu (2026-09-19, kullanıcı isteği)

> "Bana atanan işleri, benden beklenen cevapları göreyim; soru olduğunu anlayayım; kaç iş var bileyim."

Kullanıcıdan bir şey bekleyen her çalışma **gelen kutusuna** düşer (`GET /runs/overview → inbox`, türü
`InboxKind`). Bu bir türetimdir, ayrı bir durum tutulmaz (CLAUDE.md §2: durum tek yerde):

| Tür | Ne zaman | Beklenen cevap |
|---|---|---|
| `approval` (soru) | `AwaitingApproval` | **Onayla** ya da **Revize et** (not) |
| `decision` (karar) | `Paused`, `Failed`, `Interrupted`, `BudgetExceeded` | **Yeniden dene** (`retry`) ya da **Kapat** (`cancel` → `Cancelled`); Paused yalnız kapatılabilir. Kapatmadan kutudan düşmez — "okundu" yok, karar var |
| `question` (soru) | bir ajan `kind: ask, to: user` yazdı ve `ref`'i eşleşen `answer` yok | cevap ucu **henüz yok** — sözleşme ileride ajan → kullanıcı sorusu için hazır |

`Completed`, `Cancelled`, `PolicyRejected` bir şey beklemez. UI bunu üç yerde gösterir: üst barda **İşler**
düğmesi (toplam iş + kırmızı "senden bekleyen" rozeti, sekme başlığında `(N)`), üst şeritte **"Senden cevap
bekleniyor"** uyarısı (ilk madde + "Cevapla"), sahnedeki panoda kırmızı rozet ve büyük Kanban'ın üstünde
"Senden bekleniyor" şeridi (onaysız plan panoya iş açmadığı için soru orada başka türlü görünmezdi).
**Varsayımla ilerlenir:** gelen kutusu 5 s'de bir yoklanır (SSE gelince olaya bağlanır); "okundu" kavramı yok,
madde ancak cevap verilince düşer.

## Plan onayı (2026-09-19, kullanıcı kararı)

> Developer'a iş gitmeden analistin çıkaracağı iş listesi, sırası ve detayları **insan onayından**
> geçer. Onaysız hiçbir adım başlamaz, panoya iş açılmaz. İstisna yoktur.

1. Analist brief'i alır, `Spec` şemasıyla yapısal çıktı üretir → `runs/<id>/spec.json`.
   Çalışma `AwaitingApproval` olur; sahnede analist `done`, not: "plan onay bekliyor".
2. Kullanıcı UI'da planı görür: özet, mimari, kurallar, görevler (kimlik, başlık, açıklama,
   dosyalar, kabul ölçütleri, bağımlılıklar) ve **yürütme sırası** (topolojik).
3. **Onayla** → `Running`; `board.set` ile görevler ilk görev-sütununda `queued` açılır; dağıtım başlar.
4. **Revize et** (`note` zorunlu) → not `messages.jsonl`'e yazılır (`from: user, to: analyst,
   kind: Note, subject: plan-revision`). Analist **geçmişle** yeniden çağrılır: `[brief] →
   [önceki plan JSON] → [not]`. Yeni plan `spec.json`'ın üzerine yazılır (eski sürümler
   `conversations/analyst.jsonl` içindeki turlarda tam metinle durur). Çalışma yine `AwaitingApproval`.
   Döngü onaya kadar sürer; tur sınırı **yoktur** (insan döngüyü kendi bitirir).
5. `AwaitingApproval` dışında `approve`/`revise` → **409 `run.not_awaiting_approval`**.

## Dağıtım: organizatörün kuralı (2026-09-19, kullanıcı kararı)

Kod (`Application/Runs/Dispatcher`), her tetiklemede (onay, bir adımın bitişi) çalışır:

- **Hazır görev:** `dependsOn` içindeki tüm görevler `done`. Sıra: `TaskGraph.Order` (topolojik,
  analist sırası korunur).
- **Boş ajan:** o anda `Started` bir fazı olmayan ajan. **Ajan başına tek iş**; farklı ajanlar
  paralel çalışabilir (developer bir görevi kodlarken testçi başka görevi denetler).
- Hazır görev + hedef adımın ajanı boş → **atama**:
  1. `handoffRole` varsa organizatör **devir notu** üretir (1 LLM turu; girdi: görev, önceki adımın
     çıktısı/özeti, hedef rol). Not `messages.jsonl`'e `kind: Handoff, from: organizer, to: <rol>`
     yazılır ve hedef rolün prompt'una **girdi** olur. `handoffRole: null` ise not yok, atama sessiz.
  2. `phases.jsonl`'e `Started` (adım, ajan, tur) yazılır.
  3. Sahne: `meet { from: organizer, to: <rol>, kind: handoff }`, `board.move { task, stage,
     state: active }`, `agent.state { <rol>, working }`, `run.stage`.
- Hazır görev var, ajanı dolu → görev `queued` bekler; ajan boşalınca sıra ona gelir.
- Hazır görev yok → organizatör `agent.state idle` (sahne: koltuğa oturur ya da kapıdan çıkar; bunu
  UI'ın ambient turu yapar, RunService karar vermez).
- Token: organizatör `haiku`, efor `low` (`organizer.md` frontmatter). Devir notu kısa tutulur.

## Paralellik, tekrar, iptal, bütçe (2026-09-19, kullanıcı kararları)

- **Birden fazla çalışma aynı anda.** İş kanalı bir havuzdur (`AITeam:MaxParallelJobs`, varsayılan 3; 1 =
  eski sıralı davranış). İki kilit: **çalışma başına tek iş** (aynı `run.json`'a iki yazıcı olmaz; kanal kilidi)
  ve **ajan başına tek LLM çağrısı** (`AgentCaller` kilidi: analist A'da konuşurken B'nin analizi bekler).
  Dağıtımda "boş ajan" çalışmalar arası hesaplanır: başka bir **Running** çalışmada `Started` fazı olan ya da
  o an LLM çağrısında olan ajan dolu sayılır; **Paused** çalışmaların ajanları sayılmaz (yürütücüsüz bekleme
  kimseyi kilitlemesin). Hazır görev var ama ajanı doluysa çalışma `Running` kalır, `detail` "ajan bekleniyor" der.
- **Otomatik tekrar (geçici hatalar).** `RetryPolicy` (3 deneme, 3 s · 6 s artan bekleme): runtime kapalı,
  429, 5xx, zaman aşımı, ağ. Şema/politika/4xx gibi kalıcı hatalar hemen düşer. Her deneme `messages.jsonl`'e
  `subject: retry` notu yazar; sahnede ajan "yeniden deneniyor 2/3" der.
- **Elle tekrar.** `POST /runs/{id}/retry` kaldığı adımdan sürer (bkz. yaşam döngüsü ve Gelen kutusu);
  `Retries` sayacı artar, `subject: retry` notu düşer.
- **İptal.** Durum hemen `Cancelled` yazılır; süren işin belirteci kesilir (LLM çağrısı durur), dönen sonuç
  yazılmaz (`WasCancelled` denetimi). `Started` fazlar `Skipped · iptal` ile kapanır; ajanlar boşa döner.
- **Bütçe.** `POST /runs { maxCostUsd }` (boş = sınırsız; ≤0 → 400 `run.budget_invalid`). Her LLM turundan
  sonra toplam maliyet sınırla karşılaştırılır; aşılınca çalışma **`BudgetExceeded`** ile durur, neden
  `detail` ve `subject: error` notunda (CLAUDE.md §4: uyarıyla geçmez). Tekrar ile sürdürülebilir.
- **Görünürlük.** Büyük Kanban panosunda sekmeler: **Sahne** (canlı olay akışı) + her aktif çalışma;
  çalışma sekmesinin sütunları dondurulmuş akıştan, kartları plan + fazlardan türetilir (yenilemede kaybolmaz).
  Ajan panelinde **Özellikler / İşler** sekmeleri: İşler, ajanın çalışma başına her turunu (gönderilen metin,
  çıktı, o anki sağlayıcı/model/efor), notlarını ve fazlarını gösterir (`GET /agents/{key}/work`).
  Not: modelin düşünme (reasoning) metni Agent SDK'dan gelmiyor; yalnız sayısı (`thinking_tokens`) biliniyor.

## Adım yürütücüleri

| `kind` | Durum (2026-09-19) | Ne yapar |
|---|---|---|
| `analyze` | ✅ | Analist, `Spec` şeması, plan onayı döngüsü |
| `handoff` / `handoffRole` | ✅ | Organizatör devir notu (yukarıda) |
| `design` | ⏳ | Yürütücü yok |
| `implement` | ⏳ | Yürütücü yok. Kullanıcı kararı: "normalde direkt işe başlar, kendi ön analizini yapar; şimdilik masada beklesin" |
| `review` | ⏳ | Yürütücü yok |

**Yürütücüsü olmayan adıma atanan görev:** atama yapılır (devir notu, sahne, `Started`), sonra
çalışma **`Paused`** olur; `run.json` `detail`: `"yürütücü yok: <adım>"`. Bu bir hata değil, teslim
sınırıdır; yürütücü gelince kaldığı yerden devam eder.

## Model, efor ve kimlik (2026-09-19, kullanıcı kararı)

- Ajan frontmatter: `provider`, `model`, `effort` (`low | medium | high | max`). Boş → varsayılan.
- **Varsayılanlar:** sağlayıcı `anthropic`, model **`claude-opus-5`** (kullanıcı kararı), efor
  `high` (varsayımla ilerlenir; v1 analist/kontrolcü `high` kullanıyordu). `organizer.md`:
  `claude-haiku-4-5-20251001` + `low` (dağıtım ucuz kalsın).
- **Anthropic kimliği Claude Code oturumudur.** Ayrı API anahtarı yoktur; runtime Agent SDK ile
  makinedeki `claude.exe` oturumunu kullanır. Farklı Windows kullanıcıları farklı oturumdur; bu
  yüzden **UI ilk yüklemede** `GET /api/v1/providers` ile giriş durumuna bakar ve giriş yoksa
  uyarır.
- **Ayarlar ekranı (2026-09-19, kullanıcı isteği):** LLM bağlantıları tek yerden yönetilir: giriş
  durumu, hesap, modeller, **tek tıkla giriş** ve çıkış. Giriş, runtime'ın kullanıcının makinesinde
  `claude auth login` başlatmasıyla olur (yeni konsol penceresi + tarayıcı onayı); şifre/token
  hiçbir katmandan geçmez, CLI kendi OAuth akışını yürütür. UI girişten sonra 3 dakika boyunca
  5 s'de bir `refresh=true` ile yoklar ve tamamlanınca gösterir.
- **Kullanım görünümü:** `GET /api/v1/usage` bizim kayıtlarımızdan (runs/ turları: tur, token,
  sağlayıcının bildirdiği maliyet) sağlayıcı+model bazında toplar.
- **Kalan kullanım (üst bar, 2026-09-19 kullanıcı isteği):** `GET /api/v1/limits` aktif her sağlayıcı
  için kota pencerelerini verir (Anthropic: 5 saatlik oturum, haftalık, modele özel pencereler).
  Kaynak Claude Code'un kendi `/usage` ekranının okuduğu uçtur; CLI bunu komut olarak sunmadığı için
  runtime, CLI'nin makinede sakladığı oturumla aynı ucu sorgular. Belirteç hiçbir yanıta ve günlüğe
  yazılmaz. Uç belgesiz olduğu için şekli değişebilir; değişirse bar "kalan kullanım yok" der, akış
  etkilenmez — bilinçli risk.
- Model listesi (bugün yalnız Anthropic): `claude-fable-5-1`, `claude-opus-5`, `claude-sonnet-5`,
  `claude-haiku-4-5-20251001`. NVIDIA, OpenAI, Ollama sonra eklenir; sözleşme (`provider` enum'u)
  buna hazır, üye **sona** eklenir.
- `reachable` yorumu: runtime katalog için gerçek çağrı yapmaz (her model için bir tur token
  harcar); `reachable = giriş var`. `detail` alanı bunu söyler. LESSONS'taki "katalogda görünmek
  erişilebilir olmak değildir" dersi burada bilinçli olarak gevşetildi — varsayımla ilerlenir.

## Sahne eşlemesi

| Olay | Ne zaman |
|---|---|
| `workflow.set { key }` | çalışma başlarken (dondurulan akış) |
| `agent.state { analyst, working/thinking }` · `run.stage { Analiz }` | analiz turu |
| `agent.state { analyst, done, note: "plan onay bekliyor" }` | `AwaitingApproval` |
| `agent.state { analyst, working, note: "plan revize" }` | revize turu |
| `board.set { tasks[] }` | onay; görevler ilk görev-sütununda `queued` |
| `meet { organizer → rol, handoff }` · `board.move` · `agent.state { rol, working }` | atama |
| `agent.state { organizer, idle }` | bekleyen iş yok |

## Açık kararlar (sonraki turda sorulacak; şimdi kodlanmaz)

1. Red geri dönüşü: birden çok `review` varken `maxReviewRounds` kapı başına mı toplam mı; red
   sonrası aradaki adımlar yeniden koşar mı.
2. `design` adımı görev başına mı çalışma başına mı.
3. `implement`: developer'ın "kendi ön analizi", dosya yazma sınırları, test komutu üretimi.
4. `canAsk` hedefi akışta olmayan bir ajan olabilir mi (bugün: evet, denetlenmez).
5. `stage.officeRole` ile ajanın `office_roles` çakışması denetlenecek mi.
6. `kind: handoff` adımı ile `handoffRole` ikiliği; birinin kaldırılması.
7. Pano: çalışma düzeyindeki analiz için ayrı kart mı (bugün: analiz sütunu boş kalır, görevler
   onaydan sonra açılır).
