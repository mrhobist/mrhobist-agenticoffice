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

## Faz 5 — Task.Api + SSE

`Task.Api`: `POST /api/v1/jobs/run-pipeline` (kuyruğu işler), `JobRunResult` döner.
`Api`: `POST /api/v1/runs` → 202 + `runId`, `GET /api/v1/runs/{id}/events` → SSE.
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
| `__main__.py doctor/run` | ortam kontrolü, CLI çalıştırma, özet | `Task.Api` `POST jobs/run-pipeline`; doctor → `/health/ready` + `GET /v1/models` |

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
Faz 5 (Task.Api iş ucu, `POST /api/v1/runs` → 202, SSE; `RunService` → `SceneEventBus`).
Faz 6 sahnesi hazır: `agent.state`, `meet`, `board.*`, `run.stage` olayları `RunService`'in
yayımlayacağı sözleşmedir (`docs/SCENE.md`).

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
