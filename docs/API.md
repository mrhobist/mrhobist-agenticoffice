# HTTP sözleşmesi

UI'ın okuyacağı tek dosya. Makine sözleşmesi `http://127.0.0.1:5080/openapi/v1.json`;
frontend tipleri `npm run gen:api` ile üretilir (CLAUDE.md §5). Burada **anlam** yazılır.

Tüm hatalar Problem Details + `errorCode` (bkz. `docs/error-codes.md`); `title`/`detail`
ekrana basılmaz, `errorCode` Türkçe metne eşlenir. Geçersiz gövde **400** döner, sessiz kabul yok.
Enum'lar JSON'da **adıyla** taşınır. Hostlar yalnız `127.0.0.1`.

| Host | Port | Sorumluluk |
|---|---|---|
| Api | 5080 | tüm uçlar, SSE yayını, iş kanalı (veritabanına yazan tek yazıcı) |
| runtime (Python) | 5090 | yalnız LLM çağrısı ve sağlayıcı kimliği; UI doğrudan konuşmaz |

## Ajanlar — `config/agents/*.md`

Bir ajan bir markdown dosyasıdır: YAML frontmatter üstveri, gövde sistem promptu.

| Uç | Dönen | Not |
|---|---|---|
| `GET /api/v1/agents` | `AgentListItem[]` | frontmatter özeti; prompt yok |
| `GET /api/v1/agents/{key}` | `AgentDetail` | `prompt` (gövde) + `composedPrompt` (gövde + alt md'ler; modele giden metin) |
| `PUT /api/v1/agents/{key}` | `AgentDetail` | **tüm alanlar zorunlu**, kısmi güncelleme yok: eksik/null liste ya da metin → 400 `request.invalid`; `provider`/`model`/`canAsk` için `null` = "yok / varsayılan". Dosya atomik yazılır; doğrulama hataları 400 |
| `GET /api/v1/knowledge` | `KnowledgeItem[]` | alt md'ler: `key, title, body` |
| `PUT /api/v1/knowledge/{key}` `{ title?, body }` | `KnowledgeItem` | oluşturur ya da üzerine yazar; boş gövde 400 `knowledge.body_empty` |
| `POST /api/v1/knowledge/import` `{ key, markdown }` | **201** `KnowledgeItem` | yüklenen md: frontmatter `title` yoksa ilk `# Başlık`, o da yoksa anahtar |
| `DELETE /api/v1/knowledge/{key}` | **204** | bir ajanın `includes`'inde ise 409 `knowledge.in_use`; yoksa 404 `knowledge.not_found` |
| `POST /api/v1/agents/import` `{ key, markdown }` | **201** `AgentDetail` | hazır ajan md'si (frontmatter + prompt); bozuk biçim 400 `agent.markdown_invalid`; var olan anahtar 409 `agent.exists`; sahneye yerleşir |
| `GET /api/v1/agents/{key}/work?runs=30` | `AgentRunWork[]` `{ run: RunSummary, turns: Turn[], messages: Message[], phases: Phase[] }` | Ajan panelinin **İşler** sekmesi: son N çalışmada bu ajanın her LLM turu (tam gönderilen metin ve çıktı, o anki sağlayıcı/model), ona gelen/giden notlar (devir, hata, tekrar), faz geçişleri. Payı olmayan çalışma listede yoktur |

```jsonc
// AgentListItem
{ "key": "developer", "name": "Developer", "summary": "…",
  "officeRoles": ["dev"], "provider": "anthropic", "model": "claude-opus-5", "effort": "high",
  "includes": ["mimari-kurallar", "kodlama-standartlari"], "canAsk": "manager" }

// AgentDetail = AgentListItem + { "prompt": "…", "composedPrompt": "…" }
// PUT gövdesi = AgentDetail eksi composedPrompt (key yoldan gelir)
```

- `provider`: `anthropic | nvidia | ollama` ya da `null` (varsayılan kullanılır). `claude` **kabul
  edilmez**, 400 `agent.invalid_provider` — sessizce çevrilmez.
- `model`: serbest metin ya da `null` (sağlayıcı varsayılanı: Anthropic için `claude-opus-5`). Erişilebilirlik
  `GET /api/v1/models` ile ayrıca doğrulanır; katalogda görünmek erişilebilir olmak değildir (LESSONS).
- `effort`: `low | medium | high | max` ya da `null` (varsayılan `high`). Başka değer 400 `agent.invalid_effort`.
  Frontmatter anahtarı `effort`.
- `includes[]`: her biri `config/knowledge/` içinde var olmalı → yoksa 400 `agent.unknown_include`.
- `canAsk`: var olan bir ajan anahtarı ya da `null` → yoksa 400 `agent.unknown_can_ask`.
- `prompt` boş olamaz → 400 `agent.prompt_empty`.
- `key` yalnız `[a-z0-9][a-z0-9_-]*`; bilinmeyen anahtar 404 `agent.not_found`.

## Modeller

| Uç | Dönen | Not |
|---|---|---|
| `GET /api/v1/models?provider=nvidia` | `ModelInfo[]` `{ provider, model, reachable, detail }` | runtime `/v1/models`'a vekâlet eder; runtime kapalıysa **503** `runtime.unavailable` |

### Ekibe ajan ekleme / çıkarma

| Uç | Dönen | Not |
|---|---|---|
| `POST /api/v1/agents` | **201** `AgentDetail` | gövde = PUT gövdesi + `key`; `config/agents/{key}.md` oluşturulur. Var olan anahtar → **409** `agent.exists` |
| `DELETE /api/v1/agents/{key}` | **204** | md silinir. Bir iş akışında `role`/`handoffRole` olarak ya da başka ajanın `canAsk`'ında geçiyorsa **409** `agent.in_use` (önce oradan çıkarılır) |

Ekip **açıktır**: zorunlu rol yoktur; hangi ajanların çalışacağını iş akışı belirler. Sahnede yeri
(`config/scene.json` → `agents[]`) olmayan yeni ajan UI'da boş bir masaya yerleştirilir.

## İş akışları — `config/workflows/{key}.json`

Birden çok akış tutulur; `default` her zaman vardır ve silinemez. Bir çalışma başlatılırken akış
seçilir (`POST /runs { workflow }`), seçilmezse `default` kullanılır. Adımlar sırayla çalışır;
`handoffRole` verilmişse o ajan **her adım geçişinde** devir notu üretir (organizatör).

İki karar **akış düzeyindedir** (2026-09-21), ajan düzeyinde değil — çünkü aynı ajan farklı
akışlarda farklı davranmalıdır:

| Alan | Değerler | Anlamı |
|---|---|---|
| `planApprover` | yok/`null` ya da `"user"` · ajan anahtarı | Analistin planını kim onaylar. Ajan seçilirse kullanıcıya sorulmaz: ajan reddederse geri bildirim revize notu olur ve analiz yeniden koşar (`maxReviewRounds` turdan sonra karar kullanıcıya düşer) |
| `askRole` | yok/`null` · `"user"` · ajan anahtarı | Takılan ajanın sorusunu kim cevaplar. `null` = ajanın kendi `can_ask`'i (eski davranış); `"user"` = doğrudan kullanıcı. Manager'ı olmayan bir akışın manager'ı (ve maliyetini) işe sokmaması için vardır |

| Uç | Dönen | Not |
|---|---|---|
| `GET /api/v1/workflows` | `WorkflowListItem[]` `{ key, title, isDefault, stageCount, roles[] }` | `roles` = adımlarda + `handoffRole`'de geçen ajanlar |
| `GET /api/v1/workflows/{key}` | `Workflow` | bilinmeyen anahtar 404 `workflow.not_found` |
| `PUT /api/v1/workflows/{key}` | `Workflow` | **upsert**: yoksa oluşturur. Gövdede `key` yoktur (yoldan gelir). Değişmezler tutmazsa 400 (aşağıda); dosya atomik yazılır, `_comment` korunur |
| `DELETE /api/v1/workflows/{key}` | **204** | `default` → **409** `workflow.default_protected` |

```jsonc
// Workflow
{ "title": "Varsayılan", "maxReviewRounds": 3, "handoffRole": "organizer",
  "planApprover": "user",   // plan kullanıcıya sorulur (varsayılan)
  "askRole": null,          // takılan ajan kendi can_ask hedefine sorar
  "stages": [
    { "id": "analiz",     "title": "Analiz",     "kind": "analyze",   "role": "analyst",   "officeRole": "pm",   "description": "…" },
    { "id": "gelistirme", "title": "Geliştirme", "kind": "implement", "role": "developer", "officeRole": "dev",  "description": "…" },
    { "id": "test",       "title": "Test",       "kind": "review",    "role": "tester",    "officeRole": "qa",   "description": "…" },
    { "id": "karar",      "title": "Karar",      "kind": "review",    "role": "manager",   "officeRole": "gate", "description": "Altı şapka ile son onay" }
  ] }
```

Değişmezler (400): tam 1 `analyze` ve ilk sırada (`workflow.analyze_count`, `workflow.analyze_first`),
≥1 `implement` (`workflow.no_implement`), `review` öncesinde bir **üretici** adım — `design` ya da
`implement` (`workflow.review_before_implement`),
`maxReviewRounds ≥ 1` (`workflow.rounds_min`), yinelenen adım (`workflow.duplicate_stage`), geçersiz
`kind` / `officeRole` / boş `role` (`workflow.invalid_stage`), `role` ya da `handoffRole` ekipte yok
(`workflow.unknown_role`).

- `stage.kind`: `analyze | design | implement | review | handoff`. `stage.role` bir ajan anahtarıdır.
- `stage.officeRole` (sahnedeki karakter tipi): `pm | arch | dev | qa | ops | res | gate | designer`.
- `handoffRole`: ajan anahtarı ya da `null` (devir notu yok).
- Sahne olayı `workflow.set { key }`: pano sütunları o akışa göre yeniden kurulur (Faz 5'te çalışma başlarken yayımlanır).
- Sahne olayı `scene.reload { reason? }`: `config/scene.json` değişti, UI sahneyi yeniden kurar. `POST/PUT/DELETE /agents` bunu yayımlar (yeni ajan sahneye yerleşir: boş sprite + boş masa, yoksa ziyaretçi — docs/SCENE.md).

## Sahne (mevcut)

`GET /api/v1/scene`, `GET /api/v1/scene/events` (SSE), `POST /api/v1/scene/commands` — bkz.
`docs/SCENE.md`. Faz 5'te `RunService` aynı olayları yayımlar.

## Giriş

Tüm `/api/v1/*` uçları `Authorization: Bearer <jwt>` ister; `auth/login`, `jobs/health` ve `OPTIONS` hariç.
Kimliksiz istek **401 `auth.required`**. SSE yalnız `scene/events?access_token=<jwt>` ile.

| Uç | Dönen | Not |
|---|---|---|
| `POST /api/v1/auth/login` `{ username, password }` | `LoginResponse` `{ token, expiresAt, user: { id, name, role } }` | Bugün gömülü `admin/admin` (`docs/DOMAIN.md` → Giriş). Yanlışsa **401 `auth.invalid_credentials`** |
| `GET /api/v1/auth/me` | `UserInfo` `{ id, name, role }` | Belirtecin claim'lerinden |

## Sağlayıcı kimliği

| Uç | Dönen | Not |
|---|---|---|
| `GET /api/v1/providers?refresh=` | `ProviderStatus[]` `{ provider, loggedIn, account, detail, models: ModelInfo[], method: session\|apikey\|null }` | runtime `/v1/auth` + `/v1/models`'a vekâlet eder; runtime kapalıysa **503** `runtime.unavailable`. UI **ilk yüklemede** çağırır; `loggedIn=false` ise giriş talimatı gösterir (`docs/DOMAIN.md` → Model, efor ve kimlik). Runtime kimliği 60 s önbellekler; `refresh=true` ("Yeniden kontrol et") önbelleği atlar |
| `POST /api/v1/providers/{provider}/login` `ProviderLoginRequest { mode?: claudeai\|console\|chatgpt\|apikey, email?, apiKey? }` | `LoginStarted` `{ provider, started, detail }` | Runtime, sağlayıcının **kendi** giriş akışını kullanıcının makinesinde başlatır (Anthropic `claudeai`/`console`: `claude auth login`; OpenAI `chatgpt`: `codex login`; yeni konsol + tarayıcı). Şifre/token bu uçtan geçmez. **`apikey`** istisna: anahtar runtime'a iletilir, runtime sağlayıcıda doğrular (`GET /v1/models`, token harcamaz) ve kullanıcı profiline yazar; Api saklamaz, günlüklemez; `started=true` = doğrulandı ve kaydedildi, `detail` maskeli son. Tamamlanma `GET /providers?refresh=true` ile görülür |
| `POST /api/v1/providers/{provider}/logout` | `ProviderStatus`'un kimlik kısmı `{ provider, loggedIn, account, detail, method }` | Kayıtlı API anahtarı varsa **önce o silinir** (CLI oturumuna dönülür); yoksa Anthropic `claude auth logout`, OpenAI `codex logout`. Ortam değişkeninden gelen anahtar silinemez, `detail` söyler |
| `GET /api/v1/limits?refresh=` | `ProviderLimits[]` `{ provider, available, detail, subscription, fetchedAt, limits: [{ kind, group, percent, severity, resetsAt, scope, isActive }] }` | **Kota kullanımı** (üst bar): sağlayıcının kota pencereleri; `percent` kullanılan yüzde — halka bunu yazar (2026-09-20 kullanıcı kararı). `scope` üst uçtan **nesne** gelir (`{model:{display_name,id}, surface}`), runtime adı çıkarır; düz string biçimi de kabul edilir. Anthropic: Claude Code'un `/usage` ekranının okuduğu uç, CLI'nin makinede sakladığı oturumla; belirteç hiçbir yanıta yazılmaz. Runtime 90 s önbellekler. Sağlayıcı vermiyorsa ya da yanıt ayrıştırılamıyorsa `available=false` + neden (runtime **500 dönmez**). Runtime'ın kendisi 5xx dönerse Api **502** `runtime.error` (kapalı değil, uç bozuk); ulaşılamıyorsa 503 `runtime.unavailable` |
| `GET /api/v1/usage?runs=200` | `UsageItem[]` `{ provider, model, turns, runs, inputTokens, outputTokens, costUsd, lastAt }` | Son N çalışmanın tur kayıtlarından toplanır. **Abonelik limiti / kalan kota değil**: sağlayıcı bunu CLI'a açmıyor; UI bunu söyler |

## Projeler — `project` tablosu

İş yalnız bir projenin içinde başlar (`docs/DOMAIN.md` → Projeler).

| Uç | Dönen | Not |
|---|---|---|
| `GET /api/v1/projects` | `ProjectCard[]` `{ key, title, description, workflow, targetDir, ownerId, createdAt, runs, running, awaitingApproval, paused, failed, completed, totalCostUsd, lastActivityAt }` | oluşturma sırasına göre |
| `POST /api/v1/projects` `{ key, title, description, workflow, targetDir, color?, maxCostUsd?, maxTokens? }` | **201** `ProjectCard` | `maxCostUsd`/`maxTokens` **proje bütçesi** (2026-09-22): `null` = sınırsız (varsayılan), pozitif olmalı yoksa 400 `project.budget_invalid`. `workflow` null → `default`; `targetDir` null → `projects/{key}`; 409 `project.exists`. **Alanlar `null` olabilir ama atlanamaz**: `description`/`workflow`/`targetDir` gövdede hiç yoksa gövde çözülmez → 400 `request.invalid` (kurucu parametresi varsayılansız; `RespectRequiredConstructorParameters`). Yalnız `color` atlanabilir |
| `GET /api/v1/projects/{key}` | `ProjectCard` | 404 `project.not_found` |
| `PUT /api/v1/projects/{key}` `{ title, description?, workflow?, targetDir? }` | `ProjectCard` | tüm alanlar taşınır |
| `DELETE /api/v1/projects/{key}?deleteFiles=false` | `ProjectDeleteResult` `{ key, targetDir, runsDeleted, filesDeleted }` | süren çalışma varsa 409 `project.in_use`; bitmiş geçmiş projeyle silinir; `deleteFiles=true` hedef dizini de siler |
| `GET /api/v1/projects/dirs?path=` | `DirectoryListing` `{ path, parent, dirs: [{ name, path }] }` | klasör seçici; depo köküne göre, gizli/üretilen klasörler yok; dışarı çıkan yol 400 `project.target_dir_invalid` |
| `GET /api/v1/projects/{key}/runs?limit=50` | `RunSummary[]` | `GET /runs?project={key}` ile aynı |
| `POST /api/v1/projects/reorder` `{ keys: [] }` | `ProjectCard[]` | Verilen anahtarlar 0..n sırasını alır, kalanlar arkaya; ray ve Kanban bu sırayı okur. `ProjectCard.color` / `order` alanları sona eklendi; `POST/PUT` gövdesinde `color?` (`#rrggbb`, değilse 400 `project.invalid_color`) |
| `POST /api/v1/projects/{key}/launch` | **202** `LaunchResult` `{ key, processId, launcher }` | Kökteki `run.cmd` yeni konsolda (docs/DOMAIN.md → Projeyi başlatma). Yoksa 404 `project.launch_missing`; koşamazsa 400 `project.launch_failed`. `ProjectCard.launchable` düğmenin durumu |

## Çalışmalar — `run` ve alt tabloları

Davranış `docs/DOMAIN.md` (yaşam döngüsü, plan onayı, dağıtım). Yazma uçları hızlı doğrular (400/409 hemen),
uzun işi (analiz, dağıtım) Api içindeki sıralı iş kanalına bırakır ve **202** döner; ilerleme `GET /runs/{id}` ile izlenir.

| Uç | Dönen | Not |
|---|---|---|
| `POST /api/v1/runs` `{ project, brief, workflow?, sensitivity?, label?, maxCostUsd? }` | **202** `RunSummary` | `project` zorunlu (400 `run.project_required`, 404 `project.not_found`); `workflow` yoksa projenin varsayılanı; `sensitivity` yoksa `anthropic`; boş `brief` 400 `run.brief_empty` |
| `GET /api/v1/runs?limit=20&project=` | `RunSummary[]` | yeni → eski; `project` ile süzülür |
| `GET /api/v1/runs/{id}` | `RunDetail` | 404 `run.not_found` |
| `GET /api/v1/runs/{id}/turns?agent=` | `Turn[]` | Tüm ajanların LLM turları, **tam prompt ve çıktı** ile, sırasıyla (`run_turn`). Günlük ekranı |
| `POST /api/v1/runs/{id}/approve` | **202** `RunSummary` | yalnız `AwaitingApproval`; değilse 409 `run.not_awaiting_approval` |
| `POST /api/v1/runs/{id}/revise` `{ note }` | **202** `RunSummary` | boş `note` 400 `run.note_empty`; 409 gibi yukarıda |
| `POST /api/v1/runs/{id}/answer` `{ choice, note? }` | **202** `RunSummary` | yalnız `awaitingInput` (409 `run.not_awaiting_input`); `choice` sorunun `options[].id`'lerinden biri (`retry \| skip \| cancel`; değilse 400 `run.invalid_choice`); seçenek `needsNote` ise boş not 400 `run.note_empty`. `Running` dönerse dağıtım kuyruğa girer (docs/DOMAIN.md → Takılma) |
| `POST /api/v1/runs/{id}/retry` | **202** `RunSummary` | yalnız `failed \| interrupted \| budgetExceeded \| cancelled` ve limit beklemesi (`paused` + `resumeAt`); değilse 409 `run.not_retryable`. Kaldığı adımdan sürer (plan yoksa/analizde düştüyse analiz, yoksa dağıtım); onay beklerken iptal edilmişse yalnız `awaitingApproval`'a döner, kuyruğa iş girmez |
| `POST /api/v1/runs/{id}/cancel` | **200** `RunSummary` | yalnız `running \| awaitingApproval \| paused \| failed \| interrupted \| budgetExceeded \| awaitingInput`; değilse 409 `run.not_cancellable`. Düşen çalışmada anlamı **kapat**: karar verildi, gelen kutusundan düşer. Hemen yazılır; süren LLM çağrısının sonucu yazılmaz |
| `GET /api/v1/runs/overview` | `RunsOverview` | **İşler** ekranı ve üst bar: kaç iş var, kaçı ne durumda, kaçı kullanıcıdan bir şey bekliyor (`inbox`). UI 5 s'de bir yoklar |
| `GET /api/v1/jobs/health` | `{ status, pending }` | iş kanalında bekleyen iş sayısı (kimliksiz) |
| `POST /api/v1/progress/{token}` `{ tool, target? }` | **204** | Runtime'ın canlı araç bildirimi (kimliksiz; tek kullanımlık `token` yetkidir, tur bitince düşer). Api `agent.tool` sahne olayı yayımlar. Bilinmeyen belirteç sessizce 204 |
| `GET /api/v1/settings` · `PUT /api/v1/settings` `{ limitGuards: { anthropic: 99 } }` | `SettingsDto` | Limit koruması eşiği, sağlayıcı başına % (1–100; değilse 400 `settings.invalid`). `app_settings` |

`RunsOverview.awaitingInput`: takılıp seçim bekleyen çalışma sayısı (sona eklendi; `awaitingApproval` yalnız plan onayı).
`RunSummary` ek alanlar: `question` (`awaitingInput`'ta `{ ts, agent, text, options: [{ id, label, detail, needsNote }], task, stage, context }`),
`resumeAt` (limit beklemesi), `step` (`analyze | approval | dispatch`: kaldığı adım, "yeniden dene" buradan sürer),
`waitingSince` (`running` ama hazır görevin ajanı başka çalışmada dolu; sunucu bir adım kapanınca kendisi yeniden dağıtır). `Turn` ek alanlar: `toolUses: [{ tool, target }]`, `turns` (ajan döngüsünün iç tur sayısı).
`totalCostUsd` **eşdeğer** maliyettir (docs/DOMAIN.md → Bütçe ve limit).

```jsonc
// RunSummary — run.json
{ "id": "20260919-153000-a1b2", "label": "slugify", "brief": "…", "sensitivity": "anthropic",
  "workflow": "default", "status": "AwaitingApproval", "startedAt": "…", "finishedAt": null,
  "totalCostUsd": 0.0421, "detail": null }

// RunDetail = RunSummary + dondurulan akış + plan + faz/mesaj kayıtları
{ ...RunSummary,
  "workflowDef": { /* Workflow, calismayla birlikte dondurulan kopya */ },
  "spec": { "summary": "…", "architecture": "…", "rules": ["…"],
            "tasks": [{ "id": "t1", "title": "…", "description": "…", "files": ["…"], "acceptance": ["…"], "dependsOn": [] }] },
  "order": ["t1", "t2"],                       // topolojik yürütme sırası
  "tasks": [{ "id": "t1", "phases": [ /* Phase[] */ ] }],
  "messages": [ /* Message[]: plan notları, devir notları, sorular */ ] }

// RunsOverview — GET /runs/overview. failed = failed + interrupted + budgetExceeded + policyRejected
{ "total": 7, "running": 1, "awaitingApproval": 1, "paused": 1, "failed": 2, "completed": 1, "cancelled": 1,
  "inbox": [ // kullanıcıdan bir şey bekleyenler, yeni → eski (docs/DOMAIN.md → Gelen kutusu)
    { "runId": "…", "label": "slugify", "status": "awaitingApproval", "kind": "approval",
      "ts": "…", "title": "Plan onayı bekliyor", "detail": "<plan özeti>", "task": null },
    { "runId": "…", "label": "…", "status": "failed", "kind": "decision",
      "ts": "…", "title": "Başarısız oldu", "detail": "<run.detail>", "task": null } ] }
```

- Enum'lar HTTP'de **camelCase adıyla** (Api JSON seçenekleri; dosyada PascalCase): `status`:
  `running | completed | failed | interrupted | budgetExceeded | policyRejected | awaitingApproval | paused | cancelled`.
- `InboxKind` (adıyla): `approval` = soru, plan onayı (onayla / revize et) · `decision` = karar, çalışma durdu (yeniden dene / iptal) ·
  `question` = bir ajan kullanıcıya `ask` yazdı ve `ref`'i eşleşen `answer` yok (bugün üretilmiyor; sözleşme hazır).
- `Phase`: `{ ts, task, stage, stageTitle, kind, agent, round, status: started|done|rejected|failed|skipped, durationS, detail, cause? }`.
  `cause` (`agent | limit | cancelled | interrupted`, eski kayıtlarda yok): sistem kaynaklı `failed` fazlar tur sayılmaz.
- `Message`: `{ ts, kind: ask|answer|handoff|note, from, to, body, task, stage, ref, subject }`. Plan revize notu
  `from: "user", to: "analyst", kind: "note", subject: "plan-revision"`. Bir adım hata ile bitince
  `from: <ajan>, to: "user", kind: "note", subject: "error", body: <neden>` yazılır ("takıldı" tek başına bilgi değildir).
- `Turn`: `{ ts, agent, stage, task, round, provider, model, destination, durationS, promptChars, outputChars, costUsd, prompt, output, inputTokens, outputTokens, toolUses, turns, cacheReadTokens, cacheWriteTokens, toolsOffered, context }`.
  `context` (geçmiş taşınmadıysa `null`): `{ carriedMessages, carriedChars, keptMessages, keptChars, charsPerToken, calibrationSamples }` —
  sıkıştırma öncesi/sonrası; `calibrationSamples: 0` ise oran ölçülmemiş varsayılandır (DOMAIN.md → Bağlam bütçesi).
- SSE (`GET /runs/{id}/events`) **henüz yok**; UI aktif çalışmayı 2 s'de bir `GET /runs/{id}` ile yoklar — varsayımla ilerlenir.
