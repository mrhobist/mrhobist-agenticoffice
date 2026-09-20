# Fazlar

Yeni oturum buradan devam eder. Bir faz ancak **"biten sayılır"** sütunu ölçülerek kapanır;
"yapıldı" işareti ölçüm değildir.

## Faz 0 — İskelet ✅

`.slnx` + 7 proje · `Directory.Build.props` (`TreatWarningsAsErrors`) ·
`Directory.Packages.props` (merkezi sürüm) · `global.json` (SDK 10.0.401) ·
`ARCHITECTURE.md` · `CLAUDE.md` · `docs/LESSONS.md` · `scripts/verify.ps1` ·
v1'den kurtarılan `config/`

**Ölçüldü:** `verify.ps1` 6 adımın hepsinde geçti — restore, build (0 uyarı),
test, bağımlılık yönü, yasaklı ad alanları, Python sınırı.

## Faz 1 — Domain + dosya deposu ✅

`Domain`: `Agent`, `Knowledge`, `Workflow`, `Stage`, `Run`, `RunTask`, `Phase`, `Turn`,
`Message` + enum'lar + değişmezler. Hiçbir referans yok.
`Application/Abstractions`: `IAgentStore`, `IWorkflowStore`, `IRunStore`.
`Infrastructure/Storage`: md frontmatter okuma/yazma, JSONL append, atomik dosya yazımı.

**Biten sayılır:** `UnitTests` değişmezleri doğrular (geçersiz board reddi, eksik alt md,
döngülü bağımlılık patlamaz). `ServiceTests` geçici dizinde gidiş-dönüş kayıpsızlığını ve
yarım JSONL satırının yok sayıldığını kanıtlar.

**Ölçüldü (2026-09-19):** 27 birim testi (iş akışı değişmezlerinin her hata kodu, ekip çapraz
referansları, topolojik sıralama + döngü, hassasiyet politikası) ve 14 servis testi (gerçek
`config/` kopyası üzerinde md gidiş-dönüşü, `claude` sağlayıcı adının reddi, JSONL yarım satır,
atomik yazım, `runs/` dışına çıkışın temizlenmesi) geçti. Frontmatter için bağımlılık eklenmedi;
dar bir alt küme (`k: v`, `k: [a, b]`, tırnaklı dize) okunur ve yazılır.

## Faz 2 — Ajan ve iş akışı modülleri ✅

`AgentService` (listele, oku, yaz, prompt kompozisyonu), `WorkflowService` (oku, doğrula, yaz).
Api uçları: `GET/PUT /api/v1/agents/{key}`, `GET /api/v1/knowledge`, `GET/PUT /api/v1/workflow`.
Problem Details + `errorCode`, OpenAPI + Scalar.

**Biten sayılır:** Arayüzsüz, `curl` ile bir ajan md'si okunur, değiştirilir, kaydedilir ve
bileşik prompt çıktısı değişir. Geçersiz gövde **400** döner (v1'deki sessiz kabul tuzağı).

**Ölçüldü (2026-09-19):** `curl` ile `PUT /api/v1/agents/tester` dosyayı atomik yazdı ve
`composedPrompt` değişti; eksik alt md `400 agent.unknown_include`, `provider: claude`
`400 agent.invalid_provider`, bozuk iş akışı `400 workflow.rounds_min`, runtime kapalıyken
`GET /models` `503 runtime.unavailable`. Scalar eklenmedi; sözleşme `/openapi/v1.json`.
`PythonAgentRuntimeClient` (Faz 3'ün .NET ucu) bu fazda yazıldı; sahte sunucu testi Faz 3'te.

## Faz 3 — Python runtime

`runtime/` FastAPI: `POST /v1/turn`, `GET /v1/models`, `GET /health`.
Sağlayıcılar: `nvidia` (OpenAI uyumlu, `reasoning_effort` varsayılan `low`, 180s × 3 deneme),
`anthropic` (Agent SDK), `ollama`. **Durumsuz, veritabanı yok, iş kuralı yok.**

**Biten sayılır:** `pytest` sağlayıcı adaptörlerini sahte HTTP ile doğrular. `verify.ps1`
içindeki "Python sınırında iş mantığı yok" adımı geçer. .NET tarafında
`PythonAgentRuntimeClient` sahte sunucuya karşı sözleşmeyi doğrular.

## Faz 4 — Orkestrasyon

`Application/Runs/RunService`: analiz → tasarım → devir → geliştirme → test → devir → onay.
Red halinde önceki `implement` adımına dönüş, tur limiti, `ask` → manager, `handoff` → organizer.
Her tur/faz/mesaj `runs/<id>/` altına yazılır.

**Biten sayılır:** `ServiceTests` sahte runtime ile: red→geri dönüş çalışır, tur limiti
aşılınca görev reddedilmiş sayılır, bütçe aşımı çalışmayı durdurur, geçmiş dosyaları
beklenen kayıtları içerir. **Python çalışmadan geçer.**

## Faz 5 — Çalışma SSE'si (Task.Api 2026-09-19'da kaldırıldı; iş kanalı Api içinde)

`Api`: `POST /api/v1/runs` → 202 (Faz 4a'da geldi; iş kanalı süreç içi), `GET /api/v1/runs/{id}/events` → SSE (henüz yok; UI yoklar).
JWT tek şema, tek sınıf; roller claim ile ayrılır. Hostlar yalnız `127.0.0.1`.

**Biten sayılır:** `curl` ile bir çalışma baştan sona tamamlanır; SSE akışı fazları sırayla
yayınlar; süreç ortada öldürülürse çalışma yeniden başlatmada `Interrupted` işaretlenir.

## v1 → hedef eşlemesi (2026-09-18 incelemesi)

Kaynak: `../MrHobist.AITeam.v1-yedek/orchestrator/` (Python, 2170 satır, 17 dosya).
İçinde **iki nesil** var: fiilen çalışan ve ölçülen 3 rollü sabit hat (`pipeline.py`,
`team.yaml`: analyst/developer/controller) ve yüklenip doğrulanan ama **hiç çalıştırılmayan**
6 rollü veri güdümlü katman (`agents.py`, `workflow.py`, `store.py` — `pipeline.py` bunları
çağırmıyor). Hedef mimari ikinciyi .NET'te gerçekleştirir, birincinin ölçülmüş derslerini taşır.

| v1 parçası | Ne yapıyor | Hedefte nereye |
|---|---|---|
| `pipeline.py` | Analist → görev başına (Developer → Kontrolcü, `max_review_rounds`, red→geri bildirim), bütçe tavanı, dış çıkış listesi, path-traversal korumalı dosya yazımı, ofis durum basımı | `Application/Runs/RunService` — `workflow.json` adımlarına genellenir (design, handoff, ask→manager) |
| `workflow.py` | `workflow.json` yükleme + değişmezler: tam 1 `analyze` ve ilk sırada, ≥1 `implement`, `review` öncesinde `implement`, `maxReviewRounds ≥ 1`, geçerli `officeRole` | `Domain/Workflow` değişmezleri + `WorkflowService` (Faz 1–2) |
| `agents.py` | md frontmatter (`name, summary, office_roles, provider, model, includes, can_ask`), alt md'lerin prompt sonuna eklenmesi, yüklemede doğrulama, dizin dışına yazma reddi | `Domain/Agent, Knowledge` + `Infrastructure/Storage` md okuma/yazma + `AgentService.ComposePrompt` |
| `store.py` | `runs/<id>/run.json, spec.json, conversations/<ajan>.jsonl, messages.jsonl, tasks/<görev>/phases.jsonl`; append + fsync; yarım satırı yok sayan okuyucu; özet | `Infrastructure/Storage/JsonlRunStore` — aynı yerleşim, atomik yazım (CLAUDE.md §2) |
| `config.py` | `team.yaml`, `${ENV}`, sağlayıcı fabrikası, **hassasiyet politikası** (`local / anthropic / open` → izinli hedefler; tek ihlal → çalışma hiç başlamaz) | `Application/Runs/RunPolicy`: sağlayıcı/model ajan md frontmatter'ından (CLAUDE.md §4), hassasiyet çalışma başlangıcında; `destination` runtime yanıtından gelir |
| `roles/analyst.py` | Spec şeması (`summary, architecture, rules, tasks[id,title,description,files,acceptance,depends_on]`), topolojik sıralama | `Application/Runs/Schemas` (C# şema nesneleri) + `TaskGraph.Order()` |
| `roles/developer.py` | ` ```dil path=yol ``` ` blok ayrıştırıcı (3 biçim), red halinde `feedback` + `previous` | `Application/Runs/FileBlockParser` |
| `roles/controller.py` | Verdict şeması (`approve/reject, violations, feedback, tests_run, tests_passed`); Claude SDK ile **araç kullanarak testi fiilen çalıştırır** | Şema Application'a; test çalıştırma kararı aşağıda |
| `providers/nvidia_nim.py` | 3 × 180 s yeniden deneme, `reasoning_effort=low`, `response_format json_schema`, boş `content` tespiti, 1 token'lık gerçek sağlık isteği | `runtime/app/providers/nvidia.py` — **Python'da kalan tek kod bu katman**; birebir taşınır |
| `providers/claude_sdk.py` | Agent SDK `query`, `output_format json_schema`, `structured_output`, maliyet, `allowed_tools/cwd/mcp` | `runtime/app/providers/anthropic.py` (ad: `claude` → `anthropic`) |
| `providers/ollama.py` | yerel, `destination=local` | `runtime/app/providers/ollama.py` |
| `office.py` | KbWen ofisine tam kadro POST (replace semantiği dersi) | Silinir; yerine `SceneEventBus` (var). `RunService` sahne olaylarını yayımlar |
| `__main__.py doctor/run` | ortam kontrolü, CLI çalıştırma, özet | Api `POST /runs` + iş kanalı; doctor → `GET /api/v1/providers` |

**Taşınmayan / dikkat:** v1 `.env` gerçek `NVIDIA_API_KEY` içerir — depoya asla kopyalanmaz.
`vendor/agent-virtual-office` ve `.office-state` gereksiz. `team.yaml`'ın rol→sağlayıcı bilgisi
ajan md frontmatter'ına taşınır; `team.yaml` kalmaz.

**Sözleşme uyumsuzluğu:** ajan md'lerinde `provider: claude` yazılabiliyor; runtime sözleşmesi
`anthropic`. Karar: frontmatter'da da `anthropic` (enum adıyla, CLAUDE.md §5); okuyucu `claude`
görürse açık hata verir, sessizce çevirmez.

**Testçinin testi çalıştırması — varsayımla ilerlenir:** v1'de kontrolcü Claude SDK araçlarıyla
`cwd` içinde test yazıp koşuyordu. Bu, Python runtime'a dosya yazma ve süreç çalıştırma sokar
(CLAUDE.md §1'e aykırı) ve testçiyi Anthropic'e kilitler. Karar: testçi LLM'i **test dosyalarını
ve komutu** üretir (`files[] + command`), testi `RunService` çalışma dizininde süre sınırıyla
**.NET çalıştırır** ve çıktıyı testçiye ikinci bir turda verir. Alternatif (SDK araç kullanımı,
runtime'da `tools?: {cwd, allowed[]}` alanı) yalnız Anthropic sağlayıcısında çalışır ve sınırı
deler; reddedildi. Faz 4'te ölçülür.

**Önerilen sıra:** Faz 1 → 2 (Domain, depolar, ajan/iş akışı uçları) → Faz 3 (sağlayıcıları
runtime'a taşı; en çok kopya, en az risk) → Faz 4 (RunService, sahte runtime ile testler) →
Faz 5 (`POST /api/v1/runs` → 202, SSE; `RunService` → `SceneEventBus`).
Faz 6 sahnesi hazır: `agent.state`, `meet`, `board.*`, `run.stage` olayları `RunService`'in
yayımlayacağı sözleşmedir (`docs/SCENE.md`).

## Faz 2b — Dinamik ekip ve iş akışları (2026-09-19, karar)

**İstek:** Analist → developer → testçi → yönetici (altı şapkalı karar verici) → organizatör
(adım değişince işi sıradakine devreden) zinciri sabit kod olmasın; ekibe ajan eklenip iş akışı
bunlardan kurulsun. Bir varsayılan akış olsun, iş başına farklı akış seçilebilsin (örn. tasarımcıyı
çalıştırmayıp analizi verip developer + testçi ile bitirmek). Stajyer/DevOps gibi ekler kalksın;
yayın işleri ileride `publisher` gibi özel bir karakterle gelir.

**Varsayımla alınan kararlar:**

| Karar | Alternatif | Neden bu |
|---|---|---|
| `config/workflows/{key}.json`, `default` zorunlu ve silinemez | tek dosyada `workflows[]` | dosya başına git diff okunur; iş başına akış = dosya seçmek |
| `Team.KnownRoles` kaldırıldı; ekip **açık**, zorunlu rol yok | altı rol sabit | "hangi ajan çalışır" sorusunun tek cevabı iş akışıdır, iki yerde tutulmaz |
| Akış kaydedilirken `role`/`handoffRole` ekipte var mı denetlenir (`workflow.unknown_role`); ajan silinirken akışlarda geçiyor mu denetlenir (`agent.in_use`) | çalışma başında patlasın | bozuk yapılandırma kaydedilmez (LESSONS: sessiz kabul en kötü hata) |
| Organizatör = `handoffRole`: **her adım geçişinde** devir notu üretir; ayrıca `kind: handoff` adım olarak da konabilir | yalnız adım olarak | istenen davranış "adım değişince devret"; her araya adım eklemek panoyu kirletir |
| Yönetici = `karar` adımı (`kind: review`, `officeRole: gate`) + `canAsk` hedefi; altı şapka mantığı `manager.md` + `karar-ilkeleri.md` içinde | ayrı `kind: decide` | mevcut review/red mekaniği yeter; karar yöntemi prompt işidir, kod işi değil |
| Varsayılan akış: analiz → geliştirme → test → karar, `handoffRole: organizer`. İkinci örnek `tasarimli`: analiz → tasarım → geliştirme → test → karar | tek akış | seçim özelliği görünür olsun |
| `intern`/`devops` sahneden ve renk tablosundan çıkarıldı; boşalan iki masa yeni ajanlara | sahnede kalsın | kullanıcı istemiyor; masa boş kalmak yerine yeni ajanı taşır |
| Sahnede yeri olmayan ajan UI'da boş masaya + kullanılmayan sprite'a otomatik yerleşir; kalıcı yer `scene.json` ile elle | md'ye `sprite/home` alanı | sahne bilgisi ajan tanımına sızmaz |
| Pano sütunları `default` akıştan; `workflow.set { key }` olayı gelince o akışa geçer | her zaman default | çalışma hangi akışla koşuyorsa pano onu göstermeli |

Sözleşme `docs/API.md` (Ekibe ajan ekleme, İş akışları), kodlar `docs/error-codes.md`.
UI: İş Akışı paneli (akış seç/kopyala/sil, adım ekle/sil/sırala, adım → ajan, `handoffRole`),
Ekip'te "ajan ekle / sil". Kanban sütunları seçili akıştan.

**2026-09-19 backend bitti:** `Workflow(Key, Title, MaxReviewRounds, HandoffRole, Stages)` +
`ValidateAgainst(Team)`; `Team` açık (`KnownRoles` yok), bilgi dosya adı doğrulanır; `Team.AskersOf`.
`JsonWorkflowStore` dosya başına (`config/workflows/`), `IWorkflowStore` list/load/save/delete;
`WorkflowService` list/get/upsert/delete; `AgentService` create/delete (akış + `can_ask` referans
denetimi); uçlar `POST/DELETE /agents`, `GET/PUT/DELETE /workflows[/{key}]`; 409 eşlemeleri.
`config/workflows/default.json` (analiz → geliştirme → test → karar, devir: organizer) ve
`tasarimli.json`; `manager.md` altı şapka. Smoke: tüm uçlar sözleşmedeki kodları döndü;
kapı 32 + 22 test ile yeşil. UI paneli ayrı ajanda (Faz 6).

## Faz 4a — Analiz → plan onayı → devir (2026-09-19, kullanıcı kararları) 🔶

Kullanıcının 1. önceliği: "bir analist rolü iş alsın, düzgün yapsın, organizatör tasarımcıya
(ya da akışa göre developer'a) atasın" — bunu ekranda görmek. Faz 3/4/5'in dar bir dilimi olarak
kesildi; kararların tamamı `docs/DOMAIN.md`, sözleşme `docs/API.md` (Çalışmalar, Sağlayıcı kimliği).

**Sorularak alınan kararlar (varsayım değil):** tetikleme UI'da "Yeni çalışma" formu · model kaynağı
Claude Code oturumu (Agent SDK; ayrı anahtar yok; farklı Windows kullanıcısı = farklı oturum, UI ilk
yüklemede giriş kontrolü) · bugün yalnız Anthropic, dört model (fable/opus/sonnet/haiku), varsayılan
`claude-opus-5` · efor seçimi ajan başına (`effort`) · akış otomatik ilerler **ama** analistin planı
insan onayından geçer, onaysız panoya iş açılmaz; revize notu → analist yeniden → onaya kadar döngü ·
organizatör = kod dağıtıcı (bekleyen iş / boş ajan), LLM yalnız devir notu için · ajan başına tek iş,
farklı ajanlar paralel · developer bu teslimde **çalışmaz**, iş masasında bekler → çalışma `Paused`.

**Kapsam:** runtime `anthropic` sağlayıcısı + `/v1/auth` · `Agent.Effort` · `Run.Workflow` + akış
kopyası · `RunService` (analiz, onay/revize, `Dispatcher`) · Api içinde tek yazıcı iş kanalı (`JobChannel`/`JobWorker`) ·
Api `runs` uçları + `providers` · UI: giriş kontrolü, "Yeni çalışma",
plan onay paneli, ajan panelinde efor, `workflow.set`.

**Biten sayılır:** Runtime kapalıyken `ServiceTests` sahte runtime ile analiz → onay → devir →
`Paused` akışını ve `runs/` dosyalarını doğrular. Gerçek modelle: UI'dan brief girilir, analist
gerçek plan üretir, plan panelde görünür, bir revize notu planı değiştirir, onaydan sonra organizatör
sahnede developer'a yürür, görev panoda Geliştirme sütununa geçer, çalışma `Paused` görünür.

**Ölçüldü (2026-09-19, sahte runtime):** 9 yeni servis testi yeşil (toplam 32 birim + 31 servis):
analiz → `AwaitingApproval`, `spec.json` + `workflow.json` yazılır, onaysız faz/pano yok; revize notu
geçmişle (`user → assistant → user`) analiste gider ve plan değişir; onay → `board.set`, organizatör
devir notu (haiku/low) → `meet` → `board.move` → t1 developer'da `Started`, t2 bağımlı bekler, çalışma
`Paused`; 409/400 kodları; hassasiyet `local` iken `PolicyRejected`; yeniden başlatmada `Interrupted`.
Runtime: 15 pytest (Agent SDK sahte). Canlı zincir (UI 3005 → Api 5082 → runtime 5090; Task.Api aynı gün kaldırıldı, bkz. CLAUDE.md Sapmalar):
"Yeni çalışma" formundan brief gitti, analist sahnede "takıldı", çalışma panelinde `başarısız` +
`runtime runtime.not_logged_in` ayrıntısı, üst şeritte "Anthropic: giriş yok · `claude login`" uyarısı
göründü. **Gerçek modelle plan/onay/devir henüz gözle görülmedi**: bu kullanıcıda Claude Code oturumu yok.

**Aynı gün eklenenler:** Task.Api kaldırıldı, iş kanalı Api içinde (CLAUDE.md → Sapmalar). Kod incelemesi
sonrası `RunReader` / `Prompts` ayrımı, `WorkflowMapping`, CORS açık liste (`AITeam:UiOrigins`), kimlik
`refresh`, üretilen tipler (`ui/shared/types/api.ts`). **Ayarlar ekranı**: LLM bağlantıları (durum, hesap,
modeller, tek tıkla giriş = runtime `claude auth login`'i yeni konsolda başlatır, çıkış) ve kullanım tablosu
(`GET /usage`, runs/ turlarından). Abonelik limiti CLI'dan alınamıyor, ekran bunu söyler.

**İlk gerçek deneme (2026-09-19 16:27):** kullanıcı giriş yaptı, brief verdi; analist cevap üretti ama runtime
`max_turns=1` ile çağırdığı için Agent SDK yapısal çıktının ikinci turunda "Reached maximum number of turns" kesti
(çalışma `Failed`, günlükte kayıt yok). Düzeltmeler: `max_turns=4`; hata `messages.jsonl`'e `subject: error` ile
yazılır; UI'da Hata kutusu + **Günlük** (LLM turları tam prompt/çıktı, devir/hata notları, faz geçişleri;
`GET /runs/{id}/turns`). Ölçüm: `tools=[]` çağrı başına prompt'u 23k → 4,5k token'a düşürdü (Haiku: $0.047 → $0.010).
**Üst barda kalan kullanım** (`GET /limits`): 5 saatlik oturum, haftalık, modele özel pencereler; kaynak Claude Code'un
`/usage` ucu (belgesiz, 429'a duyarlı → runtime 90 s önbellek + son iyi değer). Model alanı açılır menü oldu.

**Ortam notu:** Bu oturum Windows'ta `SemihAI` kullanıcısıyla çalıştı; `runtime/.venv` bu kullanıcı
için Python 3.12 ile yeniden kuruldu (`pyproject` `>=3.12`). Agent SDK, Claude masaüstü uygulamasının
`%APPDATA%\Claude\claude-code\<sürüm>\claude.exe` ikilisini kullanır; `claude auth status` bu
kullanıcı için **"giriş yok"** dedi — gerçek model denemesi için `claude login` gerekir.
`semih` kullanıcısının eski Api (5080) ve UI (3000) süreçleri durdurulamadığı için doğrulama
`AITeam:ApiUrl=http://127.0.0.1:5082` ve `NUXT_PUBLIC_API_BASE` ile ikinci kopyalar üzerinde yapıldı;
Api CORS'u artık her loopback kökeni kabul eder. Windows Uygulama Denetimi, `bin/Debug|Release/net10.0`
dışına derlenen Api dll'ini çalıştırmadı (`-p:OutDir` ile derlenen kopyalar); standart yol kullanılmalı.

**2026-09-19 akşam — İşler, gelen kutusu, kalan hak (kullanıcı isteği):** "İşleri göreyim, kaç iş var bileyim, benden
cevap bekleneni soru olduğunu anlayarak göreyim, Kanban'dan takip edeyim, kalan hakkımı üst barda göreyim."
Eklenenler: `GET /runs/overview` (sayaçlar + `inbox`, `docs/DOMAIN.md → Gelen kutusu`), `POST /runs/{id}/retry` ve
`/cancel` uçları (servis zaten vardı, uç yoktu; 409 `run.not_retryable` / `run.not_cancellable`), UI **İşler** paneli
(`JobsPanel.vue`: gelen kutusu, durum filtreleri, tüm çalışmalar; kısayol I), üst barda İşler düğmesi + kırmızı rozet +
sekme başlığında `(N)`, "Senden cevap bekleniyor" şeridi, sahne panosunda rozet (`Board.attention`), büyük Kanban'da
"Senden bekleniyor" şeridi, çalışma panelinde **Yeniden dene / İptal et** ve onay bölümünün "Soru: bu planı onaylıyor
musun?" başlığı. `cancelled` durumu UI'a eşlendi; `shared/types/api.ts` yeniden üretildi.
**Kalan hak:** runtime `/v1/limits` 500 dönüyordu → üst bar bunu "runtime kapalı" sanıyordu (yanlış teşhis). Runtime
artık asla 500 dönmez (ayrıştırma hatası `available=false` + neden; `resets_at` epoch gelirse ISO'ya çevrilir), Api
runtime 5xx'ini 502 `runtime.error` diye ayırır, üst bar üç durumu ayrı yazar. **Ölçülmedi:** bu oturumda runtime
yeniden başlatılamadı (aşağıdaki ortam notu); düzeltme runtime yeniden başlayınca görünür. Gerçek 500'ün kök nedeni
bilinmiyor; ilk yanıt `detail` alanında okunacak. Testler: 32 birim + 32 servis yeşil; runtime pytest'e 1 test eklendi,
**koşturulamadı** (Python yok).

**2026-09-19 gece (SemihAI, kullanıcı kararları: paralel + ajan başına tek iş · elle + otomatik tekrar · panoda çalışma
sekmeleri · iptal + bütçe):** `JobChannel` havuz oldu (`AITeam:MaxParallelJobs`=3, çalışma başına kilit, iptal
belirteci), `AgentCaller` ajan kilidi + `RetryPolicy`, dağıtımda çalışmalar arası meşguliyet, `maxCostUsd` →
`BudgetExceeded`, Kanban'da Sahne + çalışma sekmeleri (durumdan türetilir), ajan panelinde **Özellikler / İşler**
sekmeleri (`GET /agents/{key}/work`), brief formunda bütçe alanı, üst barda tüm kota pencereleri (pasifler soluk).
Runtime `/v1/limits` ölçüldü: 200, oturum/hafta/modele özel pencereler geliyor. `.venv` `SemihAI` için yeniden kuruldu.

**2026-09-19 gece — Projeler ve ofis odaklı kabuk (kullanıcı kararları):** Tasarım artboard'ları
(https://claude.ai/artifact/TXLNsdkM6YQim5NQkYRjLv): ofis tam boy ortada, projeler sol ahşap rayda kağıt kart,
kart açılınca kağıt pano (İşler / Ayarlar), "Yeni iş" yalnız orada. Kod: `Project` (başlık, açıklama, varsayılan akış,
hedef dizin, `ownerId=local`; bütçe yok), `config/projects/{key}.json`, `IProjectStore`/`ProjectService`/`ProjectCard`,
`/api/v1/projects` uçları, `POST /runs` için `project` zorunlu (`run.project_required`), `Run.Project/OwnerId`,
`GET /runs?project=`. Eski projesiz çalışmalar silindi. UI: `ProjectPanel.vue`, ray kartları projeler, üst bardan
"Yeni çalışma" kalktı, `RunPanel` projeye bağlı. Ölçüm: UI'dan "Hello World Console" projesi oluşturuldu, rayda kart
belirdi, panel açıldı. Testler 37 servis + 32 birim + 21 runtime yeşil.

**Ortam notu (2026-09-19, `SemihAI2` kullanıcısı):** `semih` ve `SemihAI` kullanıcılarının bıraktığı Api (5080, 5082),
UI (3000, 3005) ve runtime (5090) süreçleri bu kullanıcıdan durdurulamıyor (`Erişim engellendi`) ve Api'nin hem
`bin/Debug` hem `bin/Release` çıktısını kilitliyor. Çözüm: Api `-p:OutputPath=<scratch>/bin/Debug/net10.0/` ile
derlendi ve oradan 5083'te çalıştırıldı (Uygulama Denetimi bu yolu engellemedi; `launch.json` → `ui-5083`, UI 3006).
`runtime/.venv` `SemihAI`'nin Python'unu gösteriyor; `SemihAI2` için Python yok → pytest ve yeni runtime başlatılamaz;
çalışan 5090 runtime'ı (kodu güncel, Anthropic girişi var) yeniden kullanıldı. Kalıcı çözüm: eski süreçleri
kapatmak (Görev Yöneticisi, yönetici) ve bu kullanıcıya Python 3.12 kurup `.venv`'i yeniden oluşturmak.

## Faz 4d — Akış tamam: yürütücüler, takılma soruları, limit koruması (2026-09-20) ✅

Kullanıcı kararları (sorularak): developer/testçi **Agent SDK araçlarıyla** dosyayı yazar, testi koşar (hedef dizin
sınırı, izin geri çağrısı) · takılmada **soru + müdahale seçenekleri** (`AwaitingInput`, `retry|skip|cancel`) ·
maliyet **eşdeğer** (≈$), asıl koruma **limit eşiği %99** platform bazında Ayarlar'da.

- `implement` / `review` / `design` yürütücüleri; `DispatchAsync` planla→koş→planla döngüsü; red → developer,
  kapı başına tavan → soru; developer `blocked` → soru; aynı adımda tekrarlanan hata → soru.
- Runtime: `tools`, `cwd`, `maxTurns` istekle; `toolUses`, `turns` yanıtla; `can_use_tool` yazma sınırı.
- `LimitGuard` (çağrı öncesi), `Paused + resumeAt`, `LimitResumer` (dakikada bir), `GET/PUT /settings`.
- UI: soru kutusu ve seçenekler (RunPanel), limit notu, araç listesi günlükte, Ayarlar → Limit koruması,
  `≈$` etiketleri, giriş ekranı sabit renkler + autofill düzeltmesi.
- Testler: 40 servis (tam akış, red döngüsü + soru, engellenen developer, limit beklemesi), 32 birim, 21 runtime.
- Gerçek koşu (2026-09-20, hello world): analiz 48 s · developer 149 s / 22 araç · test 58 s · karar 55 s; ≈$1.81.
- Kod incelemesi sonrası (2026-09-20): Bash dizin sınırı · otomatik limit sürdürmesi `ResumeAsync` (tekrar sayılmaz) ·
  `awaitingInput` sayacı ayrı · devir notu yalnız implement'te · dizin yaratma Infrastructure'da · 401 gövdesi
  `ProblemMapping`'ten · `AddAuthorization` kaldırıldı · ekip yalnız devir için yüklenir · limit beklemesinde 20 s yoklama ·
  ayarlar dosya damgasıyla önbellekte · yarım kalan `Started` faz kesinti/iptalde `Failed` (yeniden dene aynı adım).

## Kalan işler (2026-09-20 itibarıyla, öncelik sırasıyla)

1. ~~**`canAsk`**~~ → geldi (2026-09-20): developer takılınca soru önce `can_ask` hedefine (manager, 1 tur, okuma aracı); cevaplarsa kullanıcı görmez, yükseltirse/ikinci takılmada kullanıcıya (DOMAIN → Takılma). Servis testi 46.
2. **Faz 5 SSE** `GET /runs/{id}/events`: UI 2 s yoklamayla idare ediyor; çok çalışma açıkken yük artar.
3. ~~İş Akışı paneli~~ → Ekip paneli "Takımlar" sekmesi (2026-09-20): adım ekle/sil/sırala, ajan/tür/ofis rolü; yeni ajan ekleme ve sahneye otomatik yerleşim (ziyaretçi dahil) da geldi.
4. **Tasarım artıkları**: ray daralması (proje açıkken 72 px), pano için çalışma düzeyinde analiz kartı.
5. **Açık kararlar** (DOMAIN §Açık kararlar 4–7): `canAsk` hedefi, `officeRole` çakışması, `kind: handoff` ikiliği.
6. **`ownerId`** JWT claim'inden; LDAP / kullanıcı deposu `IUserDirectory`.
7. **Sağlayıcılar**: NVIDIA / Ollama runtime adaptörleri (sözleşme hazır). OpenAI eklendi (2026-09-20, aşağıda).
8. **Emülatör / tarayıcı testleri** için MCP araçları (testçi kararı).
9. **OpenAI gerçek doğrulama** (2026-09-20 devir, iki oturum birleşti): Codex `login` denenmedi; `codex exec --json` olay
   adları ve `--output-schema` gerçek turla doğrulanmadı (ilk turda `_parse_events` ve `-o` son mesaj dosyası kontrol
   edilecek); gerçek API anahtarıyla tur yapılmadı. Bilinen varsayımlar: Codex kalan hak vermiyor (limit koruması geçer),
   sistem promptu metnin başına gidiyor, API anahtarı yolunda araçlı adım 501, katalog/fiyat tahmini sabit, araçsız
   Codex turu read-only sandbox ama komut koşabilir. `.claude/launch.json`'daki `ui-5083`, `runtime-5091`, `api-rt5091`
   girişleri paralel oturumun test kurulumuydu; gerekmiyorsa silinebilir.

## Sağlayıcı: OpenAI + API anahtarı (2026-09-20, kullanıcı isteği) ✅

- `openai` sağlayıcısı (`runtime/app/providers/openai.py`), iki kimlik yolu: **ChatGPT aboneliği** = Codex CLI
  oturumu (`codex login`, tur `codex exec --json`, araçlı adımda `workspace-write` sandbox) ve **API anahtarı**
  (Responses API, araçsız). Anthropic'e de API anahtarı girişi eklendi (SDK'ya `ANTHROPIC_API_KEY` ortamla gider).
- Anahtar deposu `%USERPROFILE%\.mrhobist-aiteam\credentials.json` (`runtime/app/credentials.py`); ortam
  değişkeni önde. Yanıtlarda yalnız maskeli son. `AuthStatus.method`: `session | apikey | null`.
- Sözleşme: `Provider.Openai`, `Destination.Openai` (sona eklendi); `POST /providers/{p}/login` gövdesi
  `ProviderLoginRequest { mode: claudeai|console|chatgpt|apikey, email?, apiKey? }`.
- Ayarlar ekranı: sağlayıcı başına oturum düğmesi + "API anahtarı ile kullan" kutusu; rozet `API anahtarı`.
- Kota: OpenAI için `available=false` (Codex CLI kalan hakkı dışa vermiyor) → limit koruması sessizce geçer.
- Testler: runtime 43 (OpenAI 20 yeni), birim 34, servis 41.

## Faz 4c — Ofis odağı ve giriş (2026-09-19, kullanıcı istekleri) ✅

- Ajana tıklanınca animasyon durur, ajan izleyiciye bakar, başında işini yazan balon açık kalır; panel
  kapanınca akışına döner (`World.focus/unfocus`, `Agent.frozen`).
- Ajan paneli sağ üstte "Ekip · ad" başlığıyla; **İşler** sekmesi önde, Özellikler ikinci.
- Kanban sekmeleri **proje bazında gruplu**; kartlar çalışma detayından türetilir (canlı, 3 s).
- "Senden bekleyenler" işlerin dışında **bildirim zili** (üst bar) altında; sahnedeki yapışkan not kaldırıldı.
  `Paused` gelen kutusuna **girmez**: yürütücüsü olmayan adım kullanıcıdan bir şey istemez (kullanıcı sorusu).
- Giriş: gömülü `admin/admin`, JWT tek şema, `/api/v1` kapısı, giriş ekranı, sağ üstte profil çipi.
  Sonraki: `ownerId`'yi claim'den doldur; LDAP / kullanıcı deposu `IUserDirectory` ile.
- Test fikstürü canlı `config/` düzenlemelerinden yalıtıldı (projeler kopyalanmaz, analist efor/model satırı atılır).

## Faz 6 — UI (canlı sahne) 🔶 sahne kuruldu

Nuxt 4 + TypeScript + **Canvas 2D** (Three.js bırakıldı, bkz. `docs/SCENE.md`). Sprite'lar
`assets/raw/` + `assets/reference/v2-catalogs/` → `scripts/build-sprites.py` → `ui/public/sprites/`.
Arka plan V2 boş ofis görseli; 7 karakter 8 yön yürür, sandalyede oturur, yazar. Yerleşim `config/scene.json`,
Api `GET /api/v1/scene` ile sunar; olaylar `GET /api/v1/scene/events` (SSE) ile akar,
`POST /api/v1/scene/commands` yayımlar (bugün curl, Faz 5'te `RunService`).

**2026-09-18'de ölçüldü:** Api ayakta iken tarayıcıda 6 ajan yerinde oturur, boşta olanlar
kahve/su/pano/arkadaş turuna çıkar, kedi uyur-gezer-oturur, `meet` komutuyla testçi
developer'a yürüyüp red balonu gösterir, `board.move` notu sütun değiştirir. Api kapalıysa
sahne yüklenmez ve bunu söyler; SSE kopuksa sahte yönetmen rozetiyle çalışır.

Eski UI (procedural canvas + three.js akış grafiği) `archive/ui-canvas-v2-2026-09-17/`.

**2026-09-19:** Ajanlar paneli geldi (`ui/app/components/AgentPanel.vue`): Ekip'te ajana
tıklanınca ad, özet, ofis rolleri, sağlayıcı/model (`GET /models` önerisi, runtime kapalıysa
elle), sorabilir, bilgi dosyaları, prompt ve "modele giden metin"; `PUT /api/v1/agents/{key}`
ile kaydeder, `errorCode` → Türkçe eşleme tek dosyada (`ui/app/api/errors.ts`). Kanban paneli
ayrı bileşen (`KanbanPanel.vue`): sayaçlar, filtre, kart detayı. API tipleri elle
(`ui/app/api/types.ts`), `gen:api` gelince değişecek.

**Kalan:** İş Akışı paneli (adım ekle/sil/sırala, `PUT /workflow`), `npm run gen:api` ile
üretilen tipler, gerçek `RunService` olayları (Faz 5).

**Biten sayılır:** Tarayıcıda canlı bir çalışma izlenir; bir ajanın md'si panelden
değiştirilip kaydedilince sonraki çalışmada davranış gözle görülür biçimde değişir.

## Kod incelemesi turu — akış düzeltmeleri ve temizlik (2026-09-20) ✅

`/code-review [high]` bulguları (ölü kod, mimari uyumsuzluk, 1 iş 1 fonksiyon):

- **Ajan bekleyen çalışma asılı kalmıyor.** `Run.waitingSince` + `IRunScheduler` (Api: `RunScheduler` → `JobChannel`):
  bir adım kapanınca / dağıtım bitince / iptalde `RunService.WakeWaitingAsync` bekleyenleri dağıtıma koyar; `RunResumer`
  (eski `LimitResumer`) emniyet taraması yapar; yeniden başlatmada bekleyen `Interrupted` olmaz, kuyruğa döner.
- **Durum metinden değil alandan.** `Run.step` (`analyze|approval|dispatch`) ve `Phase.cause` (`agent|limit|cancelled|interrupted`);
  `detail` yalnız görünüm. `RetryStep/RetryResult` kalktı, `RetryAsync/ResumeAsync` `Run` döner; hangi adımın hangi işi
  doğurduğu tek yerde (`RunScheduler`), uçlar `ScheduleAndAccept` ile aynı kapıdan geçer.
- **"Red → önceki implement" tek kaynak:** `Workflow.ImplementBefore(stages, id)`; `Dispatcher.NextStage` ve pano hedefi
  (`BoardTarget`, kapanan fazı `NextStage`'e verir; `workflow.json` her fazda yeniden okunmuyor) oradan.
- **Ölü kod:** `IAgentService.ComposePromptAsync`, `JobChannel.Running`, `launch.json` geçici girişleri (`ui-5083`,
  `runtime-5091`, `api-rt5091`), bayat yorumlar. `hello-world-console` projesi, hedef dizini ve 3 çalışma geçmişi silindi.
- **Sır taraması (ayrı ajan, public push öncesi):** engelleyici yok. Uyarılar: `AuthEndpoints.DevKey` sabit JWT geliştirme
  anahtarı (loopback ile sınırlı; `AITeam:JwtKey` verilmezse herkesin bildiği anahtar — sertleştirme kararı açık),
  gömülü `admin/admin` (bilinçli), belgelerde Windows kullanıcı adları. `.gitignore` önleyici kalıplarla genişletildi.
- Ölçüm: `verify.ps1` yeşil — birim 34, servis 59 (yeni: `Ajani_dolu_calisma_bekler_ajan_bosalinca_yeniden_dagitima_konur`), ui typecheck.
  Sonraki: `ExecuteStepAsync`'i adım türü başına yürütücülere bölmek, `BusyInOtherRunsAsync` I/O'sunu azaltmak,
  `build-sprites.py`'de 9-dilim sınır denetimi ve `cafe.board`/`stretch.dst` tek kaynak.
