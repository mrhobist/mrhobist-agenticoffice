# runtime — LLM çağrı katmanı

> **Python durum tutmaz, veritabanı görmez, iş kuralı bilmez.**
> Tek işi: `{systemPrompt, messages, provider, model, schema?}` alıp
> `{text, structured, usage, costUsd}` döndürmek. Faz 4d'den beri istek ajan araçlarını da
> taşır (`tools`, `cwd`, `maxTurns`, `progressUrl`) — ama **hangi adımın hangi aracı aldığı
> .NET'in kararıdır** (`ToolAccess.ForKind`); runtime yalnız iletir.

Bu kural pazarlığa kapalıdır (`../CLAUDE.md` §1). Orkestrasyon .NET tarafındadır.

Burada **olmayacak** şeyler: tur sayacı, faz bilgisi, iş akışı bilgisi, dosya yazma,
veritabanı, görev kavramı, role göre dallanma. `scripts/verify.ps1` bunu denetler.

## Çalıştırma

Depo kökünden (sanal ortam `runtime/.venv`, Python 3.12+):

```bash
runtime/.venv/Scripts/python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 5090 --app-dir runtime
```

Testler (`runtime/` içinden):

```bash
.venv/Scripts/python.exe -m pytest -q
```

### Sanal ortam cihaza bağlıdır

`.venv/Scripts/python.exe` bir **shim**'dir: onu kuran makinenin (ve Windows kullanıcısının)
`python.exe`'sini mutlak yolla çağırır. Aynı çalışma dizinini başka bir kullanıcı açtığında
dosya **durur ama çalışmaz** — `No Python at '...'`, çıkış kodu 103. `.venv` zaten
`.gitignore`'da; taşınabilir değildir, her cihazda yeniden kurulur:

```bash
powershell -ExecutionPolicy Bypass -File scripts/verify.ps1 -SetupRuntime
```

Bu komut sırayla `py -3`, `python`, `python3` deneyerek **bu cihazın** yorumlayıcısını bulur
(`sys.executable` sorulur, Microsoft Store kısayolu elenir), bozuk `.venv`'i siler ve
`pip install -e "runtime[dev]"` ile yeniden kurar. `verify.ps1` anahtarsız çalıştırıldığında
kurmaz; yorumlayıcıyı fiilen koşturarak yoklar ve bozuksa onarım komutunu söyleyerek durur.

## Sağlayıcılar

Bağlı olanlar: **`anthropic`** (Claude Agent SDK, paket `claude_agent_sdk`) ve **`openai`**
(Codex CLI ya da Responses API). `nvidia` ve `ollama` istekleri `501 runtime.provider_unsupported` döner.

### API anahtarı (her iki sağlayıcı)

Varsayılan kimlik CLI oturumudur; kullanıcı isterse Ayarlar'dan API anahtarı girer. Anahtar sağlayıcıda
doğrulanır (`GET /v1/models`) ve `%USERPROFILE%\.mrhobist-aiteam\credentials.json` dosyasına yazılır
(`app/credentials.py`; `AITEAM_CREDENTIALS_FILE` ile yer değişir — testler bunu kullanır). Öncelik:
`ANTHROPIC_API_KEY` / `OPENAI_API_KEY` ortam değişkeni > dosya > CLI oturumu. Anahtar hiçbir yanıta yazılmaz.

### OpenAI: ChatGPT aboneliği = Codex CLI oturumu

```bash
npm i -g @openai/codex
codex login
```

`codex` arama sırası: `CODEX_CLI_PATH` → `PATH` → `%APPDATA%\npm\codex.cmd`. Tur `codex exec --json`
ile koşar (`--ephemeral`, `-C cwd`, araçlıysa `--sandbox workspace-write`, değilse `read-only` + boş geçici
dizin, yapısal çıktı `--output-schema`). Kayıtlı API anahtarı varsa CLI yerine Responses API kullanılır;
o yolda araçlı istek `501 runtime.tools_unsupported` döner. Katalog `OPENAI_MODELS="a,b"` ile değişir.

### Anthropic kimliği = Claude Code oturumu

Ayrı `ANTHROPIC_API_KEY` **yoktur**. SDK, makinedeki `claude.exe` (Claude Code CLI)
oturumunu kullanır. Giriş yoksa:

```bash
claude login
```

Oturum **Windows kullanıcısına** bağlıdır: runtime'ı çalıştıran kullanıcı ile `claude login`
yapan kullanıcı aynı olmalıdır. Farklı kullanıcıyla açılan terminaldeki giriş görünmez.

`claude.exe` arama sırası:

1. `CLAUDE_CLI_PATH` ortam değişkeni (tam yol)
2. `%APPDATA%\Claude\claude-code\<sürüm>\claude.exe` — en yeni sürüm klasörü
3. SDK'nın kendi araması (`PATH`)

Model kataloğu sabittir (`claude-fable-5-1`, `claude-opus-5`, `claude-sonnet-5`,
`claude-haiku-4-5-20251001`); `reachable = giriş var`. Katalog için gerçek çağrı yapılmaz
(gerekçe: `docs/DOMAIN.md` → "Model, efor ve kimlik").

## Uçlar

| Uç | Ne yapar |
|---|---|
| `POST /v1/turn` | Tek bir LLM çağrısı yürütür ve sonucu döner |
| `GET /v1/models?provider=` | Modeller; `provider` yoksa tüm bağlı sağlayıcılar |
| `GET /v1/auth?provider=` | Kimlik durumu `[{provider, loggedIn, account, detail, method}]`; 60 sn önbellekli |
| `POST /v1/auth/login` | `{provider, mode, email?, apiKey?}` — CLI giriş akışını başlatır ya da (`apikey`) anahtarı doğrulayıp kaydeder |
| `POST /v1/auth/logout` | Kayıtlı anahtarı siler, yoksa CLI oturumunu kapatır |
| `GET /v1/limits?provider=` | Kalan kullanım (kota pencereleri); vermeyen sağlayıcıda `available=false` |
| `GET /health` | Süreç ayakta mı |

### `POST /v1/turn` sözleşmesi

Alan adları JSON'da camelCase (`app/contracts.py`, pydantic alias). Runtime durumsuzdur:
iki ardışık istek birbirini bilmez, geçmiş `messages` ile gelir.

**İstek (`TurnRequest`)**

| Alan | Zorunlu | Ne |
|---|---|---|
| `systemPrompt` | ✓ | Ajanın sistem promptu (gövde + `includes`; .NET birleştirir) |
| `messages[]` | ✓ | `{role, content}` — çalışmanın o ana kadarki geçmişi |
| `provider` | ✓ | `anthropic` \| `openai` (`nvidia`/`ollama` → 501) |
| `model` | ✓ | Katalogdaki model adı |
| `schema` | — | Verilirse sağlayıcının yapısal çıktı mekanizması kullanılır |
| `maxTokens` | — | Varsayılan 8192 |
| `reasoningEffort` | — | `low` \| `medium` \| `high` \| `max` (varsayılan `low`; Codex'te `max` → `xhigh`) |
| `tools[]` | — | Ajanın kullanabileceği araçlar (`Read, Glob, Grep, Write, Edit, Bash`). Boş/yok = araç yok |
| `cwd` | — | Araçların çalışacağı dizin (projenin hedef dizini). **Runtime hiçbir yolu kendisi seçmez** |
| `maxTurns` | — | Ajan döngüsünün tur tavanı; yoksa araçlara göre varsayılan |
| `progressUrl` | — | Canlı araç akışı: her araç çağrısında buraya `{tool, target}` POST edilir (loopback, tek kullanımlık belirteçli adres). Cevap beklenmez, hata yutulur, tur bloklanmaz. Yoksa akış yok |

**Yanıt (`TurnResponse`)**

| Alan | Ne |
|---|---|
| `text` | Modelin düz metin çıktısı |
| `structured` | `schema` verildiyse ayrıştırılmış JSON, yoksa `null` |
| `provider` · `model` | Çağrının fiilen gittiği sağlayıcı ve model |
| `destination` | Makineden çıktı mı: `local` \| `anthropic` \| `nvidia` \| `openai`. .NET `runs/`'a yazar |
| `usage` | `{inputTokens, outputTokens}` |
| `costUsd` | Sağlayıcının bildirdiği maliyet (abonelikte **eşdeğer**, fatura kesilmez) |
| `durationS` · `attempts` | Süre ve deneme sayısı (geçici hatada yeniden deneme) |
| `toolUses[]` | Ajan döngüsündeki araç çağrıları, sırayla: `{tool, target}`. Araç yoksa boş |
| `turns` | Ajan döngüsünün tur sayısı (sağlayıcı bildirirse) |

**Yazma sınırı.** Araçlı çağrıda SDK'nın `can_use_tool` geri çağrısı `Write`/`Edit` hedefini
`cwd` içine kısar; `Bash`'te mutlak yol, `~` ve `..` reddedilir. Anthropic dışı yol için
sandbox sağlayıcının kendi mekanizmasıdır (Codex: `--sandbox workspace-write`).

Hata gövdesi `detail` içinde `{"errorCode": ..., "message": ...}` taşır:

| Kod | HTTP | Anlam |
|---|---|---|
| `runtime.cli_missing` | 503 | `claude.exe` / `codex` bulunamadı |
| `runtime.not_logged_in` | 503 | CLI oturumu yok (`claude login` / `codex login`) ya da API anahtarı geçersiz |
| `runtime.provider_error` | 502 | Sağlayıcı/SDK hatası |
| `runtime.provider_unsupported` | 501 | Sağlayıcı henüz bağlı değil |
| `runtime.tools_unsupported` | 501 | Bu kimlik yolunda araçlı adım yok (OpenAI API anahtarı) |
