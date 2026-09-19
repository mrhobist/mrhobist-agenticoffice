# MrHobist.AITeam

Yapay zekâ ekibi üretim ofisi. Altı rol (analist · tasarımcı · developer · testçi · manager ·
organizatör) bir brief'i alıp kod üretir; akış 2B piksel bir ofiste canlı izlenir.

| Klasör | Ne | Port |
|---|---|---|
| [`src/`](src/) | .NET 10 — Onion; tek host `Api` (uçlar + iş kanalı) | 5080 |
| [`runtime/`](runtime/) | Python FastAPI — **yalnız LLM çağrısı** | 5090 |
| [`ui/`](ui/) | Nuxt 4 + TypeScript + Canvas 2D — sprite tabanlı canlı piksel ofis | 3000 |
| [`assets/`](assets/) | Ham sprite sayfaları (`raw/`) ve plan panoları (`reference/`) | — |
| [`config/`](config/) | Ajan md'leri, alt md'ler, iş akışı — **tek doğru kaynak** | — |
| [`runs/`](runs/) | Çalışma geçmişi (JSONL), gitignore'da | — |

**Bu dosya her oturumda okunur → KISA TUT (≤200 satır).** Mimari sözleşme
[`ARCHITECTURE.md`](ARCHITECTURE.md), sapmalar aşağıda §Sapmalar.

## Çalışma dili

- **Sohbet: Türkçe.** Kod ve identifier'lar: **İngilizce.**
- Kullanıcıya dönük metinler **Türkçe** — sunucu `errorCode` döner, metin UI'da eşlenir.
- `*.ps1` **yalnız ASCII** (PowerShell 5.1 BOM'suz dosyayı ANSI okur).
- Kararlar **soruyla değil belgeyle** alınır: belirsizlikte önerilen kararı ve alternatifini
  belgeye "varsayımla ilerlenir" diye yaz. Yalnız geri alınamaz işlerde onay al.

## Pazarlıksız kurallar

İhlal edilirse ya durum iki yerde tutulur, ya çalışma sessizce yanlış modele gider,
ya da kimliksiz bir servis para harcamaya başlar.

### 1. Python sınırı

> **Python durum tutmaz, veritabanı görmez, iş kuralı bilmez.**
> Tek işi: `{systemPrompt, messages, provider, model, schema?}` alıp
> `{text, structured, usage, costUsd}` döndürmek.

Bu kural pazarlığa kapalıdır. Orkestrasyon döngüsü (analiz → tasarım → geliştirme → test →
onay, red halinde geri dönüş) **.NET `Application` katmanındadır**. Python'a iş kuralı
sızarsa mantık iki dile bölünür ve döngü Python olmadan test edilemez hâle gelir.

Sonuçları:
- `runtime/` içinde `if role == ...`, tur sayacı, faz bilgisi, dosya yazma **yoktur**.
- Python **durumsuzdur**: iki ardışık istek birbirini bilmez. Geçmiş `messages` ile gelir.
- Sözleşme `IAgentRuntimeService` (Application) · `PythonAgentRuntimeClient` (Infrastructure).
- Python'u kapatmak derlemeyi ve `ServiceTests`'i **bozmaz** — sahte adaptörle çalışır.

### 2. Dosya tabanlı depo

Veritabanı **yoktur**. İki ayrı sorumluluk, iki ayrı biçim:

| Ne | Nerede | Neden |
|---|---|---|
| Ajan md'leri, alt md'ler, iş akışı | `config/` — md + json | Git'te versiyonlanır, diff okunur, uygulama kapalıyken düzenlenir |
| Çalışma, görev, faz, tur, mesaj | `runs/<id>/` — JSONL | Append-only, çökme kayıtları bozmaz, UI sonunu okuyup canlı akar |

- Yazma **atomiktir**: geçici dosya + `File.Move(overwrite)`. Yarım dosya okunmaz.
- JSONL'e yazan **tek yazıcı** vardır: Api içindeki iş kanalı (`JobChannel` + `JobWorker`, sıralı).
  Uçlar hızlı doğrulamayı yapar, uzun işi kanala bırakır, 202 döner. Okuma `IRunReader`.
- Bozuk son satır **yok sayılır**, geri kalanı kurtarılır.
- `config/` değişince Api yeniden yükler; çalışma sürerken yüklenen tanım **donar**.

### 3. Ağ sınırı

Kimlik doğrulama tek şemalı ve basit olduğu için **hostlar yalnız `127.0.0.1` dinler**.
`0.0.0.0` bağlaması bilinçli bir karar gerektirir ve bu dosyada gerekçelenir.

### 4. Sağlayıcı ve maliyet

- Bir ajanın sağlayıcı/modeli **md frontmatter'ında** belirlenir; boşsa varsayılan kullanılır.
- Her tur `runs/` içine **hedefiyle** kaydedilir (`local` | `anthropic` | `nvidia`).
  Çalışma sonunda makineden çıkan çağrılar tek tek raporlanır.
- Bütçe aşımı çalışmayı **durdurur**, uyarıyla geçmez.

### 5. Sözleşme

- Enum'lar JSON'da **adıyla** taşınır; yeni üye **sona** eklenir, var olan silinmez.
- Frontend tipleri **üretilir** (`npm run gen:api`), elle yazılmaz.
- Hata gövdesi Problem Details + `errorCode`; `title`/`detail` ekrana basılmaz.

## Referans mimariden bilinçli sapmalar

Kaynak [`ARCHITECTURE.md`](ARCHITECTURE.md) (çok kiracılı kurumsal sistem). Bu proje tek
makinede, tek kullanıcıyla çalışan bir geliştirici aracıdır — aşağıdakiler **kasten** budandı:

| Sapma | Gerekçe |
|---|---|
| **Veritabanı yok** (EF, Npgsql, numaralı SQL, değişiklik defteri, drift testi) | Yapılandırma dosyada daha iyi (git, diff, elle düzenleme); geçmiş append-only JSONL'e uyuyor. Tek kullanıcıda sorgulanabilirlik karşılığını vermiyor |
| **Çok kiracılılık yok** (`tenant_id`, named filter, write guard) | Tek çalışma alanı. Ekip aracına dönerse sütun + filtre + guard + taşıma gerekir |
| **Gateway yok** | Tek makine, loopback |
| **Cache yok** (FusionCache/Redis) | Sıcak veri küçük, tek okuyucu |
| **Elastic / APM yok** | Serilog dosyaya yazar. `GenericLog` sözleşmesi korunur, sink değişikliği tek satır |
| **`Common` projesi yok** | İçeriği `Application`'a sığıyor (Appointo'daki gibi) |
| **JWT tek şema, tek sınıf** | `UserLogin` / `Automation` / `ExternalApi` ayrımı yerine tek şema; ayrımı **rol claim'i** yapar |
| **`Asp.Versioning` yok** | Tek istemci, sürümleme yolu `/api/v1` sabitiyle |
| **Task.Api yok** (2026-09-19 kararı) | "Çalışma dakikalar sürer, istek bekleyemez" sorununu ayrı süreç değil süreç içi kuyruk çözer: istek 202 döner, iş `JobWorker`'da koşar. Ayrı host iki süreç, HTTP iletim, HTTP sahne yayını ve çift `ProblemMapping` getiriyordu; tek kullanıcıda karşılığı yok. Katmanlama (`RunService`, `Dispatcher`) aynı; ileride ayırmak bir host + DI kaydı |

## Komutlar

```bash
powershell -ExecutionPolicy Bypass -File scripts/verify.ps1
```

```bash
dotnet run --project src/MrHobist.AITeam.Api
```

```bash
runtime/.venv/Scripts/python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 5090 --app-dir runtime
```

```bash
npm --prefix ui run dev
```

```bash
python scripts/build-sprites.py
```

## Belge haritası

| Belge | İçeriği |
|---|---|
| **`CLAUDE.md`** (bu dosya) | Giriş kapısı: pazarlıksız kurallar, sapmalar, komutlar |
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | Referans mimari sözleşmesi — sapmalar yukarıda |
| [`docs/DOMAIN.md`](docs/DOMAIN.md) | Roller, iş akışı türleri, ask/handoff akışı, geri dönüş kuralı |
| [`docs/API.md`](docs/API.md) | HTTP sözleşmesi — UI'ın okuyacağı tek dosya |
| [`docs/SCENE.md`](docs/SCENE.md) | Canlı sahne: `scene.json` şeması, olaylar, asset envanteri ve eksikler |
| [`docs/LESSONS.md`](docs/LESSONS.md) | Pahalıya öğrenilenler — **iş yapmadan önce oku** |
| [`docs/PHASES.md`](docs/PHASES.md) | Faz faz teslim listesi; yeni oturum buradan devam eder |
| [`docs/error-codes.md`](docs/error-codes.md) | `errorCode` → anlam. UI sözleşmesi |

⚠ **Yeni belge açma.** Envanter belgesi (uç nokta listesi, paket sürümleri) **hiç açma** —
kodun ikinci kopyası bayatlar. Sözleşme `/openapi/v1.json`'dır.
