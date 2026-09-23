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
| `knowledge.not_found` | 404 | Böyle bir bilgi dosyası yok |
| `knowledge.in_use` | 409 | `DELETE /knowledge`: bir ajanın `includes`'inde geçiyor |
| `knowledge.body_empty` | 400 | Bilgi dosyası gövdesi boş |
| `agent.unknown_sprite` | 400 | Seçilen karakter `scene.json → sprites[]` listesinde yok |
| `agent.unknown_mcp` | 400 | `mcp` listesine kayıtlı olmayan bir MCP sunucusu eklendi |
| `agent.mcp_unsupported` | 400 | MCP yetkisi verilen ajanın sağlayıcısı MCP çalıştıramıyor (yalnız `anthropic` / varsayılan) |
| `mcp.invalid_key` | 400 | MCP anahtarı `[a-z0-9][a-z0-9_-]*` değil |
| `mcp.invalid` | 400 | stdio'da komut yok, http/sse'de mutlak http(s) adres yok, boş ya da yinelenen değişken/başlık adı |
| `mcp.not_found` | 404 | Böyle bir MCP sunucusu yok |
| `mcp.exists` | 409 | `POST /mcp`: bu anahtarla sunucu var |
| `mcp.oauth_unsupported` | 400 | OAuth yalnız uzak (http/sse) sunucularda |
| `mcp.oauth_discovery_failed` | 502 | Yetki sunucusu bulunamadı ya da ucu https değil |
| `mcp.oauth_client_required` | 400 | Sunucu dinamik istemci kaydı yapmıyor/reddetti; kullanıcı istemci kimliği girmeli |
| `mcp.oauth_state_invalid` | 400 | OAuth dönüşündeki giriş oturumu bilinmiyor ya da süresi doldu |
| `mcp.oauth_token_failed` | 502 | Yetkilendirme reddedildi ya da belirteç alınamadı |
| `mcp.catalog_not_found` | 404 | Katalogda böyle bir hazır sunucu ya da bağlantı yöntemi yok |
| `mcp.in_use` | 409 | `DELETE /mcp`: sunucu bir ajana yetkili; önce yetki kaldırılır |
| `attachment.empty` | 400 | Yüklenen dosya boş ya da istekte dosya yok |
| `attachment.too_large` | 400 | Dosya 20 MB'tan büyük |
| `attachment.type_unsupported` | 400 | İzinli uzantılardan biri değil (`GET /attachments/rules`) |
| `attachment.too_many` | 400 | Bir işe 10'dan fazla ek |
| `attachment.not_found` | 400/404 | `POST /runs`: geçici ek kimliği bilinmiyor ya da süresi doldu (400) · indirme: çalışmanın böyle bir eki yok (404) |
| `agent.markdown_invalid` | 400 | `POST /agents/import`: md frontmatter/gövde çözülemedi |
| `workflow.analyze_count` | 400 | Tam olarak bir `analyze` adımı olmalı |
| `workflow.analyze_first` | 400 | `analyze` ilk sırada olmalı |
| `workflow.no_implement` | 400 | En az bir `implement` adımı olmalı |
| `workflow.review_before_implement` | 400 | `review` adımının önünde **üretici** adım (`design` ya da `implement`) yok |
| `workflow.rounds_min` | 400 | `maxReviewRounds` en az 1 |
| `workflow.duplicate_stage` | 400 | Yinelenen adım kimliği |
| `workflow.invalid_stage` | 400 | Geçersiz `kind`, `officeRole`, boş `role` ya da boş liste |
| `workflow.unknown_role` | 400 | `role` ya da `handoffRole` ekipte tanımlı bir ajan değil |
| `workflow.not_found` | 404 | Böyle bir iş akışı dosyası yok |
| `workflow.default_protected` | 409 | `default` akışı silinemez |
| `agent.invalid_effort` | 400 | `effort` `low / medium / high / max` dışında |
| `run.policy_violation` | 400 | Bir rolün hedefi çalışmanın hassasiyetine aykırı; çalışma başlamadı |
| `run.budget_exceeded` | — | Bütçe tavanı aşıldı; çalışma durduruldu (çalışma durumu) |
| `run.not_found` | 404 | Böyle bir çalışma yok |
| `run.brief_empty` | 400 | `POST /runs`: `brief` boş |
| `run.note_empty` | 400 | `POST /runs/{id}/revise`: `note` boş |
| `run.not_awaiting_approval` | 409 | `approve`/`revise` yalnız `AwaitingApproval` durumunda geçerli |
| `run.not_retryable` | 409 | `retry` yalnız `Failed / Interrupted / BudgetExceeded / Cancelled` ve limit beklemesinde (`Paused` + `resumeAt`) geçerli |
| `run.not_cancellable` | 409 | `cancel` yalnız `Running / AwaitingApproval / Paused / Failed / Interrupted / BudgetExceeded` durumunda geçerli (Completed, Cancelled, PolicyRejected kapatılamaz) |
| `run.plan_invalid` | — | Analist çıktısı şemaya uymadı ya da görevsiz (çalışma `Failed`, `detail` söyler) |
| `run.budget_invalid` | 400 | `POST /runs`: `maxCostUsd` verildiyse sıfırdan büyük olmalı |
| `run.project_required` | 400 | `POST /runs`: `project` boş; iş yalnız bir projenin içinde başlar |
| `run.not_awaiting_input` | 409 | `answer` yalnız `AwaitingInput` durumunda geçerli |
| `run.invalid_choice` | 400 | `answer.choice` sorunun seçeneklerinden biri değil |
| `run.step_invalid` | — | Bir adımın (review) çıktısı şemaya uymadı; faz `Failed`, çalışma `Failed` |
| `settings.invalid` | 400 | Limit eşiği 1–100 dışında ya da bilinmeyen sağlayıcı |
| `auth.required` | 401 | `/api/v1/*` için giriş gerekli (`/auth/login` hariç) |
| `auth.invalid_credentials` | 401 | Kullanıcı adı ya da şifre yanlış |
| `project.invalid_key` | 400 | Proje anahtarı `[a-z0-9][a-z0-9_-]*` değil |
| `project.not_found` | 404 | Böyle bir proje yok |
| `project.exists` | 409 | `POST /projects`: bu anahtarla proje var |
| `project.in_use` | 409 | `DELETE /projects`: içinde **süren** çalışma var (running / awaitingApproval / paused / awaitingInput); önce bitir ya da iptal et |
| `project.title_empty` | 400 | Proje başlığı boş |
| `project.target_dir_invalid` | 400 | Hedef dizin depo içinde göreli bir yol değil (`..`, `/`, sürücü harfi) |
| `project.launch_missing` | 404 | `launch`: proje kökünde `run.cmd` yok |
| `project.launch_failed` | 400 | `launch`: başlatıcı süreç açılamadı |
| `project.invalid_color` | 400 | `color` `#rrggbb` değil |
| `project.budget_invalid` | Proje bütçesi sıfır ya da negatif verildi; sınırsız için alan boş bırakılır | 400 |
| `project.budget_exceeded` | Proje bütçesi dolu: yeni iş başlamaz, süren iş `BudgetExceeded` olur | 400 |
| `config.file_missing` | 404/500 | Yapılandırma dosyası yok |
| `config.file_invalid` | 500 | Yapılandırma dosyası geçersiz JSON |
| `runtime.unavailable` | 503 | Python runtime'a ulaşılamıyor |
| `runtime.error` | 502 | Runtime ayakta ama istenen uç 5xx döndü (kapalı değil, uç bozuk) |

Runtime'ın kendi kodları (`runtime/README.md`) Api'ye `runtime <kod>: mesaj` metniyle gelir, ayrı `errorCode` olmaz: `runtime.cli_missing`, `runtime.not_logged_in`, `runtime.provider_error`, `runtime.provider_unsupported`, `runtime.tools_unsupported` (OpenAI API anahtarı yolunda araçlı adım yok), `runtime.mcp_unsupported` (OpenAI sağlayıcısında MCP bağlanmadı).
| `scene.command.type_missing` | 400 | Sahne komutunda `type` yok |
| `scene.command.type_unknown` | 400 | Bilinmeyen sahne olayı türü |
| `scene.command.data_missing` | 400 | Sahne komutunda `data` nesnesi yok |
| `scene.command.body_invalid` | 400 | Sahne komutu gövdesi geçerli JSON/UTF-8 değil |
