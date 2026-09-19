# runtime — LLM çağrı katmanı

> **Python durum tutmaz, veritabanı görmez, iş kuralı bilmez.**
> Tek işi: `{systemPrompt, messages, provider, model, schema?}` alıp
> `{text, structured, usage, costUsd}` döndürmek.

Bu kural pazarlığa kapalıdır (`../CLAUDE.md` §1). Orkestrasyon .NET tarafındadır.

Burada **olmayacak** şeyler: tur sayacı, faz bilgisi, iş akışı bilgisi, dosya yazma,
veritabanı, görev kavramı, role göre dallanma. `scripts/verify.ps1` bunu denetler.

## Çalıştırma

Depo kökünden (sanal ortam `runtime/.venv`, Python 3.12):

```bash
runtime/.venv/Scripts/python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 5090 --app-dir runtime
```

Testler (`runtime/` içinden):

```bash
.venv/Scripts/python.exe -m pytest -q
```

## Sağlayıcılar

Bugün yalnız **`anthropic`** bağlıdır (Claude Agent SDK, paket `claude_agent_sdk`).
`nvidia` ve `ollama` istekleri `501 runtime.provider_unsupported` döner.

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
| `GET /v1/auth?provider=` | Kimlik durumu `[{provider, loggedIn, account, detail}]`; 60 sn önbellekli |
| `GET /health` | Süreç ayakta mı |

Hata gövdesi `detail` içinde `{"errorCode": ..., "message": ...}` taşır:

| Kod | HTTP | Anlam |
|---|---|---|
| `runtime.cli_missing` | 503 | `claude.exe` bulunamadı |
| `runtime.not_logged_in` | 503 | Claude Code oturumu yok — `claude login` |
| `runtime.provider_error` | 502 | Sağlayıcı/SDK hatası |
| `runtime.provider_unsupported` | 501 | Sağlayıcı henüz bağlı değil |
