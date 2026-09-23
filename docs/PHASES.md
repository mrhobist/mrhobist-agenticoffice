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

**Ortam notu:** Bu oturum ikinci bir Windows kullanıcısıyla çalıştı; `runtime/.venv` o kullanıcı
için Python 3.12 ile yeniden kuruldu (`pyproject` `>=3.12`). Agent SDK, Claude masaüstü uygulamasının
`%APPDATA%\Claude\claude-code\<sürüm>\claude.exe` ikilisini kullanır; `claude auth status` bu
kullanıcı için **"giriş yok"** dedi — gerçek model denemesi için `claude login` gerekir.
Birinci kullanıcının eski Api (5080) ve UI (3000) süreçleri durdurulamadığı için doğrulama
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

**2026-09-19 gece (ikinci Windows kullanıcısı, kullanıcı kararları: paralel + ajan başına tek iş · elle + otomatik tekrar · panoda çalışma
sekmeleri · iptal + bütçe):** `JobChannel` havuz oldu (`AITeam:MaxParallelJobs`=3, çalışma başına kilit, iptal
belirteci), `AgentCaller` ajan kilidi + `RetryPolicy`, dağıtımda çalışmalar arası meşguliyet, `maxCostUsd` →
`BudgetExceeded`, Kanban'da Sahne + çalışma sekmeleri (durumdan türetilir), ajan panelinde **Özellikler / İşler**
sekmeleri (`GET /agents/{key}/work`), brief formunda bütçe alanı, üst barda tüm kota pencereleri (pasifler soluk).
Runtime `/v1/limits` ölçüldü: 200, oturum/hafta/modele özel pencereler geliyor. `.venv` o kullanıcı için yeniden kuruldu.

**2026-09-19 gece — Projeler ve ofis odaklı kabuk (kullanıcı kararları):** Tasarım artboard'ları
(https://claude.ai/artifact/TXLNsdkM6YQim5NQkYRjLv): ofis tam boy ortada, projeler sol ahşap rayda kağıt kart,
kart açılınca kağıt pano (İşler / Ayarlar), "Yeni iş" yalnız orada. Kod: `Project` (başlık, açıklama, varsayılan akış,
hedef dizin, `ownerId=local`; bütçe yok), `config/projects/{key}.json`, `IProjectStore`/`ProjectService`/`ProjectCard`,
`/api/v1/projects` uçları, `POST /runs` için `project` zorunlu (`run.project_required`), `Run.Project/OwnerId`,
`GET /runs?project=`. Eski projesiz çalışmalar silindi. UI: `ProjectPanel.vue`, ray kartları projeler, üst bardan
"Yeni çalışma" kalktı, `RunPanel` projeye bağlı. Ölçüm: UI'dan "Hello World Console" projesi oluşturuldu, rayda kart
belirdi, panel açıldı. Testler 37 servis + 32 birim + 21 runtime yeşil.

**Ortam notu (2026-09-19, üçüncü Windows kullanıcısı):** önceki iki kullanıcının bıraktığı Api (5080, 5082),
UI (3000, 3005) ve runtime (5090) süreçleri bu kullanıcıdan durdurulamıyor (`Erişim engellendi`) ve Api'nin hem
`bin/Debug` hem `bin/Release` çıktısını kilitliyor. Çözüm: Api `-p:OutputPath=<scratch>/bin/Debug/net10.0/` ile
derlendi ve oradan 5083'te çalıştırıldı (Uygulama Denetimi bu yolu engellemedi; `launch.json` → `ui-5083`, UI 3006).
`runtime/.venv` ikinci kullanıcının Python'unu gösteriyor; üçüncü kullanıcıda Python yok → pytest ve yeni runtime başlatılamaz;
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

## Canlı araç akışı, UX ve sahne turları (2026-09-20) ✅

Faz 4d ile kod incelemesi turu arasında kalan, o ana kadar buraya yazılmamış işler. Sözleşme
değişiklikleri `docs/API.md`, `docs/DOMAIN.md` ve `docs/SCENE.md`'ye aynı commit'lerde işlendi.

**Canlı araç akışı.** Runtime her araç çağrısında `progressUrl`'e `{tool, target}` POST eder
(2 s zaman aşımı, hata yutulur — bildirim turu bloklamaz); Api `ProgressRegistry` ile tek
kullanımlık belirteci çözer ve `agent.tool` yayımlar. Sahnede balon, çalışma panelinde
**"Şu an"** şeridi. `Bash` hedefinde baştaki `cd "<kök>" &&` ön ekleri atılır ve satır sonları
tek boşluğa iner ki asıl komut görünsün (`ProgressRegistry.Shorten`, birim testi var).

**Bitmiş çalışma kutusu (UX).** Çalışma bitince üretilen dosyalar, **Projeyi başlat** düğmesi ve
"devam brief'i" tek kutuda; adım süresi sayacı, yeni bekleyen işte toast, boş HUD gizlenir,
kısayol yardımı.

**Proje silme ve hedef dizin.** `DELETE /projects/{key}?deleteFiles=` — süren çalışma varsa
409 `project.in_use`; bitmiş geçmiş projeyle birlikte gider, dosyalar **ayrıca** işaretlenir.
Onay UI'da iki adımlı. Hedef dizin artık klasör seçiciyle geliyor (`GET /projects/dirs`,
`DirPicker.vue`); serbest metin yalnız ad ve açıklamada.

**Mini pano (sahnedeki Kanban).** Canlı kartlar; pano yalnız SSE'den değil sunucudan da
eşitlenir, böylece bitmiş işler "Bitti" şeridinde görünür. Kanban hedefi türetimi
`ui/app/api/board.ts`'e alındı — `Workflow.ImplementBefore` kuralının TS kopyası, `KanbanPanel`
ile sahne panosu aynı kaynağı kullanır.

**Sahne.** Su sebilinden su taşıma, kanepe abajuru; menü tahtası 9-dilim büyütüldü, pano yazısı
ölçekten türetilir, cam eşiği 100.

**Sprite hattı.** `sim3` kataloğu sandalyeli sürümle değişti; üretilen sandalye ara çözümü ve
onu üreten ~90 satır `build-sprites.py`'den kalktı. Tasarımcı (ponytail) için sandalyeli oturma
ve arka yazma kareleri katalogdan alındı (%5 altı ölçek farkında yeniden boyutlama yok, taban
keskin kalır).

**Sanal ortam artık cihaza göre kuruluyor (2026-09-20).** `runtime/.venv/Scripts/python.exe` bir
shim'dir ve onu kuran makinenin/Windows kullanıcısının `python.exe`'sini **mutlak yolla** çağırır;
aynı çalışma dizinini başka bir kullanıcı açınca dosya durur ama çalışmaz (`No Python at '...'`,
çıkış kodu 103). `verify.ps1` yalnız dosyanın varlığına baktığı için bunu "kurulu" sanıyordu ve
yukarıdaki üç ortam notunun hepsi bu yüzden yazıldı. Artık yorumlayıcı **fiilen koşturularak**
yoklanıyor; bozuksa adım onarım komutunu söyleyerek duruyor. `-SetupRuntime` anahtarı bu cihazın
Python'unu bulup (`py -3` → `python` → `python3`, `sys.executable` sorulur, Store kısayolu elenir)
`.venv`'i siler ve yeniden kurar. Kapı kendiliğinden kurulum yapmaz — sessiz onarım, `verify.ps1`'in
"eksik altyapıda sessizce geçme" kuralına aykırı olurdu.

**Runtime'ı Api başlatıyor (2026-09-20).** İki terminal açma zorunluluğu kalktı:
`Api/Runtime/RuntimeSupervisor` (IHostedService) kalkışta `GET {RuntimeUrl}/health` ile yoklar,
kapalıysa `runtime/.venv` yorumlayıcısıyla uvicorn'u başlatır, kapanışta öldürür.
**Sahiplik kuralı:** 5090'da zaten sağlıklı bir runtime varsa ona dokunulmaz ve kapanışta
durdurulmaz (elle başlatılmış olabilir). Python yoksa Api **yine kalkar**; uyarı günlüğe düşer
(onarım komutuyla birlikte), UI zaten "Runtime kapalı" der — `Python'u kapatmak derlemeyi ve
ServiceTests'i bozmaz` kuralı korunur. Sağlık beklemesi startup'ı bloklamaz, arka planda koşar.
Süreç başlatma Infrastructure'da (`PythonRuntimeProcess`, `WindowsProjectLauncher` gibi), gözetim
Api'de (`JobWorker`/`RunResumer` gibi); iş kuralı taşımaz (CLAUDE.md §1). uvicorn çıktısı Api
günlüğüne `runtime: ...` diye akar. Kapatma anahtarı `AITeam:AutoStartRuntime=false`.
Aynı turda `AITeam:RuntimeUrl` için de loopback denetimi eklendi (CLAUDE.md §3; `ApiUrl`'de vardı).

**UI ikon ve düğme tutarlılığı.** Emoji/glif karışımı bitti: tek renkli SVG ikon seti
`Ico.vue` (`bell · gear · folder · trash · refresh · clock · check · exit`). Kapat düğmeleri
tüm panellerde aynı (28×28, ortalı, hover); "+" ortalandı; proje silmede çöp kutusu ikonu;
halka etiketleri görünür, ikon-metin hizası düzeltildi.

## Kalan işler (2026-09-20 itibarıyla, öncelik sırasıyla)

1. ~~**`canAsk`**~~ → geldi (2026-09-20): developer takılınca soru önce `can_ask` hedefine (manager, 1 tur, okuma aracı); cevaplarsa kullanıcı görmez, yükseltirse/ikinci takılmada kullanıcıya (DOMAIN → Takılma). Servis testi 46.
2. **Faz 5 SSE** `GET /runs/{id}/events`: uç **yok**; UI 5 s'de bir yokluyor ve bunu üç ayrı bileşen ayrı ayrı yapıyor (`app.vue`, `JobsPanel`, `KanbanPanel`). Çok çalışma açıkken yük artar.
3. ~~İş Akışı paneli~~ → Ekip paneli "Takımlar" sekmesi (2026-09-20): adım ekle/sil/sırala, ajan/tür/ofis rolü; yeni ajan ekleme ve sahneye otomatik yerleşim (ziyaretçi dahil) da geldi.
4. **Tasarım artıkları**: ray daralması (proje açıkken 72 px), pano için çalışma düzeyinde analiz kartı.
5. **Açık kararlar** (DOMAIN §Açık kararlar 2, 5, 6, 7): `design` görev başına mı çalışma başına mı, `stage.officeRole` ↔ ajanın `office_roles` çakışma denetimi, `kind: handoff` ile `handoffRole` ikiliği, panoda çalışma düzeyi analiz kartı. (4 kapandı 2026-09-21: `askRole` akış düzeyinde.)
6. **`ownerId`** JWT claim'inden; LDAP / kullanıcı deposu `IUserDirectory`.
7. **Sağlayıcılar**: NVIDIA / Ollama runtime adaptörleri (sözleşme hazır). OpenAI eklendi (2026-09-20, aşağıda).
8. **Emülatör / tarayıcı testleri** için MCP araçları (testçi kararı).
9. **OpenAI gerçek doğrulama** (2026-09-20 devir, iki oturum birleşti): Codex `login` denenmedi; `codex exec --json` olay
   adları ve `--output-schema` gerçek turla doğrulanmadı (ilk turda `_parse_events` ve `-o` son mesaj dosyası kontrol
   edilecek); gerçek API anahtarıyla tur yapılmadı. Bilinen varsayımlar: Codex kalan hak vermiyor (limit koruması geçer),
   sistem promptu metnin başına gidiyor, API anahtarı yolunda araçlı adım 501, katalog/fiyat tahmini sabit, araçsız
   Codex turu read-only sandbox ama komut koşabilir. (`.claude/launch.json` temizliği yapıldı: dosyada yalnız `ui`, `api`,
   `runtime` var — 2026-09-22 denetimi.)

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

**Kalan (2026-09-22 denetimi):** yalnız gerçek `RunService` olayları, yani Faz 5 SSE. İş Akışı paneli
geldi (Ekip → Takımlar sekmesi) ve `npm run gen:api` çalışıyor: Api ayaktayken tipler yeniden üretilip
`ui/shared/types/api.ts` ile karşılaştırıldı, **fark yok**.

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

## Kalıcılık: dosya deposundan SQLite'a (2026-09-21, kullanıcı kararı) ✅

**İstek:** "projeye db katmanı ekleyelim" — CLAUDE.md §2'nin "veritabanı yoktur" kuralı ve
§Sapmalar'daki budama bilinçli olarak geri alındı. Motor **SQLite**, erişim **EF Core 10**,
sınır **yalnız çalışma zamanı durumu** (kullanıcı kararları).

**Neyin nereye gittiği:**

| Önce | Sonra |
|---|---|
| `runs/<id>/run.json` | `run` satırı (durum, adım, maliyet, soru, plan, akış kopyası sütunları) |
| `runs/<id>/spec.json`, `workflow.json` | `run.spec`, `run.workflow_snapshot` (JSON sütun) |
| `runs/<id>/conversations/{ajan}.jsonl` | `run_turn` (sağlayıcı/model/hedef/maliyet/token sütun, gövde JSON) |
| `runs/<id>/messages.jsonl` | `run_message` |
| `runs/<id>/tasks/{görev}/phases.jsonl` | `run_phase` |
| `config/projects/{key}.json` | `project` |
| `config/settings.json` | `app_settings` (tek satır) |
| `config/agents/*.md`, `knowledge/*.md`, `workflows/*.json`, `scene.json` | **değişmedi** — git'te kalır |

**Örüntü kaynağı [sst/opencode](https://github.com/sst/opencode):** sorgulanan alanlar sütun +
taşınan yük `data` JSON sütunu; monoton kimlikle sıra (JSONL satır sırasının karşılığı, ileride
SSE'nin imleci — opencode'un ayrı `seq` sayacı alınmadı: istek yolu ile iş kanalı aynı çalışmaya aynı anda
yazabildiği için `MAX+1` yarışır, AUTOINCREMENT `id` yeter); `onDelete: cascade`; ileri yönlü migration defteri; mutlak yolun veritabanına hiç
girmemesi. opencode'un yapılandırmayı dosyada bırakan ayrımı da birebir alındı.

**Şema yönetimi (ARCHITECTURE.md §8):** `scripts/sql/changes/0001_create_core_tables.sql` derlemeye
gömülür; `SchemaMigrator` uygulanmamışları sözlük sırasında, her birini kendi işleminde koşar ve
`schema_change_log(script_name, checksum, applied_at, applied_by)` defterine yazar. **Uygulanmış bir
betiğin checksum'ı değişirse kalkış durur.** EF migration yok; EF yalnız eşler, sütun adları elle verilir.

**Sapma:** ARCHITECTURE §8.3 DDL'i ayrı bir dağıtım rolüne verir ve replikaların kalkışta DDL
koşmasını yasaklar. Burada tek makinede tek süreç var, dağıtım adımı yok → şema kalkışta uygulanır.
Çok kopya gerekirse bu kaldırılır ve betikler dışarıdan koşulur.

**Kazanılanlar:** çalışma kimliği artık dosya adına dönüşmediği için `runs/` dışına çıkma
(path traversal) hata sınıfı **tamamen** kalktı; silme tutarlılığı FK cascade'e devredildi;
kullanım/maliyet sorgusu dosya taramadan çıktı.

**Biten sayılır / ölçüldü (2026-09-21):** `verify.ps1` tüm adımlarda geçti — build 0 uyarı,
**34 birim + 68 servis** testi (dosya deposu testleri yerine: ekleme sırası, bozuk gövde kurtarma,
cascade silme, defter + WAL + checksum uyuşmazlığında durma, toleranslı damga okuma, tek sorgulu kullanım
özeti, ayar gidiş-dönüşü, drift denetimi `ProbeDatabaseAsync`), Python 43, UI typecheck.

**Code review (2026-09-21, aynı gün):** 10 bulgu, 10'u uygulandı. En önemlisi `seq` sayacının kaldırılması
(istek yolu ile iş kanalı aynı çalışmaya aynı anda yazabiliyor; `MAX+1` yarışıyordu), `UsageReader`'ın tek
sorguya inmesi ve eski `runs/` klasörü için kalkışta uyarı.
Servis testleri artık depoları elle değil **DI'dan** alıyor: kayıt yanlışsa test de düşer.

**Sıradaki iş:** MAF (`Microsoft.Agents.AI`) Compaction ile `compact` — geçmiş artık `run_turn`'den
okunacak. Bulut oturumundaki araştırma kararları: MAF yalnız kütüphane olarak, LLM çağrısı ve
orkestrasyon için değil.

## Akış çoğaltma: rol sayısı ve bağlam taşıma (2026-09-21, kullanıcı isteği) ✅

**İstek:** üç akış — (1) tek kişi: analiz + geliştirme + test, sorular insana; (2) ikili:
analist/tasarımcı ve developer/testçi; (3) tam kadro: analist → manager onayı → tasarımcı →
manager onayı → developer → testçi. Hepsi **UI'dan yönetilebilir** olacak.

**Ölçüm gerekçesi:** 2026-09-21 hello-world koşusunda $4,02'nin $2,07'si (%51) aynı doğrulamanın
iki kez yapılmasına gitti — testçi ve manager ikisi de `ls -R` + tüm dosyaları `cat` + `build` +
`test` koştu (11'er iç tur, 9'ar Bash). Sebep: manager'a testçinin kararı değil **görevin kendisi**
veriliyordu ve `ToolAccess.ForKind(Review) => Full` ile yazma araçları alıyordu.

**Akış düzeyine taşınan iki karar** (ajan düzeyinde olmaları yanlıştı: aynı ajan farklı akışlarda
farklı davranmalı):

| Alan | Değerler | Neden akışta |
|---|---|---|
| `planApprover` | `user` (varsayılan) · ajan | `analyze` çalışma başına bir kez koşar, görev başına değil — görev düzeyindeki `review` ile ifade edilemez |
| `askRole` | `null` (ajanın `can_ask`'i) · `user` · ajan | Manager'ı olmayan bir akışta developer manager'a soruyor, akışta olmayan ajanı (ve maliyetini) işe sokuyordu (**açık karar #4 kapandı**) |

**Geri dönüş kuralı genelleştirildi:** `ImplementBefore` → `ProducerBefore` — reddedilen iş
kendinden önceki en yakın **üretici** adıma döner (`design` ya da `implement`). Önceden yalnız
`implement` aranıyordu, bu yüzden "tasarımı onayla, reddedersen tasarımcıya dön" kurulamıyordu.
`analyze` üretici sayılmaz (çalışma düzeyi). Kural hâlâ tek kaynakta.

**Ajan md'leri çoğaltıldı**, her akışın şekline göre yazıldı:
`analist-developer` (tek kişi: üç adım + sorular kullanıcıya), `analist-tasarimci` (plan tarafı,
tasarımın gerekli olup olmadığına kendi karar verir), `developer-testci` (yapım tarafı, kendi işini
denetlerken kendine karşı yumuşak olma riski prompt'ta açıkça adlandırılır).

**Yol boyunca çıkan dört ayrı hata:**

1. **`developer.md` bayat ve çelişkiliydi:** "sadece kod bloklarını üret, blok dışında açıklama
   yazma" diyordu. `FileBlockParser` kodda yok; `Implement` şeması `filesChanged`/`commandsRun`
   istiyor, yani developer dosyaları araçlarla kendisi yazıyor. Model iki zıt talimat alıyordu.
2. **Sahne kaydı kendini onarmıyordu:** `AgentService.UpdateAsync` sahneyi yalnız **ad değişince**
   yazıyordu; md'si elle eklenen bir ajan ofiste sonsuza dek görünmez kalıyordu. Artık her kayıtta
   `UpsertAgentAsync` çağrılır (idempotent, değişiklik yoksa dosya yazılmaz) ve `bool` döner.
3. **`verify.ps1` yanlış teşhis üretiyordu:** `$LASTEXITCODE` önceki adımdan sızdığı için bir test
   hatası üç alakasız denetimi de BAŞARISIZ gösteriyordu. Adım başına sıfırlanır.
4. **`EXTRA_ROLE_HEX` yazılmış ama hiç bağlanmamıştı:** sonradan eklenen ajanların renk noktası boş
   kalıyordu. `roleHex(key)` anahtardan türetir — aynı ajan her açılışta aynı rengi alır.

**Biten sayılır / ölçüldü (2026-09-21):** `verify.ps1` tüm adımlarda geçti — **38 birim + 70 servis**
testi, Python 44, UI typecheck. Üç akış da API'den kabul edildi ve UI'daki Takımlar sekmesinde
düzenlenebiliyor; dokuz ajanın hepsi sahneye yerleşti.

**Ölçülmedi:** üç akışın gerçek maliyeti. Aynı hello-world brief'iyle koşulup `default` ($4,02) ile
karşılaştırılmalı. Beklenti: tek kişi ~3 tur, ikili 4 adım, tam kadro 5 adım.

## Üç akışın ölçümü + organizatör maliyetsizleşti (2026-09-21) ✅

**Kullanıcı kararları:** organizatör LLM turu harcamasın (yalnız aktarım yapsın) · insan sadece
sorulara cevap versin (plan onayı için durdurulmasın) · varsayılan akış **tek kişi** olsun.

**Yapılanlar:** `Prompts.HandoffNote` devir notunu kodda üretir, `HandoffAsync` artık model çağırmaz
(bütçe kontrolü de gerekmez) · `planApprover: auto` üçüncü seçenek olarak geldi (onay kapısı yok) ·
`default` akışı tek kişilik oldu, `organizer` her akışa `handoffRole` olarak atandı.

**Ölçüm** — aynı brief, aynı model (ölçüm süresince hepsi `claude-sonnet-5`, haftalık Opus penceresi
%92 dolu olduğu için kullanıcı kararıyla):

| | Tek kişi | İkili | Tam kadro |
|---|---|---|---|
| Sonuç | **Completed** | **Completed** | AwaitingInput (yarım) |
| Maliyet | **$1,38** | $1,61 | $1,70 |
| Ajan turu | 3 | 7 | 12 |
| Girdi token | 2.517.080 | 2.076.348 | 1.054.901 |
| **Önbellek okuma** | **%92,9** | %87,3 | %71,1 |
| Organizatör | **$0** (1 devir) | **$0** (2 devir) | **$0** |
| Çıktı | build 0 uyarı, test geçer | build 0 uyarı, test geçer | kod yok |

**Önbellek oranı rol sayısıyla birebir düşüyor** (1 ajan %92,9 → 2 ajan %87,3 → 3 ajan %71,1).
Bu, "her rol değişimi önbellek ön ekini kırar" tezinin doğrudan ölçümü: tek ajan tek ön ek demek.

**Yol boyunca çıkan hata (gerçek koşuda yakalandı):** otomatik ve ajan onayında durum `Running`
yazılıyor ama **iş kuyruğa konmuyordu** — üç çalışma birden sonsuza kadar bekledi. Eski akışta bunu
kullanıcının "Onayla" isteği (uç → `ScheduleAndAccept`) yapıyordu. `scheduler.Schedule` üç geçişe de
eklendi; üç yeni test bunu kilitliyor (`Otomatik_onayda_dagitim_kuyruga_girer`, ajan onayı kabul/red).

**Tam kadro yakınsamadı:** manager tasarımı **3 kez reddetti** (tavan 3) ve iş kullanıcıya düştü;
$1,70 harcandı, kod üretilmedi. Tasarım kapısı hello-world ölçeğinde fazla sıkı. Ayar gerektirir:
ya `maxReviewRounds` ya manager'ın tasarım için kabul ölçütü.

**Testler ürünün varsayılanından ayrıldı:** servis testleri artık kendi `klasik` akışını kuruyor
(`default` değiştiğinde kırılmasınlar diye); `WorkflowStoreTests` şekle değil değişmezlere bakıyor.

**Ölçüldü:** `verify.ps1` tüm adımlarda geçti — **38 birim + 73 servis**, Python 44, UI typecheck.

### Claude Code ile kıyas — aynı model, aynı efor (2026-09-21)

Aynı brief (Türkçe), `claude-sonnet-5`, efor `high`, aynı araç seti, boş dizin:

| | **Claude Code** | Tek kişi | İkili | Tam kadro |
|---|---|---|---|---|
| Sonuç | ✅ | ✅ | ✅ | ⚠️ yarım |
| Maliyet | **$0,426** | $1,384 | $1,606 | $1,700 |
| İç tur | **16** | 55 | 58 | 40 |
| Girdi token | **876.793** | 2.517.080 | 2.076.348 | 1.054.901 |
| Süre | **68 sn** | ~3,5 dk | ~4 dk | ~5,5 dk |
| Önbellek okuma | %93,4 | %92,9 | %87,3 | %71,1 |

**Tek kişi akışı Claude Code'un 3,25 katı.** Ama fark **önbellek değil**: tek ajanda oran zaten
eşitlendi (%92,9 ≈ %93,4). Fark **iş hacmi**: 55 iç tur / 16.

Sebep, adım sınırları. Tek kişi akışında aynı ajan üç adımı da koşuyor ama her adım ayrı bir
`CallAsync` ve tek mesajlık geçmişle başlıyor — yani ajan **kendi bağlamını kendi adımları arasında
atıyor**. Dökümü: analiz 4 iç tur · geliştirme 32 · **test 19**. Test adımı, geliştirme adımının az
önce yazdığı dosyaları sıfırdan okuyup doğruluyor. Claude Code bunu tek sürekli konuşmada yapıyor,
yazdığını zaten biliyor.

**Compact için sonuç:** sıkıştırma bu farkı kapatmaz — kapatacak olan, aynı ajanın adımları arasında
**bağlamın taşınması**. Compact'in yeri bu taşınan bağlam büyüyünce onu sınırlamaktır.

### Adımlar arası bağlam taşıma (2026-09-21) ✅

**Gerekçe:** Claude Code kıyası, farkın önbellek değil **iş hacmi** olduğunu gösterdi (55 iç tur / 16).
Sebep: her adım tek mesajlık geçmişle başlıyordu, yani aynı ajan kendi bağlamını **kendi adımları
arasında** atıyordu. Test adımı, geliştirme adımının az önce yazdığı dosyaları sıfırdan okuyordu.

**Kural:** bir ajan, **aynı görevdeki** kendi önceki turlarının **çıktısını** görür
(`RunService.AgentTaskHistoryAsync`). Taşınan şey çıktı; önceki **istem** taşınmaz — istem zaten
görev bağlamını (plan, kurallar, notlar) içerir, tekrarı her turda bedel ödetirdi. Kapsam görev
başına: başka görevin geçmişi taşınmaz.

**Ölçüm** (aynı brief, `claude-sonnet-5`, efor `high`). İki koşu birebir kıyaslanamaz — analist
farklı planladı (1 görev / 2 görev), o yüzden **görev başına** bakılır:

| Görev t1 | Taşıma yok | Taşıma var | Fark |
|---|---|---|---|
| geliştirme iç tur | 32 | **15** | −%53 |
| test iç tur | 19 | **16** | −%16 |
| toplam iç tur | 51 | **31** | **−%39** |
| girdi token | 2.407.296 | **1.191.095** | **−%51** |

Toplam maliyet **iki kat iş yapılmasına rağmen** $1,3838 → $1,3180. Önbellek okuma %92,9 → %94,6.
Çıktı doğrulandı: build 0 uyarı, `Merhaba, dünya!`, test geçiyor.

**Compact'in yeri artık belli:** taşınan geçmiş red turlarıyla büyür (implement → review → implement…).
Sıkıştırma bu büyüyen geçmişi sınırlamak içindir — taşıma olmadan sıkıştıracak bir şey yoktu.

### Claude Code'un sistem promptu: ölçüldü, iddia tutmadı (2026-09-21)

**Hipotez:** SDK'ya `system_prompt` düz string verildiğinde Claude Code'un kendi çalışma kılavuzu
siliniyor; kılavuz korunursa (`preset: claude_code` + `append`) yürütme verimi Claude Code'a yaklaşır.
Doğrulandı: SDK iki biçimi de kabul ediyor (`SystemPromptPreset`).

**Ölçüm 1 — global preset:** analist iki koşuda da plan üretemedi. Bir kez "summary: test, rule1, a.cs"
taslağı (3 `StructuredOutput` denemesi), bir kez *"Failed to provide valid structured output after 5
attempts — must have required property 'rules', 'tasks'"*. Developer aynı preset altında geçerli yapısal
çıktı verdi. Yani preset yapısal çıktıyı genel olarak değil, **büyük iç içe plan şemasını** bozuyor.

**Uygulanan:** mod adım başına, karar .NET'te (`ToolAccess.IsExecution` → `SystemPromptModes`), Python yalnız
eşler. Write araçlı adımlar `claude_code`, plan üreten adımlar `replace`. Testler: Python 45, .NET 38 + 75.

**Ölçüm 2 — adım başına mod, aynı brief/model/efor:**

| | A: string, taşıma yok | B: string, taşıma var | C: adım başına mod |
|---|---|---|---|
| Maliyet | $1,38 | $1,32 (2 görev) | **$0,93** (1 görev) |
| İç tur | 55 | 68 | 34 |
| **t1 geliştirme + test** | 32 + 19 = 51 | 15 + 16 = **31** | 16 + 15 = **31** |
| Önbellek okuma | %92,9 | %94,6 | %90,7 |

**Sonuç:** görev başına iç tur B ile C'de **birebir aynı (31)**. C'nin ucuzluğu analistin 1 görev planlamasından;
preset'ten değil. Preset ayrıca büyük ön ekini her yürütme adımında bir kez yazdığı için önbellek oranını hafif
düşürdü. **51 → 31'i taşıma açıkladı; preset n=1'de sıfır kazanç.** Kalan 31 vs Claude Code 11-16 farkı
yapısal: ayrı test adımı (15 tur), ayrı analiz turu, adım başına yapısal çıktı — ürünün kendi tasarımı.

**Varsayımla ilerlenir:** mod altyapısı duruyor (düşük risk: planlamada kapalı, testli). Bir ikinci örnek de
görev başına fark göstermezse `claude_code` yolu kaldırılır; ölçüm altyapısı olmadan bu karar verilemezdi.

### Solo akış: Claude Code seviyesi (2026-09-21, kullanıcı isteği) ✅

**Fikir:** yapısal farkı ölçüm göstermişti — ayrı test adımı, ayrı analiz turu, adım başına şema. En yalın
yasal akış kuruldu: `analyze` (değişmez gereği zorunlu) + tek `implement`; **ayrı test adımı yok**, doğrulama
işin içinde. Ajan `solo`: "asgari plan, tek görev, yaz-çalıştır-doğrula-bitir; aynı komutu iki kez koşma".
`planApprover: auto`, `askRole: user`, `handoffRole: organizer` (maliyetsiz).

**Ölçüm** — aynı brief, `claude-sonnet-5`, efor `high`:

| | Claude Code (2 örnek) | **Solo** | Tek kişi (3 adım, taşımalı) |
|---|---|---|---|
| İç tur | 11 · 16 | **16** (analiz 3 + iş 13) | 34 |
| Maliyet | $0,21 · $0,43 | **$0,34** | $0,93 |
| Girdi token | 485k · 877k | **566k** | 1,49M |
| Önbellek okuma | %95 · %93 | **%92,2** | %90,7 |
| Süre | 55 · 68 sn | **~90 sn** | ~2,5 dk |
| Çıktı | build/run/test ✔ | build/run/test ✔ | build/run/test ✔ |

**Sonuç:** Solo, Claude Code'un aralığının içinde; üstüne yazılı plan + kabul ölçütleri + tam kayıt veriyor
(Claude Code'da yok). Fark artık kapanmış sayılır. Ayrı denetim kapısı isteyen işler için `default`
(tek kişi, 3 adım) ve `ikili`/`tam-kadro` duruyor; **hız/maliyet için `solo`**.

## Compact: MAF Compaction taşınan geçmişe (2026-09-21) ✅

**Yer:** `RunService.AgentTaskHistoryAsync`'in taşıdığı önceki çıktılar. Red turlarıyla büyür (her implement
turu öncekilerin çıktısını taşır; `maxReviewRounds: 3` ile 6 mesaja kadar). Yeni istem sıkıştırmaya **girmez**.

**Yerleşim:** politika Application'da — `IHistoryCompactor` + `CompactionBudget.TaskHistory` (4 mesaj / ~12k
token); uygulama Infrastructure'da — `MafHistoryCompactor`, `Microsoft.Agents.AI` 1.22.0'ın **statik**
`CompactionProvider.CompactAsync`'i (agent runtime yok, `AIAgent` yok). Stratejiler modelsiz:
`Truncation(TokensExceed)` → `SlidingWindow(MessagesExceed)` pipeline'ı. Özetleme yok: bir model turu
ister, CLAUDE.md §4 gereği kayda ve bütçeye girmeli — ayrı karar. `[Experimental]` → `NoWarn MAAI001;MEAI001`.

**Ölçüm (deterministik, sahte runtime):** 6 zorla red turu → developer 6. turda 10 mesaj taşıyacaktı,
**4'e** indi; ilk tur 1 mesaj, 3. tur 5 (bütçe içinde, dokunulmadı); istem daima sonda ve tam; roller almaşık.
Sentetik: 10 tur → 4 mesaj, en yeni korunur en eski düşer; bütçe içindeyse aynı örnek geri döner; 50k token'lık
tek çıktı kırpılır, en yeni geri bildirim kalır. Testler: 38 birim + **79** servis.

**Gerçek koşuda ölçülmedi:** hello-world'de hiç red olmadı; geçmiş 4 mesajı aşmadı. Kazanç yalnız uzun red
döngülerinde görünür. Doğrulanmış olan: doğru yerde, doğru sınırla, hiçbir şeyi bozmadan devrede.

## Merge: SQLite dalı ana dala alındı (2026-09-22) ✅

`feat/sqlite-persistence` (tek commit, `4a6c758`) main'e **fast-forward** ile alındı — ayrık commit yoktu.
Çalışma ağacındaki commit'siz kota işi stash'lenip geri uygulandı; `app.vue` ve `LimitsBar.vue`'de 4 çakışma
çıktı. Sebep: iki taraf **aynı işi ayrı adla** yapmıştı (dalda `signedOutProviders`, yerelde
`offlineProviders`; ikisi de "bant yalnız hiçbir sağlayıcıda giriş yokken çıksın" kuralını kuruyordu).
Dalın adlandırması korundu, çift kalan `offlineReason` silindi; yalnız yerelde olan kısımlar üstüne
bindirildi: halkada **kullanılan** yüzde, `saatlik`/`haftalık` etiketleri, `_scope_name` (kapsam nesnesinden
model adı) ve kullanılmayan sağlayıcının soluk `.quiet` çipi.

**Doğrulama:** 38 birim + 79 servis + 46 python testi, hepsi geçti; ui typecheck ✔; mimari denetimleri
(bağımlılık yönü, yasaklı ad alanları, Python sınırı) ✔.

**.NET testleri Windows'ta koşmadı.** Smart App Control imzasız yerel derleme çıktılarını engelliyor
(LESSONS → Windows); merge öncesi commit de birebir aynı şekilde patlıyor, yani sebep bu dal değil.
Yukarıdaki .NET sayıları WSL Ubuntu'daki koşudandır. Api Windows'ta **hiç başlamıyor**, dolayısıyla
SQLite geçişi uçtan uca çalıştırılarak denenmedi: `data/aiteam.db` bu makinede henüz oluşmadı.
Şema betiklerinin uygulanması, `schema_change_log` ve WAL davranışı yalnız testlerle doğrulanmış durumda.

### Yerel kalkış: Api WSL'de, runtime Windows'ta (2026-09-22)

SAC yüzünden Api Windows'ta başlamıyor, ama `.wslconfig`'te `networkingMode=mirrored` var: WSL ile Windows
`127.0.0.1`'i paylaşıyor. Kurulum — Api WSL'de (`AITeam:ConfigRoot` **gerçek depoyu** gösterir,
`AITeam:AutoStartRuntime=false`), Python runtime Windows'ta (giriş yapılmış `claude.exe` oturumu orada),
UI Windows'ta. Hiçbir şey `0.0.0.0` dinlemiyor, CLAUDE.md §3 korunuyor.

**Çalışan:** şema geçişi (`2 şema betiği uygulandı`, `data/aiteam.db` + WAL/SHM oluştu), EF okumaları,
giriş, `GET /providers` (`anthropic loggedIn=true`, hesap doğru), `GET /limits`, proje oluşturma
(MSBuild bariyeri dahil), çalışma kaydı, `LimitGuard`'ın tur öncesi kota okuması, düşen çalışmanın
veritabanına yazılması.

**Çalışmayan — ajan turu.** `POST /v1/turn` 502: `Failed to start Claude Code: [WinError 267] Dizin adı
geçersiz`. Sebep bölünmüş kurulum: WSL'deki Api `cwd`'yi `/mnt/c/...` diye gönderiyor, Windows'taki
`claude.exe` bu yolu tanımıyor. İki taraf farklı dosya sistemi ad uzayında. Uçtan uca koşu için Api'nin
Windows'ta çalışması, yani SAC'ın kapatılması gerekiyor. Runtime'a yol çevirisi eklemek **çözüm değil**:
§1 gereği runtime'a iş kuralı sızdırır.

**Yan bulgu (dalla ilgisiz, belge düzeltildi):** `POST /projects` gövdesinde `description`/`workflow`/
`targetDir` **atlanamıyor**, `null` verilmeli — yoksa 400 `request.invalid`. API.md bunları `?` ile
isteğe bağlı gösteriyordu.

## Proje bütçesi ve limit sonrası kesintisiz devam (2026-09-22, kullanıcı isteği) ✅

**İstenen üç şey, biri zaten vardı.**

**1. Proje başı toplam token + bütçe (yeni).** Bütçe bugüne kadar yalnız iş başınaydı (`Run.MaxCostUsd`);
"bütçe projede yoktur" kararı kalktı. `Project.MaxCostUsd` ve `Project.MaxTokens` eklendi, **ikisi de boş =
sınırsız** — var olan projelerin davranışı değişmedi. Ölçüler bağımsız, **önce dolan durdurur**. Harcama
`run.input_tokens` / `run.output_tokens`'ta maliyetle aynı yoldan birikir; proje kartı çalışmaları toplar.
Tavan doluysa **yeni iş hiç kurulmaz** (`project.budget_exceeded`), süren iş tur sonunda `BudgetExceeded`.
Şema: `0003_project_budget_and_run_tokens.sql`.

**2. Limit dolunca kaldığı yerden devam — zaten kuruluydu.** `LimitGuard` çağrıdan **önce** bakar (boşa token
gitmez) → çalışma `Paused` + `ResumeAt` (pencerenin sıfırlanma zamanı) → `RunResumer` dakikada bir bakıp
`Run.Step`'ten sürdürür. Tamamlanmış fazlar yeniden koşmaz, sayaç artmaz, sıfırdan başlamaz. Yani istenen
davranış vardı; ölçülüp belgelendi.

**3. Gerçek boşluk: limit çağrı SIRASINDA gelirse (düzeltildi).** Koruma yüzdeleri 90 s önbellekli, pencere
tam o aralıkta dolabiliyordu; sağlayıcının reddi `provider_error` sayılıp çalışma **`Failed`** oluyordu —
pencere sıfırlandığında kendiliğinden sürmüyor, kullanıcı elle "yeniden dene" demek zorunda kalıyordu.
Runtime artık bunu `runtime.provider_limit` diye ayrı sınıflandırıyor (karar hâlâ .NET'te, §1 korunuyor),
`AgentCaller` bekleme turuna çeviriyor. Tekrar denenmiyor: sıfırlanma dakikalar/günler sonra.

**Doğrulama:** 38 birim + 82 servis + 47 python = **167 test, hepsi geçti**; ui typecheck ✔.
Yeni testler: proje bütçesi varsayılan sınırsız / token toplamı kartta / sıfır-negatif reddi;
proje bütçesi dolunca çalışma durur ve yeni iş başlamaz; çağrı sırasında limit gelirse beklemeye düşer ve
aynı adımdan sürer; runtime sınıflandırması (kota reddi ≠ hata ≠ giriş yok).

**Ölçülmedi:** gerçek sağlayıcı limitiyle uçtan uca koşu — bu makinede Smart App Control ajan turunu
engelliyor (yukarıda). Davranış deterministik sahte runtime ile doğrulandı.

## Bağlam bütçesi: kalibre ölçü ve kayıt (2026-09-23, kullanıcı onayı) ✅

**İstenen:** token maliyeti için bağlam boyutuna bakan yarı kod yarı LLM yapı. Önerilen dört adımdan 1 ve 2
onaylandı, 3 ölçüme hazır hale getirilecekti.

**1. Kalibre token ölçüsü (yapıldı).** `TokenCalibration.Fit`: araçsız turlardan tur başına girdi ↔ istem karakteri
doğrusal oturtması, eğimden karakter/token. Yetersizse 4 (bugünkü davranış). `CompactionBudget.CharsPerToken`,
`MafHistoryCompactor` tetiği bu orandan **karakterle** sayar — önceden erken çıkış karakter/4, MAF bayt/4 sayıyordu
(Türkçe harf 2 bayt: iki eşik birbirini tutmuyordu). Depo: `ReadCalibrationSamplesAsync`, `data` gövdesi
okunmadan `json_extract`. Şema değişmedi (alanlar `data` JSON'unda).

**2. Pencereye oranlı bütçe (yapılmadı, gerekçeli).** Ölçüm plana karşı çıktı: tavanı pencereye göre büyütmek
maliyeti **artırırdı** (taşınan geçmiş her iç turda yeniden gider) ve bugünkü ajanların hepsi ≥200k pencereli
Anthropic'te — koşul hiç bağlamazdı. Karar ve açılma koşulu: DOMAIN.md → Bağlam bütçesi.

**3. Ölçüme hazır (yapıldı).** `Turn.toolsOffered` (kalibrasyon örneği seçimi) ve `Turn.context`
(`ContextStats`: öncesi/sonrası mesaj+karakter, oran, örnek sayısı) sona eklendi (§5); UI tipleri üretildi.
`scripts/context-report.py` dolum eğrisini, düşeni ve "yeniden gönderilmeyen" tahmini gösterir.

**Doğrulama:** 38 birim + **88** servis testi (WSL; Windows'ta SAC engeli, LESSONS → Windows). Yeni: kalibrasyon
(ek yük + iç turlar eğimden ayrılır, düz oran < 1,5 çıkar; az örnek / yayılımsız / aralık dışı → varsayılan),
kalibre oran eşiği taşır (Türkçe metin), depo yalnız araçsız + aynı model örnekleri döner, 6 red turunda tur kaydı
10 → ≤4 mesajı ve varsayılan oranı gösterir. `verify.ps1`: build, mimari, Python sınırı, ui typecheck ✔.

**Ölçülmedi:** gerçek koşu yok (`data/aiteam.db`'de tur yok). Kalibrasyonun gerçek değeri ve sıkıştırmanın
tetiklenme sıklığı ilk koşulardan sonra `context-report.py` ile okunacak; 4. adım (LLM özeti) o rakama bağlı.

## Tasarım kapısı, geri alma ve ziyaretçi turu (2026-09-22) 🔶 ölçüm bekliyor

### 1. Tasarım kapısı yakınsamıyordu — kök neden istemdeydi

`tam-kadro` 2026-09-21'de ard arda 3 red verip **$1,70 harcadı ve tek satır kod üretmedi**. Suç
manager'ın md'sinde sanılmıştı; asıl neden `Prompts.ReviewTask`'ın **tek bir inceleme türü** varsayması:

> "Developer dosyaları bu dizine yazdı… komutları Bash ile FİİLEN çalıştır" … "Şüphedeyken reddet."

`tasarim-onay` adımında ortada kod yok — tasarımcı bir rehber üretti. Manager'a olmayan bir build'i
koşması söyleniyor, bulamıyor, sonra "şüphedeyken reddet" talimatını uyguluyordu.

**Düzeltme (özel durum değil, genelleme):** inceleme istemi artık kapının **beklettiği üretici adıma**
göre şekilleniyor — bilgi zaten tek kaynakta duruyordu (`Workflow.ProducerBefore`). `design` kapısında:
komut koşulmaz, `testsRun=false`, ölçüt "developer bu rehberle tahmin etmeden ilerleyebilir mi", ve
**şüphedeyken kabul** (ara kapıda red bedava değil; eksiği sonraki test adımı zaten yakalar).
`implement` kapısında eski davranış aynen korunur, şüphedeyken red.

Aynı ayrım `manager.md` (§2a ara kapı / §2b son kapı) ve `karar-ilkeleri.md`'ye de girdi. Oradaki
**"karşılanmamış kabul ölçütü varsa hüküm RED'dir"** kuralı asıl kilitti: bir rehber kabul ölçütünü
*karşılamaz*, onu kod karşılar — kural manager'ı reddetmeye zorluyordu. Artık yalnız son kapı için.
Her iki kapıya ölçek kuralı eklendi: brief'in istemediği ek özellik/belge/mimari talep edilmez.

### 2. Geri alma: opencode `snapshot` aktarıldı

Ajanlar dosyayı kendileri yazar; red turunda yarım kalan dosyalar sonraki tura kalıyordu ve **geri
alma yolu yoktu**. Örnek [sst/opencode](https://github.com/sst/opencode) `snapshot`: proje dizininin
**dışında** duran bir gölge git deposu (`--git-dir` ayrı, `--work-tree` proje kökü) — kullanıcının
kendi git geçmişine, dallarına ve staging alanına dokunulmaz, proje hiç git deposu olmasa da çalışır.

Alınan: mekanizma (`track` + `restore`). Alınmayan: yama/diff/budama (opencode'da 800 satır).
`IWorkspaceSnapshot` (Application) · `GitWorkspaceSnapshot` (Infrastructure). Her `implement` turundan
**önce** hâl kaydedilir; tanıtıcı `Phase.Snapshot`'ta taşınır — **SQL değişikliği yok**, faz gövdesiyle
birlikte `run_phase.data` JSON'una biner (sorgulanmıyor).

**Geri alma kendiliğinden ASLA olmaz:** yarım iş çoğu zaman doğruya yakındır ve red geri bildirimi
"şunu düzelt" der, "baştan yap" demez. Yalnız red tavanı sorusunda, yalnız kaydedilmiş bir hâl varsa
dördüncü seçenek olarak çıkar (`choice: revert`) ve yalnız kullanıcı seçerse koşar. Dönüş başarısızsa
ajana **"dizin dönmedi, kendin kontrol et"** notu gider: sessiz yanlış bilgilendirme olmaz.

### 3. Ziyaretçi turu zenginleşti (kullanıcı kararı)

Ofiste 9 masa, 10 ajan var. Karar: **masa eklenmedi**, masasız ajanın hayatı zenginleştirildi —
kapıdan girer, kahve/pano/su/pencere duraklarından karışık sırayla 2–3'ünü gezer, içeceğini alıp
taşır, çıkarken elini boşaltır. Başka bir ofisten uğramış gibi. Ayrıntı ve tuzağı `docs/SCENE.md`.

### 4. Limit beklemesi yanlış okunuyordu

Ölçüm koşusu sırasında yakalandı: haftalık kota 3 gün sonrasına sıfırlanıyordu ama mesaj yalnız
**"07:00'de sürer"** diyordu — bugün sanılıyor. `ResumeText.For` artık bugün değilse tarihi de yazar.

### Ölçülemedi — Anthropic haftalık kotası doldu

`tam-kadro` koşusu başlatıldı (`20260922-052033-c45c`) ama ilk turda limit koruması devreye girdi:
`weekly_scoped` %99, sıfırlanma **2026-09-25T04:00Z**. Çalışma `Paused` duruyor ve pencere açılınca
kendiliğinden sürer. **Bu yüzden üç ölçüm de bekliyor:**

1. tasarım kapısı düzeltmesi gerçek koşuda yakınsıyor mu,
2. compact gerçek red döngüsünde ne kazandırıyor (hâlâ yalnız sentetik ölçüldü),
3. `SystemPromptModes`'un ikinci veri noktası (n=1'de kazanç **sıfır** ölçülmüştü).

Kod tarafı bitti ve testlerle bağlandı: **38 birim + 98 servis + 47 Python** (main ile birleştikten sonra, Windows), `verify.ps1` tüm
adımlarda geçiyor.

## Ekip sıfırlandı: tek kişilik dev kadro (2026-09-23, kullanıcı kararı) ✅

Canlı `config/`'ta yalnız bir ajan ve iki ek bilgi kaldı: `tek-kisilik-dev-kadro` (eski `solo`'nun Claude Code
gibi çalışan hâli; analist + developer, `office_roles: [pm, dev]`) ve `backend-developer` (kurumun referans
backend deseni) + `frontend-developer-nuxt` (referans Nuxt panel deseni + `kurallar.py` denetimi grep olarak).
Kurum ve ürün adları yer tutucudur (`<Urun>`, `<Kok>`): yerel pre-commit koruması kurum izini reddeder, ajan gerçek adları hedef koddan okur.
`default` akışı iki adım (analiz → geliştirme), `handoffRole: null`. Sahnede tek ajan.

**Eski ekip silinmedi, taşındı:** 10 ajan, 5 akış, 5 bilgi dosyası `tests/MrHobist.AITeam.ServiceTests/Fixtures/config/`
altında testlerin sabit ekibi oldu — `StorageFixture` artık canlı `config/`'u değil bunu kopyalar. Sebep: servis
testleri manager/tam-kadro/tasarimli'ye bağlıydı; kullanıcı ekibi değiştirdikçe testler kırılmamalı. Canlı config
için ayrı `LiveConfigTests`: yalnız çözülebilirliği denetler (akış rolleri ve `includes` var mı), içeriği değil.
Tam yedek ayrıca `data/backup/config-20260923-111925/` (git dışı).

**Veri de temizlendi:** 11 çalışma ve 10 deneme projesi silindi (hedef dizinlerdeki dosyalara dokunulmadı);
öncesi `data/backup/aiteam-20260923-*.db`.

**Hook notu:** hedef projenin `.claude/settings.json` hook'ları ofis ajanlarında çalışmaz (runtime `setting_sources`
vermiyor); bu yüzden frontend bilgisi denetimi ajanın kendisinin koşacağı tek `grep` olarak taşır.

**Doğrulama:** 38 birim + 99 servis testi (yeni `LiveConfigTests` dahil) geçti; Api canlı config'le hatasız kalkıyor.

