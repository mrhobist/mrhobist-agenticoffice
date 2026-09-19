# `errorCode` → anlam

Sunucu Problem Details gövdesinde `errorCode` döner; UI metni `ui/app/api/errors.ts` içinde
Türkçe'ye eşler. `title`/`detail` ekrana basılmaz. Kodlar yayından sonra yeniden adlandırılmaz.
Kaynak: `src/MrHobist.AITeam.Domain/ErrorCodes.cs`.

| Kod | HTTP | Anlam |
|---|---|---|
| `agent.invalid_key` | 400 | Ajan anahtarı `[a-z0-9][a-z0-9_-]*` değil |
| `agent.not_found` | 404 | Böyle bir ajan md'si yok |
| `agent.prompt_empty` | 400 | Gövde boş; sistem promptu olmadan rol çalışamaz |
| `agent.invalid_provider` | 400 | `provider` `anthropic / nvidia / ollama` dışında (`claude` kabul edilmez) |
| `agent.unknown_include` | 400 | `includes` içinde `config/knowledge/` altında olmayan dosya |
| `agent.unknown_can_ask` | 400 | `canAsk` var olmayan bir ajanı gösteriyor |
| `agent.exists` | 409 | `POST /agents`: bu anahtarla md zaten var |
| `agent.in_use` | 409 | `DELETE /agents`: ajan bir iş akışında (`role`/`handoffRole`) ya da bir `canAsk`'ta geçiyor |
| `knowledge.invalid_key` | 500 | `config/knowledge/` altında dosya adı `[a-z0-9][a-z0-9_-]*` değil (config bozuk) |
| `workflow.analyze_count` | 400 | Tam olarak bir `analyze` adımı olmalı |
| `workflow.analyze_first` | 400 | `analyze` ilk sırada olmalı |
| `workflow.no_implement` | 400 | En az bir `implement` adımı olmalı |
| `workflow.review_before_implement` | 400 | `review` adımının önünde `implement` yok |
| `workflow.rounds_min` | 400 | `maxReviewRounds` en az 1 |
| `workflow.duplicate_stage` | 400 | Yinelenen adım kimliği |
| `workflow.invalid_stage` | 400 | Geçersiz `kind`, `officeRole`, boş `role` ya da boş liste |
| `workflow.unknown_role` | 400 | `role` ya da `handoffRole` ekipte tanımlı bir ajan değil |
| `workflow.not_found` | 404 | Böyle bir iş akışı dosyası yok |
| `workflow.default_protected` | 409 | `default` akışı silinemez |
| `run.policy_violation` | 400 | Bir rolün hedefi çalışmanın hassasiyetine aykırı; çalışma başlamadı |
| `run.budget_exceeded` | — | Bütçe tavanı aşıldı; çalışma durduruldu (çalışma durumu) |
| `run.not_found` | 404 | Böyle bir çalışma yok |
| `config.file_missing` | 404/500 | Yapılandırma dosyası yok |
| `config.file_invalid` | 500 | Yapılandırma dosyası geçersiz JSON |
| `runtime.unavailable` | 503 | Python runtime'a ulaşılamıyor |
| `scene.command.type_missing` | 400 | Sahne komutunda `type` yok |
| `scene.command.type_unknown` | 400 | Bilinmeyen sahne olayı türü |
| `scene.command.data_missing` | 400 | Sahne komutunda `data` nesnesi yok |
| `scene.command.body_invalid` | 400 | Sahne komutu gövdesi geçerli JSON/UTF-8 değil |
