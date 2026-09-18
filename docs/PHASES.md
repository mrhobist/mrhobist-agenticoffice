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

## Faz 1 — Domain + dosya deposu

`Domain`: `Agent`, `Knowledge`, `Workflow`, `Stage`, `Run`, `RunTask`, `Phase`, `Turn`,
`Message` + enum'lar + değişmezler. Hiçbir referans yok.
`Application/Abstractions`: `IAgentStore`, `IWorkflowStore`, `IRunStore`.
`Infrastructure/Storage`: md frontmatter okuma/yazma, JSONL append, atomik dosya yazımı.

**Biten sayılır:** `UnitTests` değişmezleri doğrular (geçersiz board reddi, eksik alt md,
döngülü bağımlılık patlamaz). `ServiceTests` geçici dizinde gidiş-dönüş kayıpsızlığını ve
yarım JSONL satırının yok sayıldığını kanıtlar.

## Faz 2 — Ajan ve iş akışı modülleri

`AgentService` (listele, oku, yaz, prompt kompozisyonu), `WorkflowService` (oku, doğrula, yaz).
Api uçları: `GET/PUT /api/v1/agents/{key}`, `GET /api/v1/knowledge`, `GET/PUT /api/v1/workflow`.
Problem Details + `errorCode`, OpenAPI + Scalar.

**Biten sayılır:** Arayüzsüz, `curl` ile bir ajan md'si okunur, değiştirilir, kaydedilir ve
bileşik prompt çıktısı değişir. Geçersiz gövde **400** döner (v1'deki sessiz kabul tuzağı).

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

**Kalan:** Ajanlar paneli (md editörü + LLM seçici + alt md listesi), İş Akışı paneli
(adım ekle/sil/sırala), `npm run gen:api` ile üretilen tipler (bugün `scene/contract.ts`
elle), büyük pano görünümü (tıklayınca), gerçek `RunService` olayları.

**Biten sayılır:** Tarayıcıda canlı bir çalışma izlenir; bir ajanın md'si panelden
değiştirilip kaydedilince sonraki çalışmada davranış gözle görülür biçimde değişir.
