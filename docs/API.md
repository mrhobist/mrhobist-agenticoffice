# HTTP sözleşmesi

UI'ın okuyacağı tek dosya. Makine sözleşmesi `http://127.0.0.1:5080/openapi/v1.json`;
frontend tipleri `npm run gen:api` ile üretilir (CLAUDE.md §5). Burada **anlam** yazılır.

Tüm hatalar Problem Details + `errorCode` (bkz. `docs/error-codes.md`); `title`/`detail`
ekrana basılmaz, `errorCode` Türkçe metne eşlenir. Geçersiz gövde **400** döner, sessiz kabul yok.
Enum'lar JSON'da **adıyla** taşınır. Hostlar yalnız `127.0.0.1`.

| Host | Port | Sorumluluk |
|---|---|---|
| Api | 5080 | okuma, yapılandırma yazma, SSE yayını, kuyruğa koyma |
| Task.Api | 5081 | çalışmayı fiilen yürütür (`runs/` JSONL'e yazan tek süreç) |
| runtime (Python) | 5090 | yalnız LLM çağrısı; UI doğrudan konuşmaz |

## Ajanlar — `config/agents/*.md`

Bir ajan bir markdown dosyasıdır: YAML frontmatter üstveri, gövde sistem promptu.

| Uç | Dönen | Not |
|---|---|---|
| `GET /api/v1/agents` | `AgentListItem[]` | frontmatter özeti; prompt yok |
| `GET /api/v1/agents/{key}` | `AgentDetail` | `prompt` (gövde) + `composedPrompt` (gövde + alt md'ler; modele giden metin) |
| `PUT /api/v1/agents/{key}` | `AgentDetail` | **tüm alanlar zorunlu**, kısmi güncelleme yok: eksik/null liste ya da metin → 400 `request.invalid`; `provider`/`model`/`canAsk` için `null` = "yok / varsayılan". Dosya atomik yazılır; doğrulama hataları 400 |
| `GET /api/v1/knowledge` | `KnowledgeItem[]` | alt md'ler: `key, title, body` |

```jsonc
// AgentListItem
{ "key": "developer", "name": "Developer", "summary": "…",
  "officeRoles": ["dev"], "provider": "nvidia", "model": "z-ai/glm-5.3",
  "includes": ["mimari-kurallar", "kodlama-standartlari"], "canAsk": "manager" }

// AgentDetail = AgentListItem + { "prompt": "…", "composedPrompt": "…" }
// PUT gövdesi = AgentDetail eksi composedPrompt (key yoldan gelir)
```

- `provider`: `anthropic | nvidia | ollama` ya da `null` (varsayılan kullanılır). `claude` **kabul
  edilmez**, 400 `agent.invalid_provider` — sessizce çevrilmez.
- `model`: serbest metin ya da `null` (sağlayıcı varsayılanı). Erişilebilirlik `GET /api/v1/models`
  ile ayrıca doğrulanır; katalogda görünmek erişilebilir olmak değildir (LESSONS).
- `includes[]`: her biri `config/knowledge/` içinde var olmalı → yoksa 400 `agent.unknown_include`.
- `canAsk`: var olan bir ajan anahtarı ya da `null` → yoksa 400 `agent.unknown_can_ask`.
- `prompt` boş olamaz → 400 `agent.prompt_empty`.
- `key` yalnız `[a-z0-9][a-z0-9_-]*`; bilinmeyen anahtar 404 `agent.not_found`.

## Modeller

| Uç | Dönen | Not |
|---|---|---|
| `GET /api/v1/models?provider=nvidia` | `ModelInfo[]` `{ provider, model, reachable, detail }` | runtime `/v1/models`'a vekâlet eder; runtime kapalıysa **503** `runtime.unavailable` |

## İş akışı — `config/workflow.json`

| Uç | Dönen | Not |
|---|---|---|
| `GET /api/v1/workflow` | `Workflow` `{ maxReviewRounds, stages[] }` | dosya olduğu gibi |
| `PUT /api/v1/workflow` | `Workflow` | değişmezler tutmazsa 400: tam 1 `analyze` ve ilk sırada (`workflow.analyze_count`, `workflow.analyze_first`), ≥1 `implement` (`workflow.no_implement`), `review` öncesinde `implement` (`workflow.review_before_implement`), `maxReviewRounds ≥ 1` (`workflow.rounds_min`), yinelenen adım (`workflow.duplicate_stage`), bilinmeyen `kind` / `officeRole` / `role` (`workflow.invalid_stage`) |

`stage.kind`: `analyze | design | implement | review | handoff`. `stage.role` bir ajan anahtarıdır.

## Sahne (mevcut)

`GET /api/v1/scene`, `GET /api/v1/scene/events` (SSE), `POST /api/v1/scene/commands` — bkz.
`docs/SCENE.md`. Faz 5'te `RunService` aynı olayları yayımlar.

## Çalışmalar (Faz 5, henüz yok)

| Uç | Dönen |
|---|---|
| `POST /api/v1/runs` `{ brief, sensitivity, label? }` | **202** `{ runId }` |
| `GET /api/v1/runs` | `RunSummary[]` |
| `GET /api/v1/runs/{id}` | `RunSummary` + görevler + fazlar |
| `GET /api/v1/runs/{id}/events` | SSE: faz/tur/mesaj kayıtları sırayla |
| Task.Api `POST /api/v1/jobs/run-pipeline` | `JobRunResult` |
