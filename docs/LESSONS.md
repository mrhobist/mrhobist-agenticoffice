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

### Smart App Control açıkken yerel derleme koşmuyor

`dotnet test` 78/78 patlıyor, `dotnet run` daha ilk satırda ölüyor:
`FileLoadException ... Uygulama Denetimi ilkesi bu dosyayı engelledi. (0x800711C7)`.
Hata koda benziyor ama koda ait değil. Windows Smart App Control
(`HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy` → `VerifiedAndReputablePolicyState = 1`) imzasız,
itibarı olmayan yerel derleme çıktılarını yükletmiyor; düşen testin kendisi değil, yüklenemeyen
`Infrastructure.dll`.

Teşhis kuralı: hata **bütün** testlerde aynıysa ve **merge öncesi commit** de aynı şekilde patlıyorsa sebep
kod değil ortamdır. Ayrı bir worktree'ye eski commit'i çıkarıp koşturmak bunu bir dakikada ayırır; bu
yapılmazsa saatler yanlış yerde aranır.

Çıkış yolu WSL: `wsl -d Ubuntu` içinde .NET SDK bir Linux sürecidir, SAC dokunmaz. İki tuzak var — depoyu
WSL'in kendi dosya sistemine kopyala (yoksa Windows `bin`/`obj` çıktısı ezilir) ve **kopyanın**
`global.json`'unu oradaki SDK'ya düşür: `rollForward: latestFeature` daha düşük yamayı kabul etmez
(10.0.401 istenirken 10.0.400 kuruluysa çözülmez).

SAC'ı kapatmak **tek yönlüdür**: geri açmak Windows'u sıfırlamayı gerektirir. Uçtan uca deneme bu makinede
ya SAC kapatılarak ya da .NET tarafı WSL'de koşturularak yapılır.

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

## Token ölçüsü (2026-09-23)

### Girdi tokeni çağrı başınadır, istem başına değil

`run_turn`'de hem istem karakteri hem girdi tokeni var; "oranı buradan al" bedava görünüyor. Değil: runtime'ın
girdi tokeni **iç turların toplamı** (yapısal çıktı en az 2 tur, araçlı adım 120'ye kadar) ve CLI araçsız turda bile
istemde görünmeyen ~4,5k token ekliyor. 10k karakterlik bir istemde düz oran ~1,4 çıkar, tahmin ~3 kat şişer ve
sıkıştırma hep erken tetiklenir. Doğrusu: yalnız araçsız turlar, tur başına girdi, eğim (sabit terim ek yükü yutar).

## Bir istem iki işe hizmet ediyorsa, biri sessizce bozulur (2026-09-22)

`tam-kadro` tasarım kapısında 3 kez reddedip $1,70 harcadı ve kod üretmedi. Önce ajanın md'si suçlandı.
Asıl neden `ReviewTask` isteminin tek bir inceleme türü varsaymasıydı: "dosyalar yazıldı, build'i koş" +
"şüphedeyken reddet". Tasarım kapısında ortada kod yok, koşacak şey yok → ikinci cümle devreye giriyor.

**Ders:** aynı istem iki farklı bağlamda kullanılıyorsa, **bağlamı isteme yaz**. Burada bilgi zaten tek
kaynakta duruyordu (`Workflow.ProducerBefore`) — istem onu sormuyordu. Özel durum eklemek yerine istemi
o bilgiye bağlamak hem `tam-kadro`'yu hem gelecekteki her ara kapıyı düzeltti.

**İkinci ders:** "şüphedeyken reddet" bedava değil. Ara kapıda her red bir tur daha maliyet demek ve
eksiği zaten sonraki test adımı yakalıyor; son kapıda ise hatalı kodu geçirmek daha pahalı. Aynı ilke
iki kapıda **zıt** yönde doğru.

**Üçüncü ders:** `karar-ilkeleri.md`'deki "karşılanmamış kabul ölçütü varsa hüküm RED'dir" kuralı, bir
rehber metni değerlendirirken manager'ı reddetmeye **zorluyordu** — bir rehber kabul ölçütünü karşılamaz,
onu kod karşılar. Mutlak yazılmış bir bilgi kuralı, yazıldığı bağlamın dışında bir yerde kullanılırsa
kural olmaktan çıkıp hataya dönüşür. Kuralın kapsamını kuralın yanına yaz.

## Saat tek başına "bugün" sanılır (2026-09-22)

Limit beklemesi mesajı `{resumeAt:HH:mm}` basıyordu. Haftalık kota 3 gün sonrasına sıfırlandığında
kullanıcı "07:00'de sürer" okuyup bir buçuk saat bekledi. Kullanıcıya gelecekteki bir an yazılıyorsa
**gün bilgisi de** yazılmalı; aynı gün değilse tarih şart.

## Kopan istek sunucuda ölmez (2026-09-23)

.NET tur bekçisi HTTP isteğini kesti, iş "zaman aşımı" diye kapandı; ama Starlette kopan isteğin işleyicisini
durdurmaz ve SDK üreteci kapatılmadan bırakılırsa `claude.exe` ancak çöp toplayıcıda kapanır. Kesilen t1 18 dk
daha koştu, ~3 $ harcadı, kayda geçmedi; "Yeniden dene" aynı dizinde ikinci bir ajan başlatabiliyordu. Belirti
"ajan kesildi ama iş yine de bitmiş"ti — iyi haber gibi göründüğü için sorgulanmadı. **İptali uçtan uca
test et:** istemci iptali sunucudaki alt süreci durduruyor mu? Runtime artık bağlantıyı yoklar, turu iptal eder
ve akışı `aclose()` ile hemen kapatır.

**İkinci ders:** "Kim ne harcadı" ölçülemez sanılıyordu (kota yüzdesi kaynağa göre ayrılmaz). Oysa her CLI
çağrısı kendi oturum kaydına kullanımıyla ve giriş noktasıyla yazılıyordu. İlk ölçüm: haftalık %92'nin ≥~83
puanı yöneten Claude Code oturumları, ~9 puanı ofis ajanıydı — uzun bağlamlı yönetim oturumu her mesajda
yüz binlerce token'ı yeniden okur. Tahmin etmeden önce verinin zaten bir yerde yazılı olup olmadığına bak.

## İzin geri çağrısı her yazmayı görmez (2026-09-24)

Runtime'ın `_guard`'ı "dosya yazma yalnız cwd altında" sınırını uyguluyordu ve testleri geçiyordu. Ama ofis ajanı
4 işte 9 turda `~/.claude/projects/<cwd>/memory/` altına dosya **yazdı ve düzenledi**: Claude Code'un otomatik
hafızası açıktı ve CLI kendi hafıza dizinine yazmayı **izin sormadan** onaylıyor — `can_use_tool` hiç çağrılmıyor.
Sonuç iki kat kötü: sınır delindi ve veritabanı dışında, çalışmadan çalışmaya taşınan gizli bir durum birikti
(ajan her görevin başında o notları okuyordu). `setting_sources=[]` bunu kapatmıyor; ayrı değişken gerekiyor
(`CLAUDE_CODE_DISABLE_AUTO_MEMORY=1`).

**Ders:** bir izin kancasının sınırı, yalnız kancanın **gördüğü** çağrılar için geçerlidir. Sınırı test ederken
"kanca reddediyor mu"ya değil, **gerçek bir koşunun araç kayıtlarında cwd dışı yol var mı**ya bak — o kayıt zaten
`run_turn.data.toolUses`'ta duruyordu. Notların işe yarayanları bilgi dosyasına ve ajan md'sine taşındı.

## "Kolaylık, çalışmayı durdurmaz" sessiz ölüm demektir (2026-09-24)

Anlık görüntü "bir kolaylık" diye tasarlanmıştı: alınamazsa `null`, çalışma sürer. Canlı projede **bir gün boyunca
hiç** alınamadı — proje Visual Studio'da açıktı, `.vs` altındaki kilitli indeks dosyası `git add`'i tamamen
düşürüyordu. Hiçbir şey kırmızıya dönmedi; "son turu geri al" seçeneği sessizce hiç sunulmadı. Fark eden, üstüne
yeni bir özellik kurmaya çalışan oldu. Testler geçiyordu: test dizininde kilitli dosya yoktu.

**Ders:** hatayı yutan bir yol, yuttuğunu **kayda** düşmeli ve kayıt okunmalı. "Durdurmaz" ile "görünmez"
aynı şey değil. Yeni bir özelliği mevcut bir mekanizmaya bağlamadan önce, o mekanizmanın canlı veride gerçekten
ürettiğine bak (`run_phase.data.snapshot` boştu). Testte gerçek ortamın kötü hâlini kur: burada açık bir IDE.

## Sahne donması tuval boyutundan gelir, koddan değil (2026-09-26)

"Ajanlar dolaşırken donuyor" şikâyetinde sahne kodu masum çıktı (kare 0,5 ms, yol bulma 1 ms, uzun görev yok).
Bu makinede tarayıcının çizim motoru **Microsoft Basic Render Driver** (ekran kartı sürücüsü yok). Tarayıcı
~3,3 Mpx üstündeki tuvali hızlandırılmış yoldan çıkarıp yazılımla rasterliyor; eşik keskin:

| Arka tampon | Kare |
|---|---|
| 1800×1787 (3,22 Mpx) | 0,6 ms |
| 1900×1887 (3,59 Mpx) | 29,5 ms |
| 2300×2284 (5,25 Mpx) | 41,6 ms |

Maliyet tek bir çağrıda görünmez: toplu rasterleştirme ilk senkron noktada ödenir (profilde masum bir `save()`
40 ms görünür). **Kural:** tuval arka tamponu sınırlıdır (`OfficeScene.vue → maxCanvasPx`, 2,4 Mpx) ve kareler
yavaş kaldıkça sınır kendini küçültür. Performansı küçük panelde ölçme; büyük tuvalde (`canvas.width = 2400`) ölç,
`webgl` renderer adına bak.
