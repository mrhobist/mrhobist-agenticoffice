# MrHobist.AITeam

Yapay zekâ ekibi üretim ofisi. Ekip (`config/agents` + `config/workflows`) bir brief'i alıp kod üretir;
akış 2B piksel bir ofiste canlı izlenir. Canlı ekip 2026-09-23'ten beri tek ajan (`tek-kisilik-dev-kadro`);
eski altı rollü ekip testlerin sabit fikstürüdür (`tests/…ServiceTests/Fixtures/config/`).

| Klasör | Ne | Port |
|---|---|---|
| [`src/`](src/) | .NET 10 — Onion; tek host `Api` (uçlar + iş kanalı) | 5080 |
| [`runtime/`](runtime/) | Python FastAPI — **yalnız LLM çağrısı** | 5090 |
| [`ui/`](ui/) | Nuxt 4 + TypeScript + Canvas 2D — sprite tabanlı canlı piksel ofis | 3000 |
| [`assets/`](assets/) | Ham sprite sayfaları (`raw/`) ve plan panoları (`reference/`) | — |
| [`config/`](config/) | Ajan md'leri, alt md'ler, iş akışları, sahne — **tek doğru kaynak** | — |
| `data/` | `aiteam.db` (SQLite): proje, çalışma, tur, mesaj, faz, ayarlar. Gitignore'da | — |
| [`scripts/sql/`](scripts/sql/) | İleri yönlü şema betikleri; uygulanma defteri `schema_change_log` | — |

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
- `runtime/` içinde `if role == ...`, tur sayacı, faz bilgisi **yoktur**; runtime kodu dosya yazmaz.
  **Ajan araçları** (Agent SDK: Read/Write/Edit/Bash…) .NET'in istekle gönderdiği `tools` listesi ve `cwd`
  ile açılır; hangi adımın hangi aracı aldığı `Application`'dadır (`ToolAccess`). Yazma `cwd` dışına
  çıkamaz (SDK izin geri çağrısı). 2026-09-19 kullanıcı kararı: developer dosyayı kendisi yazar.
- Python **durumsuzdur**: iki ardışık istek birbirini bilmez. Geçmiş `messages` ile gelir.
- Sözleşme `IAgentRuntimeService` (Application) · `PythonAgentRuntimeClient` (Infrastructure).
- Python'u kapatmak derlemeyi ve `ServiceTests`'i **bozmaz** — sahte adaptörle çalışır.

### 2. Yapılandırma dosyada, durum veritabanında

**2026-09-21 kararı:** çalışma zamanı durumu SQLite'a taşındı; "veritabanı yoktur" kuralı bu tarihte
kalktı. Gerekçe ve alternatifi aşağıda §Sapmalar. Ayrım kalktı değil, **yer değiştirdi** — iki ayrı
sorumluluk hâlâ iki ayrı yerde:

| Ne | Nerede | Neden |
|---|---|---|
| Ajan md'leri, alt md'ler, iş akışları, sahne yerleşimi | `config/` — md + json | Git'te versiyonlanır, diff okunur, uygulama kapalıyken düzenlenir. Bunlar **kaynak koddur** |
| Proje, çalışma, görev, faz, tur, mesaj, ayarlar | `data/aiteam.db` — SQLite | Sorgulanabilir (maliyet, kullanım, süzme), tek yazımda tutarlı, kimlik dosya adına dönüşmez |

- Şema **database-first**: `scripts/sql/changes/*.sql` ileri yönlü, yayından sonra değişmez; uygulanan
  betik `schema_change_log`'a checksum'ıyla yazılır. Uygulanmış bir betik değişirse **kalkış durur**.
  EF migration **yoktur**; EF yalnız eşler. Sapma testi: her tablodan `Take(0)` (`ProbeDatabaseAsync`).
- Domain'de EF **yoktur** (`verify.ps1` bunu denetler). Kalıcılık satırları `Infrastructure/Persistence`
  altındadır; Application yalnız `IRunStore`/`IProjectStore`/`ISettingsStore` arayüzlerini görür.
- Append-only **satır düzeyinde** korunur: tur/mesaj/faz satırları eklenir, güncellenmez; sıra AUTOINCREMENT
  `id`'dir. Ayrı bir `seq` sayacı **yok** — istek yolu (iptal, cevap) ile iş kanalı aynı çalışmaya aynı anda
  yazabilir, `MAX+1` orada yarışır. Silme yalnız çalışmanın tamamı için, FK cascade ile.
- Yazan **tek yazıcı** vardır: Api içindeki iş kanalı (`JobChannel` + `JobWorker`, sıralı). Uçlar hızlı
  doğrulamayı yapar, uzun işi kanala bırakır, 202 döner. Okuma `IRunReader`. SQLite WAL modundadır:
  okuyucu yazıcıyı beklemez.
- Bozuk JSON gövdesi olan satır **yok sayılır**, geri kalanı kurtarılır (JSONL'deki yarım satır kuralı).
- `config/` yazımı **atomiktir**: geçici dosya + `File.Move(overwrite)`. Yarım dosya okunmaz.
- `config/` değişince Api yeniden yükler; çalışma sürerken yüklenen tanım **donar**.

### 3. Ağ sınırı

Kimlik doğrulama tek şemalı ve basit olduğu için **hostlar yalnız `127.0.0.1` dinler**.
`0.0.0.0` bağlaması bilinçli bir karar gerektirir ve bu dosyada gerekçelenir.

### 4. Sağlayıcı ve maliyet

- Bir ajanın sağlayıcı/modeli **md frontmatter'ında** belirlenir; boşsa varsayılan kullanılır.
- Her tur `runs/` içine **hedefiyle** kaydedilir (`local` | `anthropic` | `nvidia` | `openai`).
  Çalışma sonunda makineden çıkan çağrılar tek tek raporlanır.
- Bütçe **iki kapsamda** verilir ve ikisi de **boşsa sınırsızdır**: iş başına (`Run.maxCostUsd`) ve
  2026-09-22'den beri proje başına (`Project.maxCostUsd` / `maxTokens`, önce dolan durdurur).
- Bütçe aşımı çalışmayı **durdurur**, uyarıyla geçmez. Maliyet **eşdeğerdir** (abonelikle ücret kesilmez);
  asıl koruma **limit eşiği** (ayarlarda `limitGuards`, varsayılan %99): kota dolunca yeni tur başlamaz,
  çalışma pencere sıfırlanınca kendisi sürer (`docs/DOMAIN.md` → Bütçe ve limit).

### 5. Sözleşme

- Enum'lar JSON'da **adıyla** taşınır; yeni üye **sona** eklenir, var olan silinmez.
- Frontend tipleri **üretilir** (`npm run gen:api`), elle yazılmaz.
- Hata gövdesi Problem Details + `errorCode`; `title`/`detail` ekrana basılmaz.

## Referans mimariden bilinçli sapmalar

Kaynak [`ARCHITECTURE.md`](ARCHITECTURE.md) (çok kiracılı kurumsal sistem). Bu proje tek
makinede, tek kullanıcıyla çalışan bir geliştirici aracıdır — aşağıdakiler **kasten** budandı:

| Sapma | Gerekçe |
|---|---|
| **PostgreSQL yerine SQLite** (EF Core 10 + `Microsoft.Data.Sqlite`; numaralı SQL, değişiklik defteri ve drift testi **var**) | 2026-09-21: veritabanı geldi, motor küçüldü. Tek makinede çalışan bir geliştirici aracı için sunucu/Docker şartı koymak karşılığını vermiyor; ARCHITECTURE §7 zaten testlerde SQLite'ı sayıyor. Postgres'e geçiş = sağlayıcı + SQL betiklerinin çevirisi. Yapılandırma (`config/`) **kasten dışarıda bırakıldı**: prompt'lar ve iş akışları git diff'inde okunabilir kalmalı (opencode da aynı ayrımı yapıyor) |
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

Runtime'ı Api başlatır ve kapatır (`AITeam:AutoStartRuntime=false` ile kapanır). Ayrı izlemek
için önce kendin başlat — ayakta olana dokunulmaz. `.venv` cihaza bağlıdır, taşınmaz:
onarımı `verify.ps1 -SetupRuntime`.

```bash
runtime/.venv/Scripts/python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 5090 --app-dir runtime
```

```bash
npm --prefix ui run dev
```

```bash
python scripts/build-sprites.py
```

Veritabanı kalkışta hazırlanır: `data/aiteam.db` yoksa oluşur, uygulanmamış şema betikleri koşar.
Sıfırdan başlamak için `data/` klasörünü sil — yapılandırma (`config/`) etkilenmez.

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
| [`docs/architecture.drawio`](docs/architecture.drawio) | 8 sayfalık resim: sistem, yığın, Onion, ekip, yaşam döngüsü, bir tur, UI, Python. draw.io ile aç |

⚠ **Yeni belge açma.** Envanter belgesi (uç nokta listesi, paket sürümleri) **hiç açma** —
kodun ikinci kopyası bayatlar. Sözleşme `/openapi/v1.json`'dır.
