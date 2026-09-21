# Pahalıya öğrenilenler

v1'de (yedek: `MrHobist.AITeam.v1-yedek/`) ölçülerek bulundu. **İş yapmadan önce ilgili
bölümü oku** — hepsi saatler kaybettirdi.

## NVIDIA NIM sağlayıcısı

### `reasoning_effort` verilmezse modeller cevabı hiç üretemez

Katalogdaki modellerin çoğu "thinking" modeli. Varsayılan derinlikte token bütçesinin
tamamını akıl yürütmeye harcayıp `content` alanını **boş** döndürüyorlar — hata da
vermiyorlar. Ölçüm (analist şeması, `z-ai/glm-5.3`):

| `reasoning_effort` | Sonuç |
|---|---|
| `low` | **87 saniye**, `reasoning_content` boş, şema tam doldu |
| `medium` | 420 saniyede timeout |
| verilmedi | 600 saniyede timeout |

**Kural:** `reasoning_effort` her istekte gönderilir, varsayılan `low`.
Rol bazında artırılabilir.

### Bütçe darsa `content` boş, `reasoning_content` dolu döner

`max_tokens: 600` ile `content=0`, `reasoning_content=2083` geldi. Sessizce boş çıktı
vermek yerine **açık hata** fırlat: bütçe yetmedi, `max_tokens` artır.

### `/v1/models` kimlik doğrulamıyor ve GLOBAL katalog dönüyor

Geçersiz anahtarla bile 200 ve tam liste döner. "Model katalogda var" **erişilebilir
demek değil**. Hesabın gerçekten çağırabildiği modeller ancak istek atarak anlaşılır:
erişim dışı model `404 "Function ... not found for account"` döner.

Ölçüm: katalogdaki 82 modelden **20'si erişilebilir, 55'i `404`**.

### Ücretsiz uç aralıklı takılıyor — kısa timeout + yeniden deneme şart

Aynı çağrı bazen 2 saniyede, bazen 10+ dakikada döner. Hız sınırı başlığı yok, kısıtlama
yok — kuyruk. Uzun timeout beklemek yerine **180s × 3 deneme** çok daha güvenilir;
yeni deneme genelde sağlıklı bir worker'a düşüyor.

### `nvext.guided_json` desteklenmiyor

Yapısal çıktı için `response_format: {"type":"json_schema","json_schema":{...}}` kullan.
`nvext.guided_json` `400` döner.

### Kimi K3 bu hesapta pratikte kullanılamıyor

`moonshotai/kimi-k3` (2.8T MoE) sayfada "Free Endpoint: Available" görünüyor ve anahtar
geçerli. 5 bağımsız deneme yapıldı; yalnız 1'i yanıt verdi (198s). `Accept: text/event-stream`,
`stream=true`, `reasoning_effort: max` dahil sayfadaki örnek birebir kopyalandı → 500s timeout.
Aynı hesapta `z-ai/glm-5.3` 87s, `openai/gpt-oss-20b` 17s çalışıyor. Sorun anahtar veya
kısıtlama değil, o modelin ücretsiz havuzdaki kapasitesi.

**Çalıştığı doğrulanan modeller:** `z-ai/glm-5.3` (analist/kontrol), `openai/gpt-oss-20b`
(developer, daha hızlı).

## Claude Agent SDK

### Alt süreç, oturum yenilemesini devralamıyor

Claude Code masaüstü oturumu içinden Agent SDK çağrıldığında alt `claude` süreci
`~/.claude/.credentials.json` kullanır. O dosyadaki `refreshToken` dolmuşsa
`authentication_failed: OAuth session expired and could not be refreshed` gelir ve
**ortam değişkenlerini temizlemek çözmez**. Çözüm kullanıcı tarafında: terminalde
`claude` → `/login`.

Kontrol: `claude -p "sadece OK yaz"` tek başına çalışmıyorsa SDK de çalışmaz.

### `output_format` ve `structured_output` vardır

`ClaudeAgentOptions.output_format = {"type": "json_schema", "schema": {...}}` desteklenir,
sonuç `ResultMessage.structured_output` alanından okunur. Ayrıca `effort`,
`max_budget_usd`, `fallback_model`, `mcp_servers`, `allowed_tools` mevcut.

## Windows

### Konsol cp1254 — UTF-8'e zorlanmalı

Türkçe kod sayfasında `→` gibi karakterler `UnicodeEncodeError` veriyor. Süreç başında
stdout/stderr açıkça UTF-8'e ayarlanmalı.

### `*.ps1` yalnız ASCII

PowerShell 5.1 BOM'suz dosyayı ANSI okur; Türkçe karakter içeren script bozulur.

### Çıktıyı boruya bağlamak çıkış kodunu maskeler

`program | tail -40` çalıştırıldığında `$?` **tail'in** kodudur. Çöken bir çalışma
"exit 0" görünür. Doğrulama komutlarında boru kullanma veya `PIPESTATUS` oku.

### Python stdout dosyaya yönlendirilince tamponlanır

Canlı ilerleme göremezsin. `python -u` kullan.

## Görselleştirme (v1 pixel ofis)

v1'de [KbWen/agent-virtual-office](https://github.com/KbWen/agent-virtual-office) çatallandı.
v2 Three.js ile sıfırdan yazılıyor, ama iki tuzağı kayda değer:

### Durum gönderimi: birleştirme değil DEĞİŞTİRME

O API'de her POST tüm kadroyu değiştiriyordu; tek rol gönderince diğerleri siliniyordu.
**Kendi UI sözleşmemizde bunu tersine kur:** olay akışı append-only olsun, istemci
kendi durumunu biriktirsin.

### Sessiz kabul en kötü hata biçimi

Yanlış şekilli gövde `HTTP 200` + `agents: 0` dönüyordu — hiçbir şey kaydedilmiyor,
hata da verilmiyordu. Saatler kaybettirdi. **Kendi API'mizde geçersiz gövde 400 döner.**

## SQLite (2026-09-21, dosya deposundan geçişte)

### `DateTimeOffset` ile `ORDER BY` çalışmaz

EF Core'un SQLite sağlayıcısı sıralamayı reddeder: *"SQLite does not support expressions of type
'DateTimeOffset' in ORDER BY clauses."* Çalışma listesi "yeni → eski" sıralanmak zorunda olduğu için
bu, derleme değil **çalışma zamanı** hatasıdır — ilk `GET /runs` isteğinde patlar.

**Çözüm:** damgayı UTC'ye çevirip sabit genişlikte metne yazan bir `ValueConverter`
(`Persistence/UtcTextConverter`), tip düzeyinde uygulanır (`ConfigureConventions`), nullable olanlar
dahil. Sabit genişlik şart: `yyyy-MM-ddTHH:mm:ss.fffffffZ` — sözlük sırası = zaman sırası.
Yerel ofsetli bir damga yazılırsa bu kırılır, o yüzden yazan taraf **daima** UTC üretir.

### Yabancı anahtarlar varsayılan KAPALI

SQLite `PRAGMA foreign_keys` varsayılanı `OFF`'tur. Açıkça `ForeignKeys = true` yazılmazsa
`ON DELETE CASCADE` **sessizce** çalışmaz: çalışma silinir, turları/mesajları/fazları öksüz kalır.
Sessiz kabul yine en kötü hata biçimi — bağlantı dizesinde açık, testte doğrulanır
(`Calisma_silinince_alt_satirlar_da_duser`).

### Testte veritabanı dosyası kilitli kalır

`Microsoft.Data.Sqlite` bağlantıları havuzlar; `ServiceProvider` atılsa da dosya açık kalır ve
geçici dizin silinemez. Fixture `Dispose`'unda `SqliteConnection.ClearAllPools()` çağrılır.

### Ondalık sütuna tip verilmezse EF uyarır

SQLite'ta ondalık tür yoktur. `HasColumnType("TEXT")` yazılır ve para alanları **SQL tarafında
sıralanmaz/karşılaştırılmaz**; toplama bellekte yapılır. Yuvarlama hatası istemiyorsak `REAL` değil
metin doğru seçimdir (opencode `real` kullanıyor; bizim maliyet eşitliğini doğrulayan testlerimiz var).

### "Tek yazıcı" kuralı istek yolunu kapsamıyor

`JobChannel` çalışma başına kilit tutar ama yalnız **işler** için. `POST /runs/{id}/cancel`, `/answer`,
`/revise` HTTP isteğinin içinde doğrudan depoya yazar — tam da bir iş slotu aynı çalışmaya yazarken.
İlk tasarımdaki `seq = MAX(seq)+1` bu yüzden yarışıyordu (unique index → 500). Ders: bir "sayaç" tutmak
yerine veritabanının kendi monoton kimliğini (AUTOINCREMENT `id`) sıra olarak kullan; hesaplanan sıra
numarası, eş zamanlı yazıcı varsa daima yarış demektir.

## Claude Code'un sistem promptu ≠ bedava verim (2026-09-21)

### Preset prompt büyük iç içe şemayı doldurmayı bozuyor

SDK'ya `system_prompt` düz string verilince Claude Code'un kendi kılavuzu silinir; `preset: claude_code`
onu korur. Kulağa saf kazanç gibi geliyor ama planlama turunda model, kılavuzun "yap, kısa bitir" baskısı
altında `rules`/`tasks` gibi büyük iç içe alanları hiç doldurmadı: 5 denemede şema hatası, bir kez de
"rule1 / a.cs" taslağı. Yürütme adımında (küçük şema, araç ağır iş) sorun yok. **Mod adım başına seçilir,
karar .NET'te.**

### "Açıklayıcı" hipotezi ölçmeden anlatma

55 iç tur / 16 farkını "sistem promptu" diye anlattım; ölçüm görev başına **31 = 31** dedi. Farkı bağlam
taşıma kapattı, preset sıfır ekledi. Makul mekanizma ≠ ölçülmüş etki; iddiayı rakamdan sonra yaz.
