# Alan modeli ve akış kuralları

Roller, çalışma yaşam döngüsü, plan onayı, organizatörün dağıtım kuralı, devir ve sahne eşlemesi.
Bu belge **davranış** sözleşmesidir; HTTP biçimi `docs/API.md`, sahne olayları `docs/SCENE.md`.
Kararlar tarihli alınır; "varsayımla ilerlenir" işaretli olanlar kullanıcıya sorulmadan seçildi.

## Roller

Ekip **açıktır** (zorunlu rol yok); hangi ajanın çalışacağını iş akışı belirler. Bugünkü kadro:

### Ekip yönetimi (2026-09-20, kullanıcı kararı)

- **Takım = iş akışı.** "Takım kurmak" bir akış kurmaktır: analist → tasarımcı → developer → … ya da analist →
  developer → … gibi sıralı adımlar, her adımda bir ajan. Ajanlar tek havuzdur (`config/agents/*.md`); proje bir
  takım (akış) seçer. Ayrı bir "takım" kavramı **yok** (alternatif — adlandırılmış ajan grubu — reddedildi: akış-ajan
  eşlemesi ve göç getiriyordu).
- **Ekip paneli** (üst bar "Ekip", kısayol E, pusulada "Yönet"): Ajanlar sekmesi = havuz, yeni ajan (ad, anahtar,
  özet, ofis rolleri, sağlayıcı/model/efor, sorabilir, bilgi dosyaları, sistem promptu), silme (akışta ya da
  `can_ask`'ta kullanılıyorsa 409). Takımlar sekmesi = akış düzenleyici (adım ekle/sil/sırala, tür, ajan, ofis rolü;
  ilk adım analiz kilitli; `PUT /workflows/{key}`, `default` silinemez). **md yükleme (2026-09-20):** hazır ajan
  md'si (frontmatter + prompt) dosya seçiciden içe aktarılır (`POST /agents/import`); **Bilgi dosyaları** sekmesi
  `config/knowledge/*.md`'yi yönetir: oluştur, düzenle, md yükle, sil (bir ajanın listesindeyken 409). Dosyalar
  Api tarafından yazılır; tarayıcı yalnız metni gönderir.
- **Sahne yerleşimi.** Yeni ajan `config/scene.json → agents[]`'e otomatik yazılır: kullanılmayan ilk karakter
  sprite'ı (`sprites[]`, 8 karakter; bitince tekrar) + boş ilk masa (`seats`). Masa kalmadıysa `home: {}` →
  **ziyaretçi**: ofiste evi yoktur, arada kapıdan girip panoya bakar, çıkar; iş alınca panonun önünde çalışır.
  Silinince sahneden düşer. Her değişiklikte Api `scene.reload` yayımlar, UI sahneyi yeniden kurar.
- **Karakter seçimi (2026-09-23, kullanıcı isteği).** Yeni ajan formunda ve ajan panelinde karakter (sprite) seçilir;
  önizleme yürüyüş sayfasının ilk karesidir. Seçim md'ye değil `scene.json → agents[].sprite`'a yazılır (görünüm
  yerleşimdir, ekip tanımı değil). Liste `sprites[]`'tır; dışındaki ad 400 `agent.unknown_sprite`. Aynı karakteri iki
  ajan seçebilir (kullanıcının kararı; seçici kimin kullandığını gösterir); "otomatik" kullanılmayan ilk karakteri verir.

| Ajan | Ne yapar | Kod mu prompt mu |
|---|---|---|
| `analyst` | Brief'i çözümler; özet, mimari, bağlayıcı kurallar ve bağımlılık sıralı görev listesi üretir. Onay gelmeden plan **değişebilir**, iş açılmaz | prompt |
| `designer` | Kullanıcıya dokunan yüzeylerin yapısını ve akışını belirler; kod yazmaz | prompt |
| `developer` | Görevi kodlar. **İlk teslimde henüz çalışmaz**: iş masasına gelir, bekler (bkz. Adım yürütücüleri) | prompt |
| `tester` | Kuralları denetler, testi çalıştırır, onaylar ya da reddeder | prompt |
| `manager` | `ask` hedefi ve son karar kapısı (altı şapka) | prompt |
| `organizer` | **Dağıtıcı.** Bekleyen iş var mı, boş ajan var mı bakar, işi verir. Bu mantık **koddadır** (`Dispatcher`), sıfır token. LLM'e yalnız **devir notu** için gider | kod + prompt |

### MCP sunucuları (2026-09-23, kullanıcı isteği; ayrıntılar varsayımla ilerlenir)

İstek: "MCP yönetim ekranı olsun, MCP eklenip yönetilebilsin, ekipteki kişilere MCP yetkisi verilsin."

- **Tanım veritabanında** (`mcp_server`, betik 0004), `config/`'da **değil**. Gerekçe: çalıştırılabilir yol, yerel adres ve
  belirteç (ortam değişkeni, `Authorization` başlığı) makineye özgüdür ve git'e girmemeli. Alternatif — `config/mcp.json` +
  `${ENV}` genişletme — reddedildi: yönetim ekranından yazılan belirteç ya git'e girer ya da ayrı bir sır deposu ister.
- **Yetki ajan md'sinde** (`mcp: [anahtar, …]`): yetki ekibin tanımıdır, ekip `config/`'dadır (CLAUDE.md §2). MCP paneli
  kutu işaretlenince ilgili md'leri yeniden yazar (`PUT /mcp/{key}/access`); ajan panelinde salt okunur görünür.
  Yeni verilen anahtar kayıtlı olmalı (400 `agent.unknown_mcp`); sonradan silinmiş/kapatılmış anahtar ajanı kaydetmeyi
  engellemez, çalışma anında **atlanır** ve çalışmanın kaydına `subject: "mcp"` notu düşer (sessiz kabul yok).
- **Kim, nerede alır.** Yetkili ajan yalnız **araçlı** turlarda (analiz, geliştirme, test, soru) sunucuyu alır; araçlar
  `mcp__{anahtar}__{araç}` adıyla gelir. Araçsız tur (plan onayı) almaz. `strict_mcp_config` açık kalır: kullanıcının kendi
  Claude Code MCP'leri değil, yalnız verilenler yüklenir. İzin kararı runtime'ın `_guard`'ında: dosya yazmayan araç serbest.
- **Yalnız Anthropic.** MCP'yi bugün yalnız Claude Agent SDK çalıştırır; sağlayıcısı `anthropic` ya da boş (varsayılan)
  olmayan ajana yetki 400 `agent.mcp_unsupported`. Codex (openai) destekler ama bağlanmadı; runtime istenirse 501
  `runtime.mcp_unsupported` döner — .NET zaten göndermez.
- **Sırlar.** `env`/`headers` değerleri hiçbir yanıta yazılmaz (`{ name, hasValue }`); güncellemede değeri `null` gelen
  satır kayıtlı değeri korur, listede olmayan ad silinir. Canlı bağlam görünümü yalnız ad + komut/adres gösterir.
- **Bağlantıyı dene** (`POST /mcp/{key}/test`): runtime sunucuyu açar, araçlarını listeler, kapatır (durumsuz, 45 s).
  Bağlanamamak hata değil sonuçtur (`ok: false` + neden). Runtime kapalıysa 503.
- **Maliyet notu.** Her aracın şeması her iç turda bağlama girer (2026-09-23 ölçümü: 48 araç ≈ 32K token/çağrı). Panel bunu
  söyler; gereken sunucuyu gereken ajana vermek kullanıcının kararıdır.
- Silme: bir ajana yetkiliyse 409 `mcp.in_use` (önce yetkiler kaldırılır; bilgi dosyası kuralıyla aynı).
- **Katalog (2026-09-23, kullanıcı isteği: "hazır MCP'ler; token, basic auth… hangisini destekliyorsa hepsi seçenek olsun").**
  `config/mcp-catalog.json` hazır sunucuları ve her birinin **bağlantı yöntemlerini** tanımlar: taşıma, komut/adres, form
  alanları (ortam değişkeni, başlık ya da yalnız şablon girdisi), `Bearer {value}` / `Basic {base64:E-POSTA:TOKEN}` biçimleri,
  seçimli alanlar (salt okuma), önkoşullar (Node.js, uv, Docker). Katalog `config/`'dadır çünkü sır taşımaz ve kaynak koddur;
  sırlar kurulumda kullanıcıdan alınır, **sunucuda** birleştirilir (`McpCatalogBuilder`), veritabanına yazılır. Yüklenirken
  denetlenir: bilinmeyen alana başvuran şablon ve **sır alanını argümana/adrese yazan** seçenek reddedilir (argüman yanıtta açık döner).
- İlk katalog (kaynaklar resmi belgelerden doğrulandı, `docsUrl`): **Figma** (PAT · OAuth belirteci · masaüstü Dev Mode ·
  uzak OAuth *henüz yok*), **Bitbucket** (Rovo MCP: e-posta+API token Basic · servis anahtarı Bearer (Bitbucket için
  doğrulanmadı) · OAuth *henüz yok*; yerel Cloud e-posta+token; Server/DC HTTP erişim belirteci; Server/DC parola ve Cloud
  uygulama parolası *henüz yok* — ilki npm'de yayımlı değil, ikincisini Atlassian Haziran 2026'da kaldırdı), **Jira** (Rovo MCP: e-posta+API token Basic · servis anahtarı Bearer · OAuth *henüz yok*;
  mcp-atlassian: Cloud e-posta+token · Server/DC PAT · Server/DC kullanıcı+parola), **Slack** (xoxp · xoxb · xoxc+xoxd ·
  resmi OAuth *henüz yok*), önerilen üç resmi sunucu: **GitHub** (uzak PAT · Docker · GHES), **Playwright** (Microsoft; görünmez /
  görünür tarayıcı), **Context7** (Upstash; güncel kütüphane dokümanı). Seçim gerekçesi: ofis kod yazıp test eden bir ekip —
  depo/PR, ön yüzü gerçek tarayıcıda denemek ve eski API tahminini azaltmak en çok işe yarayan üç yetenek.
- **Araç seçimi (2026-09-23, kullanıcı isteği).** Sunucu başına izin listesi (`tools`; boş = hepsi). Seçenekler son başarılı
  "Bağlantıyı dene"de görülen araçlardır (`knownTools`, sunucuda saklanır). Seçilmeyen araçlar SDK'ya `disallowed_tools` olarak
  gider — şeması bağlama hiç girmez — ve runtime'ın izin denetimi (`_guard`) izin listesi dışındaki `mcp__{sunucu}__{araç}`
  çağrısını ayrıca reddeder (iki kat: yeni eklenen, henüz görülmemiş bir araç da izin listesinde değilse açılmaz). Hiç araç
  seçilmediyse sunucu verilmez, kayda neden düşer. Tanım düzenlemesi seçimi ve OAuth girişini silmez.
- **Kullanım raporu (2026-09-23).** Her tur, ajana açılan sunucuları kaydeder (`Turn.McpServers`). `GET /mcp/usage` son N işte
  sunucu başına: verildiği tur, kullanıldığı tur, çağrı, iş, son kullanım; araç ve ajan kırılımı; silinmiş sunucuların geçmişi.
  "Verildi ama hiç kullanılmadı" işaretlenir: araç şemaları her iç turda ödenir, kullanılmayan sunucu boşuna bağlamdır. Şema
  tokeni bilinmediği için rapora tahmin yazılmaz.
- **OAuth girişi (2026-09-23, kullanıcı isteği).** Uzak (http/sse) sunucularda panelden "OAuth ile giriş yap". Akış .NET'tedir
  (Python durumsuz kalır): kimliksiz istek → 401 `WWW-Authenticate: resource_metadata` → korunan kaynak bilgisi (RFC 9728) →
  yetki sunucusu bilgisi (RFC 8414) → istemci (kullanıcının verdiği · aynı yetki sunucusu için saklanan · dinamik kayıt RFC 7591)
  → PKCE S256 + `resource` (RFC 8707) ile yetkilendirme → loopback dönüş `http://127.0.0.1:5080/api/v1/mcp/oauth/callback` →
  kod takası. Kapsam: önce `WWW-Authenticate: scope`, sonra korunan kaynağın `scopes_supported`, kullanıcı daraltabilir.
  Dönüş ucu JWT'siz açıktır; yetki tek kullanımlık, 15 dk'lık `state`'tir (bellekte). Belirteç sunucu kaydına yazılır, yanıta
  yazılmaz; her turda `Authorization: Bearer` olarak gider, 5 dk içinde dolacaksa tur **öncesi** yenilenir; yenilenemezse sunucu
  "giriş yok" diye atlanır (iş durmaz). Uzak uçlar https olmalı (yalnız loopback'te http).
  Canlı keşif (2026-09-23, salt okunur): Figma ve Atlassian dinamik kaydı ilan ediyor (token girmeden giriş); Slack ve GitHub
  etmiyor — kullanıcının kendi OAuth uygulamasının istemci kimliği/gizli anahtarı gerekir (dönüş adresi o uygulamaya eklenir).
  Alternatif (Claude Code'un saklı OAuth belirtecini paylaşmak) reddedildi: kullanıcının kişisel oturumu ajana sızardı.

## Projeler (2026-09-19, kullanıcı kararı)

> Her işin bir projesi vardır; proje bağımsız iş başlatılamaz (`run.project_required`).

- Proje kaydı (`project` tablosu): **başlık, açıklama, varsayılan iş akışı, hedef dizin** (`projects/{key}`
  varsayılan; depo içinde göreli, `..` yok). **Bütçe projede yoktur**, iş başınadır (kullanıcı kararı).
- İş projenin akışını devralır; formda değiştirilebilir. Hedef dizin, geliştirme yürütücüsü gelince developer'ın
  dosya yazacağı yerdir.
- Ekip ve bilgi dosyaları çalışma alanı düzeyinde ortaktır; proje yalnız bir akış seçer.
- Kart özeti (`ProjectCard`): iş sayıları duruma göre, toplam maliyet, son hareket. UI'da ray kartları bunu gösterir.
- **Silme (2026-09-20, kullanıcı kararı):** UI iki adımda onaylatır: önce "silinsin mi", sonra **dosyalar da silinsin mi**
  (onay kutusu). `DELETE /projects/{key}?deleteFiles=` → **süren** çalışma varsa (running / awaitingApproval / paused /
  awaitingInput) **409 `project.in_use`**; yoksa proje kaydı ve **bitmiş çalışmaların geçmişi** (çalışma satırı + turları, mesajları, fazları) birlikte silinir
  (varsayımla ilerlenir: proje kapsamlı UI'da projesiz geçmişin yeri yok; alternatif "geçmiş kalsın" sahipsiz kartlar üretirdi).
  `deleteFiles=true` ise hedef dizin de içeriğiyle silinir; depo dışına çıkamaz. Yanıt `ProjectDeleteResult`.
- **Hedef dizin seçimi (2026-09-20, kullanıcı kararı):** serbest metin **yok**; klasör seçici `GET /projects/dirs?path=` ile depo
  içini bir seviye bir seviye gezer (`.git`, `node_modules`, `bin`, `obj`, `runs`… gizli). Seçim: var olan klasör ya da
  gezilen klasörün içinde `<key>` adlı yeni klasör. Boş = varsayılan `projects/{key}`. İlke: **serbest metin yalnız ad ve
  açıklama alanlarında**; diğer alanlar seçici/liste.
- **Renk ve sıra (2026-09-20, kullanıcı isteği):** her projenin bir rengi (`color`, `#rrggbb`; boşsa paletten
  kullanılmayan ilk renk) ve sırası (`order`) var. Ray kartı, Kanban sekme grupları ve Kanban **"Tümü"** sekmesi
  (tüm projelerin görevleri dört Kanban şeridinde, kart rengi = proje) aynı rengi ve sırayı okur. Sıra
  `POST /projects/reorder { keys }` ile, renk proje ayarlarından değiştirilir.
- **Sahip:** proje ve çalışmada `ownerId`; bugün sabit `local`. Giriş JWT'sindeki `sub` claim'i ile
  doldurulması sonraki adım (bkz. Giriş).
- Geçiş: 2026-09-19'a kadarki projesiz çalışmalar **silindi** (kullanıcı kararı; hepsi deneme kaydıydı).

### Projeyi başlatma (2026-09-20, kullanıcı isteği)

Proje panelindeki **▶ Projeyi başlat** düğmesi, proje kökündeki **`run.cmd`** dosyasını yeni bir konsol penceresinde
açar (`POST /projects/{key}/launch`, `cmd /c start … cmd /k run.cmd`). Sözleşme tek dosyadır: ne başlatılacağını
**developer** yazar; `implement` prompt'u bunu ister (ASCII, `cd /d "%~dp0"`, konsol uygulamasında `pause`, web'de
sunucu + `start http://127.0.0.1:PORT`, kurulum gerekiyorsa o da içinde). Dosya yoksa düğme pasif (`launchable=false`)
ve 404 `project.launch_missing`. Süreç Api'ye bağlanmaz; çıktı pencerede, kapatmak kullanıcıda. İlk `run.cmd`
`hello-world-console` için elle yazıldı (varsayımla ilerlenir: Windows tek platform; Linux/mac gelirse `run.sh`).

## Çalışma yaşam döngüsü

```
POST /runs ──► Running(analiz) ──► AwaitingApproval ◄──┐
                                        │ approve      │ revise {note}
                                        ▼              │ (analist yeniden)
                                  Running(dağıtım) ────┘
                                        │
                     ┌──────────────────┼──────────────────┐
                     ▼                  ▼                  ▼
                  Paused            Completed         Failed / Interrupted /
        (yürütücüsü olmayan adım)                    BudgetExceeded / PolicyRejected
                     │                                      │ retry (kaldığı adımdan)
   cancel ───────────┴──► Cancelled ◄── cancel (Running / AwaitingApproval / Paused / Failed / Interrupted / BudgetExceeded)
                              │ retry
                              └──► Running ya da AwaitingApproval (onay beklerken iptal edilmişse)
```

`RunStatus` üyeleri (adıyla taşınır, **sona eklenir**): `Running, Completed, Failed, Interrupted,
BudgetExceeded, PolicyRejected, AwaitingApproval, Paused, Cancelled`.

- **Planı kim onaylar (2026-09-21):** akışın `planApprover` alanı. Varsayılan kullanıcıdır (`AwaitingApproval`).
  Bir ajan verilirse analiz biter bitmez o ajan planı inceler: kabul → dağıtım, red → geri bildirim revize notu
  olarak yazılır ve analist yeniden koşar. `maxReviewRounds` tur içinde onaylamazsa karar **kullanıcıya** düşer;
  sonsuz döngü yoktur. Bu bir adım değil akış özelliğidir: `analyze` çalışma başına bir kez koşar, görev başına
  değil, dolayısıyla görev düzeyindeki `review` ile ifade edilemez.
- **Geri dönüş hedefi (2026-09-21 genellemesi):** reddedilen iş, kendinden önceki en yakın **üretici** adıma
  döner (`design` ya da `implement`). Önceden yalnız `implement` aranıyordu; bu yüzden "tasarımı onayla,
  reddedersen tasarımcıya dön" kurulamıyordu. Kural tek kaynakta: `Workflow.ProducerBefore`.
- **Akış donar (2026-09-19):** çalışma başlarken seçilen akış çalışma satırına kopyalanır
  (`workflow_snapshot`); ayrıca `workflow_key` anahtarı tutulur. `config/workflows/` sonradan değişse de
  çalışma kendi kopyasını okur; pano da onu kurar (`workflow.set { key }` olayı).
- **Hassasiyet** çalışma başında verilir; boşsa `anthropic` (varsayımla ilerlenir: bugün tek
  sağlayıcı Anthropic). Politikaya aykırı bir ajan varsa çalışma **hiç başlamaz** (`PolicyRejected`).
- **Süreç ölürse** yeniden başlatmada `Running` olan çalışmalar `Interrupted` işaretlenir.

## Gelen kutusu (2026-09-19, kullanıcı isteği)

> "Bana atanan işleri, benden beklenen cevapları göreyim; soru olduğunu anlayayım; kaç iş var bileyim."

Kullanıcıdan bir şey bekleyen her çalışma **gelen kutusuna** düşer (`GET /runs/overview → inbox`, türü
`InboxKind`). Bu bir türetimdir, ayrı bir durum tutulmaz (CLAUDE.md §2: durum tek yerde):

| Tür | Ne zaman | Beklenen cevap |
|---|---|---|
| `approval` (soru) | `AwaitingApproval` | **Onayla** ya da **Revize et** (not) |
| `decision` (karar) | `Failed`, `Interrupted`, `BudgetExceeded` | **Yeniden dene** (`retry`) ya da **Kapat** (`cancel` → `Cancelled`). Kapatmadan kutudan düşmez — "okundu" yok, karar var |
| `question` (soru) | bir ajan `kind: ask, to: user` yazdı ve `ref`'i eşleşen `answer` yok | cevap ucu **henüz yok** — sözleşme ileride ajan → kullanıcı sorusu için hazır |

`Completed`, `Cancelled`, `PolicyRejected` bir şey beklemez. **`Paused` de beklemez** (2026-09-19, kullanıcı
sorusu "cevap bekleniyor ama işlem yapamıyorum"): yürütücüsü olmayan adımda duran çalışma akışın eksikliğidir,
kullanıcıdan bir karar istemez; proje kartında "durakladı" sayısı ve iş panelinde durum olarak görünür, kutuya
düşmez. Developer yürütücüsü gelince bu durum zaten oluşmaz.
UI kutuyu **işlerin dışında**, üst bardaki **bildirim zili** altında gösterir (madde + "Cevapla" → çalışma
paneli); ayrıca İşler düğmesinde kırmızı rozet, sekme başlığında `(N)`, sahnedeki panoda rozet, proje kartında
"senden bekliyor" ve büyük Kanban'da **bağımsız "Senden bekleniyor" sütunu** (2026-09-20, kullanıcı isteği: sekmeden
bağımsız, boşken de durur, zille aynı kaynak; tıklanınca çalışma açılır).
**Varsayımla ilerlenir:** gelen kutusu 5 s'de bir yoklanır (SSE gelince olaya bağlanır); "okundu" kavramı yok,
madde ancak cevap verilince düşer.

## Ekler (2026-09-23, kullanıcı isteği; ayrıntılar varsayımla ilerlenir)

İstek: "İş verilirken PDF vb. doküman, resim verilebilsin."

- **Akış.** Dosya seçilince (seç / sürükle-bırak / brief'e ekran görüntüsü yapıştır) hemen yüklenir: `POST /attachments`
  → `data/attachments/_staging/` + kimlik. İş gönderilirken kimlikler `POST /runs` gövdesine (`attachments[]`) girer;
  `RunService.CreateAsync` dosyaları `data/attachments/{runId}/`'ye taşır, üstveri `run.attachments` (JSON) sütununa yazılır.
  Bilinmeyen/süresi dolmuş kimlik iş başlamadan 400 `attachment.not_found`. Bir günden eski geçici yüklemeler silinir.
- **Türler ve sınır** (`AttachmentRules`, tek kaynak): PDF, resim (png/jpg/gif/webp), Word (docx), metin (md, txt, csv,
  json, xml, yaml, log, html, sql). Dosya başına 20 MB, iş başına 10 ek. Ad sunucuda güvenli hâle getirilir (yol parçası,
  özel karakter atılır; çakışmada `ad-2.pdf`).
- **İçerik isteme gömülmez** — her iç turda yeniden ödenirdi. Analiz ve görev istemlerinde `# Ekler` bölümü yalnız ad, tür,
  boyut ve **mutlak yol** listeler; ajan gerektiğinde Read ile okur (Claude'un Read aracı PDF'i sayfa sayfa, resmi görüntü
  olarak okur). Word'ü Read okuyamaz: Api taşırken metnini çıkarır, yanına `.docx.txt` yazar, istem onu gösterir.
- **Okuma izni.** Ekler çalışma dizininin dışındadır: araçlı turda `readDirs` = ek dizini (SDK `add_dirs`). Yazma araçları
  yine yalnız cwd'de; Bash ek dizinindeki mutlak yolu kullanabilir (ör. logoyu projeye kopyalamak). Ek dizini bir **silme**
  denetiminden geçmez — risk yalnız o çalışmanın kendi ekleri (varsayımla ilerlenir).
- **Sağlayıcı.** PDF/resim yalnız Claude (Read aracı) ile okunur; Codex yolu metin eklerini okuyabilir, PDF/resmi okuyamaz.
- **Silme.** Proje silinince çalışmalarıyla birlikte ek dizinleri de silinir. Revize/devam brief'ine ek eklemek bu turda yok.
- İndirme: `GET /runs/{id}/attachments/{fileName}` (JWT'li; UI Blob alır, PDF/resmi yeni sekmede açar).

## Plan onayı (2026-09-19, kullanıcı kararı)

> Developer'a iş gitmeden analistin çıkaracağı iş listesi, sırası ve detayları **insan onayından**
> geçer. Onaysız hiçbir adım başlamaz, panoya iş açılmaz. İstisna yoktur.

1. Analist brief'i alır, `Spec` şemasıyla yapısal çıktı üretir → çalışma satırının `spec` alanı.
   Çalışma `AwaitingApproval` olur; sahnede analist `done`, not: "plan onay bekliyor".
   Plan, maliyet için üç isteğe bağlı alan taşır: görev başına `ruleRefs` (yalnız o görevi bağlayan kurallar),
   `knowledge` (işin gerçekten ihtiyaç duyduğu bilgi dosyaları) ve **`codeMap`** (2026-09-24): analizin okuduğu
   dosyalardan uygulayıcının bilmesi gerekenler, yol + tek satır öz. Harita her görevin istemine "Kod haritası"
   olarak girer — önce görevin kendi dosyaları, **3000 karakterde kesilir** (her iç turda yeniden okunur; büyürse
   kazançtan çok maliyet olur). Uygulayıcıya "anlamak için yeniden okuma, yalnız değiştireceğini aç" denir.
   Gerekçe: bir görev analizin okuduğu 11 dosyanın 6'sını yeniden okudu; analiz maliyetin %15'i.
2. Kullanıcı UI'da planı görür: özet, mimari, kurallar, görevler (kimlik, başlık, açıklama,
   dosyalar, kabul ölçütleri, bağımlılıklar) ve **yürütme sırası** (topolojik).
3. **Onayla** → `Running`; `board.set` ile görevler ilk görev-sütununda `queued` açılır; dağıtım başlar.
4. **Revize et** (`note` zorunlu) → not mesaj kaydına yazılır (`from: user, to: analyst,
   kind: Note, subject: plan-revision`). Analist **geçmişle** yeniden çağrılır: `[brief] →
   [önceki plan JSON] → [not]`. Yeni plan `spec` alanının üzerine yazılır (eski sürümler
   analistin tur kayıtlarında tam metinle durur). Çalışma yine `AwaitingApproval`.
   Döngü onaya kadar sürer; tur sınırı **yoktur** (insan döngüyü kendi bitirir).
5. `AwaitingApproval` dışında `approve`/`revise` → **409 `run.not_awaiting_approval`**.

## Dağıtım: organizatörün kuralı (2026-09-19, kullanıcı kararı)

Kod (`Application/Runs/Dispatcher`), her tetiklemede (onay, bir adımın bitişi) çalışır:

- **Hazır görev:** `dependsOn` içindeki tüm görevler `done`. Sıra: `TaskGraph.Order` (topolojik,
  analist sırası korunur).
- **Boş ajan:** o anda `Started` bir fazı olmayan ajan. **Ajan başına tek iş**; farklı ajanlar
  paralel çalışabilir (developer bir görevi kodlarken testçi başka görevi denetler).
- Hazır görev + hedef adımın ajanı boş → **atama**:
  1. `handoffRole` varsa organizatör **devir notu** üretir (1 LLM turu; girdi: görev, önceki adımın
     çıktısı/özeti, hedef rol). Not mesaj kaydına `kind: Handoff, from: organizer, to: <rol>`
     yazılır ve hedef rolün prompt'una **girdi** olur. `handoffRole: null` ise not yok, atama sessiz.
  2. Faz kaydına `Started` (adım, ajan, tur) yazılır.
  3. Sahne: `meet { from: organizer, to: <rol>, kind: handoff }`, `board.move { task, stage,
     state: active }`, `agent.state { <rol>, working }`, `run.stage`.
- Hazır görev var, ajanı dolu → görev `queued` bekler; ajan boşalınca sıra ona gelir.
- Hazır görev yok → organizatör `agent.state idle` (sahne: koltuğa oturur ya da kapıdan çıkar; bunu
  UI'ın ambient turu yapar, RunService karar vermez).
- Token: organizatör `haiku`, efor `low` (`organizer.md` frontmatter). Devir notu kısa tutulur.

## Paralellik, tekrar, iptal, bütçe (2026-09-19, kullanıcı kararları)

- **Birden fazla çalışma aynı anda.** İş kanalı bir havuzdur (`AITeam:MaxParallelJobs`, varsayılan 3; 1 =
  eski sıralı davranış). İki kilit: **çalışma başına tek iş** (aynı `run.json`'a iki yazıcı olmaz; kanal kilidi)
  ve **ajan başına tek LLM çağrısı** (`AgentCaller` kilidi: analist A'da konuşurken B'nin analizi bekler).
  Dağıtımda "boş ajan" çalışmalar arası hesaplanır: başka bir **Running** çalışmada `Started` fazı olan ya da
  o an LLM çağrısında olan ajan dolu sayılır; **Paused** çalışmaların ajanları sayılmaz (yürütücüsüz bekleme
  kimseyi kilitlemesin). Hazır görev var ama ajanı doluysa çalışma `Running` kalır, `waitingSince` işlenir ve **işi
  kuyruktan çıkar** (`detail` "ajan bekleniyor" der). **Uyandırma (2026-09-20):** herhangi bir çalışmada bir adım
  kapanınca, dağıtım döngüsü bitince ya da iptalde `RunService.WakeWaitingAsync` bekleyen çalışmaları `IRunScheduler`
  ile yeniden dağıtıma koyar; emniyet olarak `RunResumer` 2 dk'dan eski bekleyenleri dakikada bir tarar. Yeniden
  başlatmada bekleyen çalışma `Interrupted` olmaz (ortada yarım LLM çağrısı yok), dağıtım kuyruğa geri girer.
- **Otomatik tekrar (geçici hatalar).** `RetryPolicy` (3 deneme, 3 s · 6 s artan bekleme): runtime kapalı,
  429, 5xx, zaman aşımı, ağ. Şema/politika/4xx gibi kalıcı hatalar hemen düşer. Her deneme mesaj kaydına
  `subject: retry` notu yazar; sahnede ajan "yeniden deneniyor 2/3" der.
- **Elle tekrar.** `POST /runs/{id}/retry` kaldığı adımdan sürer (bkz. yaşam döngüsü ve Gelen kutusu);
  `Retries` sayacı artar, `subject: retry` notu düşer. **Kaldığı adım `run.step`'tir** (`analyze | approval |
  dispatch`; 2026-09-20): `detail` metninden çıkarım yapılmaz, metin yalnız görünüm içindir. `approval` = onay
  beklerken iptal/kesinti; tekrar `AwaitingApproval`'a döner, kuyruğa iş girmez. Hangi adımın hangi işi doğurduğu
  tek yerde: `RunScheduler` (Api) — uçlar, `RunResumer` ve `RunService`'in uyandırması aynı kapıdan geçer.
- **İptal.** Durum hemen `Cancelled` yazılır; süren işin belirteci kesilir (LLM çağrısı durur), dönen sonuç
  yazılmaz (`WasCancelled` denetimi). `Started` fazlar `Skipped · iptal` ile kapanır; ajanlar boşa döner.
- **Bütçe.** `POST /runs { maxCostUsd }` (boş = sınırsız; ≤0 → 400 `run.budget_invalid`). Her LLM turundan
  sonra toplam maliyet sınırla karşılaştırılır; aşılınca çalışma **`BudgetExceeded`** ile durur, neden
  `detail` ve `subject: error` notunda (CLAUDE.md §4: uyarıyla geçmez). Tekrar ile sürdürülebilir.
- **Görünürlük.** Büyük Kanban panosunda sekmeler **Tümü | proje | proje …** (2026-09-20 kullanıcı kararı; "Sahne"
  sekmesi kaldırıldı): dört Kanban şeridi, kartlar çalışma detayından türetilir; Tümü'de proje proje, proje sekmesinde
  çalışma başına gruplu. Eski tarif: sekmeler Sahne + her aktif çalışma;
  çalışma sekmesinin sütunları dondurulmuş akıştan, kartları plan + fazlardan türetilir (yenilemede kaybolmaz).
  Ajan panelinde **Özellikler / İşler** sekmeleri: İşler, ajanın çalışma başına her turunu (gönderilen metin,
  çıktı, o anki sağlayıcı/model/efor), notlarını ve fazlarını gösterir (`GET /agents/{key}/work`).
  Not: modelin düşünme (reasoning) metni Agent SDK'dan gelmiyor; yalnız sayısı (`thinking_tokens`) biliniyor.

## Adım yürütücüleri

Tamamı çalışır (2026-09-20). **Kullanıcı kararı (2026-09-19): ajanlar dosyayı kendisi yazar, testi kendisi koşar** —
Agent SDK araçlarıyla (Read/Glob/Grep/Write/Edit/Bash), projenin hedef dizininde. Alternatif (yapısal `files[]`
çıktısı → .NET yazar) reddedildi: model build'i görmeden kod yazıyor, tur sayısı katlanıyor.

| `kind` | Araçlar | Ne yapar | Çıktı şeması |
|---|---|---|---|
| `analyze` | salt okuma | Analist dizine bakar, `Spec` üretir, plan onayı döngüsü | `SpecSchema` |
| `handoff` / `handoffRole` | yok | Organizatör devir notu (yukarıda) | metin |
| `design` | salt okuma | Tasarımcı developer'a rehberlik notu yazar (`design` mesajı), kod yazmaz | `guidance, decisions[]` |
| `implement` | tam | Developer dosyaları yazar, build/test komutlarını koşar; sonunda rapor. `blocked=true` → kullanıcıya soru | `summary, filesChanged[], commandsRun[], blocked, question` |
| `review` | tam | Testçi / manager kodu okur, testleri **fiilen** koşar (test dosyası yazabilir, uygulama kodunu değiştirmez); kabul ya da gerekçeli red | `verdict, testsRun, findings[], feedback, commandsRun[]` |

**Sınırlar.** Araç listesi ve çalışma dizini .NET'ten gelir (`ToolAccess.ForKind`); runtime seçmez. Dosya yazan
araçlar (`Write/Edit/MultiEdit/NotebookEdit`) yalnız hedef dizinin **içine** yazar; **Bash** komutlarında dizin
dışına çıkan mutlak yol, `~` ve `..` reddedilir (`/dev/null`, `/tmp` serbest). İkisi de SDK izin geri çağrısında
(`can_use_tool`) kararlaşır, ajan ret mesajını görür ve yolunu düzeltir (2026-09-20: ilk koşuda developer'ın
kullanıcı profilinden dosya okuduğu görüldü, kapatıldı). Her araç çağrısı (araç + hedef) turun kaydına yazılır
(`Turn.toolUses`), günlükte "Araçlar" olarak görünür. Emülatör/tarayıcı testleri için ajan Claude Code'un kendi
araçlarını kullanır (ileride MCP).

**Canlı araç akışı (2026-09-20, kullanıcı isteği):** `AgentCaller` araçlı tur için tek kullanımlık belirteçli bir
geri çağrı adresi (`progressUrl`) üretir; runtime her araç çağrısında `{tool, target}` POST eder (2 s zaman aşımı, hata
yutulur; turu etkilemez). Api `agent.tool` yayımlar: sahnede balon, çalışma panelinde "Şu an" şeridi. Belirteç tur
bitince silinir; runtime iş kuralı bilmez, yalnız "araç X hedef Y" der. OpenAI/Codex yolunda olaylar tur sonunda
ayrıştırıldığı için canlı akış yok (varsayımla ilerlenir).

**Devir notu yalnız `implement` atamasında** (ilk ve red sonrası): inceleme adımlarına not bilgi katmıyordu, her
geçiş bir organizatör turu ediyordu (ölçüldü: 14–70 s, ≈$0.02–0.05).

**Yarım kalan adım.** Süreç yeniden başlar (`Interrupted`) ya da kullanıcı iptal ederse `Started` faz **`Failed`**
ile kapanır (`süreç yeniden başladı` / `iptal`); "yeniden dene" aynı adımı yeniden koşar. `Skipped` kullanılmaz:
o bir sonraki adıma geçirir ve kod yazılmadan test başlar. Sistem kaynaklı bu fazlar **`phase.cause`** ile işaretlenir
(`limit | cancelled | interrupted`; ajanın kendi sonucu `agent`/boş) ve tur sayılmaz, tavana girmez — karar `detail`
metnine değil bu alana bakar (2026-09-20).

**Yürütme döngüsü.** `DispatchAsync`: planla → bir adımı koş → yeniden planla; hazır iş kalmayınca ya da durum
`Running`'den çıkınca döner. Çalışma **içinde sıralı** (çalışma başına tek iş, kanal kilidi), **çalışmalar arası
paralel** (JobWorker havuzu). Tur = o adımda bitmiş deneme sayısı + 1.

### Geri dönüş kuralı (2026-09-20, varsayımla ilerlenir)

- **Red** (`Rejected`) → görev en yakın önceki `implement` adımına döner; aradaki adımlar yeniden koşar.
  Testçinin `feedback`'i developer'a `review-feedback` notu olarak gider ve bir sonraki turun prompt'una girer.
- **Tavan** `maxReviewRounds` **kapı başına** sayılır: aynı `review` adımı bu kadar kez reddedince akış durmaz,
  **kullanıcıya sorulur** (aşağıda Takılma). Aynı adımda üst üste `Failed` için de aynı tavan.
- **Hata** (`Failed`) → aynı adım yeniden (yeniden dene ya da cevap sonrası). Limit beklemesinden doğan `Failed`
  (`cause: limit`) tur sayılmaz.
- **Tek kaynak:** "en yakın önceki `implement`" kuralı `Workflow.ImplementBefore`'dadır; dağıtıcı (`Dispatcher.NextStage`)
  ve pano hedefi (`RunService.BoardTarget`, kapanan fazı `NextStage`'e verir) oradan okur. UI'daki türetim (`~/api/board`)
  aynı kuralın TypeScript kopyasıdır; kural değişirse ikisi birlikte değişir.

## Takılma: soru + müdahale (2026-09-19, kullanıcı kararı)

> "Flow'da sorun olursa soru gelsin; çözüm için müdahale imkânı varsa sunsun, yoksa ben geliştirme yaparım."

Akış kendi çözemediği yerde durur, çalışma **`AwaitingInput`** olur, soru `run.json → question` alanında
(`{ ts, agent, text, options[], task, stage, context }`), kaydı mesajlarda (`ask`, subject `question`).
Bildirim zilinde ve gelen kutusunda **soru** olarak görünür; çalışma panelinde seçenekler düğme olur.

| Ne zaman | Soran | Seçenekler |
|---|---|---|
| Developer `blocked=true` (eksik/çelişkili bilgi) | developer | **retry** (not zorunlu: cevap developer'a gider, aynı adım yeniden) · **skip** (elle hallettim: sonraki adım) · **cancel** |

**Ajan → ajan sorusu (`can_ask`, 2026-09-20):** developer `blocked=true` dediğinde soru **önce** md'sindeki `can_ask`
hedefine (bugün `manager`) gider: 1 LLM turu, yalnız okuma aracı, şema `{ answer, escalate, reason }`. Kayıt
Mesaj kaydında `ask` (developer → manager) + `answer` (manager → developer, aynı `ref`); faz `Failed` ("soru → manager")
olur, aynı adım yeniden koşar ve cevap notlar arasında gider. **Kullanıcı hiçbir şey görmez.** Kullanıcıya düşen hâller:
`can_ask` yok · manager `escalate=true` (sebep soru bağlamına eklenir, günlükte `escalate`) · manager hata verdi (günlükte
`error`) · **aynı görevde ikinci takılma** (varsayımla: manager görev başına bir kez sorulur; sonrası kullanıcıya). Limit
manager turunda dolarsa adım limit fazı olur, sürdürmede developer yeniden koşar. Sahnede developer manager'a `?` balonuyla
yürür (`meet: ask`); üst üste hata tavanı (`maxReviewRounds`) bu fazları da sayar.
| Review tavanı aşıldı | testçi / manager | **retry** (notla bir tur daha) · **skip** = olduğu gibi kabul et (adım `Skipped`, sonraki adım) · **cancel** |
| Aynı adımda `maxReviewRounds` kez hata | ilgili ajan | **retry** · **skip** · **cancel** |

Seçenek kimlikleri sabittir (`retry | skip | cancel`), etiket bağlama göre değişir. Cevap `POST /runs/{id}/answer
{ choice, note? }`; `answer` mesajı yazılır (`ref` soruya bağlanır), `skip` ilgili adıma `Skipped` fazı ekler
(ajan `user`), `retry` yalnız durumu `Running` yapar (dağıtıcı `Failed` → aynı adım, `Rejected` → developer),
`cancel` iptal eder. Müdahale imkânı olmayan durum (geçici olmayan sağlayıcı hatası) eski **karar** akışında kalır:
`Failed` → yeniden dene / kapat; kullanıcı düzeltmeyi kodda yapar.

## Bütçe ve limit (2026-09-19, kullanıcı kararı)

- **Maliyet eşdeğerdir.** SDK'nın döndürdüğü `costUsd` API liste fiyatına göre hesaplanır; Claude Code
  aboneliğiyle **ücret kesilmez**, kota penceresi tükenir. UI her yerde `≈$` yazar ve bunu söyler. İş başına
  `maxCostUsd` tavanı isteğe bağlı kalır ve bu eşdeğer rakamla ölçülür (karşılaştırma için kullanışlı).
- **Soruyu kim cevaplar (2026-09-21):** akışın `askRole` alanı. `null` = ajanın kendi `can_ask`'i (eski
  davranış), `"user"` = doğrudan kullanıcı, ajan anahtarı = o ajan. Açık karar #4 böyle kapandı: `can_ask`
  ajan özelliğiydi, dolayısıyla manager'ı olmayan bir akışta bile developer manager'a soruyor ve akışta
  olmayan bir ajanı (ve maliyetini) işe sokuyordu.
- **Proje bütçesi (2026-09-22 kullanıcı kararı):** `Project.maxCostUsd` ve `Project.maxTokens`; ikisi de
  **boş = sınırsız** (varsayılan davranış değişmedi). İş bütçesinden farkı **kapsamdır**: iş bütçesi tek bir işi,
  proje bütçesi projenin **bütün çalışmalarının toplamını** sınırlar — on küçük iş üst üste aynı tavanı on kez
  harcayamasın. İki ölçü bağımsızdır ve **önce dolan durdurur**: abonelikte ücret kesilmediği için asıl tükenen
  kaynak token'dır, `$` eşdeğer maliyettir (CLAUDE.md §4). Tavan dolmuşsa **yeni iş hiç başlamaz**
  (400 `project.budget_exceeded`) — başlayıp ilk turdan sonra durmak bir tur token'ı boşa harcardı; süren iş
  tur sonunda `BudgetExceeded` olur ve `Detail` hangi ölçünün dolduğunu yazar. Harcama `run.input_tokens` /
  `run.output_tokens` sütunlarında birikir (tur başına kırılım `run_turn`'de kalır); 2026-09-22 öncesi işlerde
  bu sütunlar 0'dır: **ölçülmedi** demektir, sıfır harcandı demek değil.
- **Kesilen tur da harcamadır (2026-09-23):** runtime tur sürerken mesaj başına kullanımı bildirir (`progress`,
  `kind=usage`). Tur kesilirse (tur bekçisi, iptal, sağlayıcı hatası, çağrı sırasında limit) biriken kullanım
  `run_turn`'e `cutShort=true` ile yazılır, maliyet `config/models.json → prices` tablosundan tahmin edilir (fiyat
  yoksa null: token yazılır, `$` ölçülemedi) ve çalışmanın toplamına (bütçe) eklenir. İstemci bağlantıyı keserse
  runtime turu **durdurur**: önceden `claude.exe` kimse beklemeden işi bitiriyordu (kayıtsız ~3 $, aynı dizinde iki ajan).
- **Kim ne harcadı:** kota kaynağa göre ayrılmaz; `GET /usage/split` makinedeki CLI kayıtlarından ofis ajanı ile
  Claude Code oturumlarını ayırır ve haftalık yüzdeyi eşdeğer `$` oranıyla böler (docs/API.md).
- **Limit koruması** (asıl koruma): ayarlarda `limitGuards { provider: yüzde }`, varsayılan
  **%99**, Ayarlar ekranında platform bazında değiştirilir. Her LLM çağrısından önce (`LimitGuard`) sağlayıcının
  aktif kota pencereleri (saatlik, haftalık, modele özel) okunur (runtime 90 s önbellek); biri eşiğe ulaştıysa çağrı
  **yapılmaz**: çalışma `Paused` + `resumeAt` (pencerenin sıfırlanma zamanı), `limit` notu, bildirim zilinde
  "Limit doldu · HH:mm'de sürer". Pencere **çağrı sırasında** dolarsa (koruma yüzdeleri 90 s önbellekli,
  o aralıkta dolabilir) runtime bunu `runtime.provider_limit` diye ayrı sınıflandırır ve çalışma yine
  `Paused` olur — `provider_error` sanılıp `Failed` olsaydı pencere sıfırlandığında kendiliğinden sürmez,
  kullanıcı elle "yeniden dene" demek zorunda kalırdı (2026-09-22). Bu turda tekrar denenmez: sıfırlanma
  dakikalar ya da günler sonradır. `RunResumer` dakikada bir bakar, süresi gelen çalışmayı kendisi sürdürür
  (`ResumeAsync`: kullanıcı tekrarı sayılmaz, not organizatörden `limit-resume`); kullanıcı "Yeniden dene" ile
  erken deneyebilir. Kota ucu bilgi vermiyorsa koruma sessizce geçer (varsayımla
  ilerlenir; üst bar zaten "kalan kullanım yok" der).

### Bağlam bütçesi (2026-09-23, kullanıcı onayı; ölçüm varsayımla ilerlenir)

Taşınan görev geçmişinin (`AgentTaskHistoryAsync`) ne zaman ve ne kadar sıkıştırılacağına **kod** karar verir;
LLM özeti (üçüncü kademe) **yoktur**, ölçüm onu hak ettiğini gösterene kadar kurulmaz.

- **Tavan maliyet tavanıdır, pencere tavanı değil.** `CompactionBudget.TaskHistory` 4 mesaj / 12k token kalır.
  Modelin penceresine oranlayıp büyütmek (ör. 200k'nın %70'i) **reddedildi**: taşınan geçmiş araçlı adımda her
  iç turda yeniden gönderilir (120 iç tura kadar), tavanı büyütmek girdiyi tur sayısıyla çarpar. Pencere yalnız
  küçük pencereli (yerel) modellerde bağlardı; bugün tüm ajanlar Anthropic'te. Yerel modele geçilirse ajan
  frontmatter'ına pencere alanı + `min(maliyet tavanı, pencere payı)` — o gün.
- **Token ölçüsü kalibre edilir.** Sabit "4 karakter = 1 token" yerine ajanın hedef modeli için kayıtlı
  **araçsız** turlardan (`Turn.toolsOffered = false`) karakter/token oranı çıkarılır (`TokenCalibration`). Düz
  oran kullanılmaz: girdi tokeni iç turların toplamıdır ve CLI istemde görünmeyen sabit ek yük koyar; tur başına
  girdi istem karakterine karşı doğrusal oturtulur, **eğim** oranı verir. En az 8 örnek, yeterli yayılım ve
  1,5–6 aralığı yoksa oran **4** kalır: ölçüm gelene kadar davranış değişmez.
- **Ölçü kayda girer.** Geçmiş taşıyan her tur `Turn.context`'e sıkıştırma öncesi/sonrası mesaj ve karakteri,
  kullanılan oranı ve örnek sayısını yazar. Okuma: `python scripts/context-report.py`.
- **Görev başında yazılan kod (2026-09-24, kullanıcı kararı: önce kodla, LLM yok).** Uygulama isteminde
  "Bu işte şimdiye kadar yazılan kod": çalışmanın **ilk** anlık görüntüsünden (kapanmış fazların en eskisi) görevin
  başlangıcına gölge depo farkı + değişen kod dosyalarının imzaları (`CodeDigest`: C# tür/genel üye/uç, TS/JS/Vue
  dışa aktarılan + makrolar, Python üst düzey + genel metot + rota). Kaynak ajanın raporu değil **disk**: Bash'le
  yazılan dosya da girer. Paket/derleme dizinleri ve kilit dosyaları farka hiç girmez; görevin dosyaları önce,
  **3000 karakterde kesilir** (kod haritasıyla aynı gerekçe). İlk görevin ilk denemesinde ya da git yoksa bölüm
  yoktur. Gerekçe (4 iş/14 görev): sonraki görevlerde 57 Read'in 18'i önceki görevin dosyasına, 12'si düzenlemeden.
  Ölçüm: sonraki koşularda bu 12 düştü mü (`toolUses`), istem büyümesi iç tur başına girdiye oranla ne kadar.
- **Üçüncü kademe (LLM özeti) için koşul:** raporda düşen geçmişin, özet turunun maliyetini aşacak kadar sık ve
  büyük olduğu görülmeli. Kurulursa `config/agents/` altında ucuz modelli bir ajan olur, `AgentCaller` üzerinden
  çağrılır (tur kaydı, bütçe ve limit koruması kendiliğinden).
- **Tek ajanlı ekipte bu katman UYKUDADIR (2026-09-24 ölçümü, bilinçli).** 4 gerçek işin 18 turunda taşınan geçmiş
  **0**, araçsız tur **0** (kalibrasyon örneği yok → oran 4'te). Geçmiş yalnız red döngüsünde taşınır, tek ajanlı
  akışta red yok. Kod silinmedi: çok rollü akış geri gelirse kendiliğinden devreye girer. Yatırım yapılmaz.
- **Bağlam asıl CLI oturumunun İÇİNDE büyür**: araç sonuçları, yazılan kod, düşünme. Ölçülen tepe tur başına 95–168K
  token; CLI'nin kendi sıkıştırması hiç tetiklenmedi. Bu yüzden ölçü `Turn.peakContextTokens`'tır (turdaki en büyük
  tek API çağrısı; `inputTokens` iç turların toplamı olduğu için büyümeyi söylemez) ve kaldıraçlar istem tarafındadır:
  ajan md'sinde **okuma disiplini** (önce ara sonra dar oku, dosyaları topluca `cat` etme, taşan çıktıyı baştan
  sona okuma), plandaki **kod haritası**. Etki `context-report.py`'nin ikinci bölümünden okunur.

### İstem önbelleği ömrü (2026-09-24, kullanıcı onayı; varsayılan değişmedi)

- Ölçüm: CLI **tüm** önbellek yazmalarını 1 saatlik yapıyor (oturum kayıtlarında `ephemeral_5m` = 0). 1 sa yazma baz
  girdinin 2 katı, 5 dk yazma 1,25 katı. 4 işte ($35) yazma %40, çıktı %38, okuma %22; 5 dk ile hesapta **~%15** eder.
- **Ayar:** Ayarlar → İstem önbelleği (`app_settings.cacheTtl`): boş = CLI varsayılanı · `5m` · `1h`. Yalnız Anthropic'e
  gider; runtime yalnız CLI değişkenine eşler (`FORCE_PROMPT_CACHING_5M` / `ENABLE_PROMPT_CACHING_1H`), karar .NET'te.
- **Bedeli:** iç turlar saniyeler arayla gelir, ama 5 dk'yı aşan bir araç çağrısından (npm install, uzun test) sonra
  bağlamın tamamı yeniden yazılır; görevler arası ortak önek de (~20K) 5 dk'dan uzun arada düşer. Net etki ölçülmeli.
- **Ölçü:** runtime yazmanın 5 dk payını ayırır (`Turn.cacheWrite5mTokens`), istenen ömür `Turn.cacheTtl`'a yazılır.
  Fiyat tablosunda `cacheWrite5m` (yoksa girdi × 1,25): kesilen turun tahmini ve "Kim ne harcadı" bununla doğru kalır.
  Abonelikte eşdeğer $ düşse de **kotanın aynı oranda düştüğü ölçülmedi**.
- Karar ölçümden sonra: bir iş 5 dk ile koşar, `context-report.py` + kota yüzdesi karşılaştırılır; iyiyse varsayılan olur.

## Model, efor ve kimlik (2026-09-19, kullanıcı kararı)

- Ajan frontmatter: `provider`, `model`, `effort` (`low | medium | high | max`). Boş → varsayılan.
- **Varsayılanlar:** sağlayıcı `anthropic`, model **`claude-opus-5`** (kullanıcı kararı), efor
  `high` (varsayımla ilerlenir; v1 analist/kontrolcü `high` kullanıyordu). `organizer.md`:
  `claude-haiku-4-5-20251001` + `low` (dağıtım ucuz kalsın).
- **Anthropic kimliği Claude Code oturumudur.** Ayrı API anahtarı yoktur; runtime Agent SDK ile
  makinedeki `claude.exe` oturumunu kullanır. Farklı Windows kullanıcıları farklı oturumdur; bu
  yüzden **UI ilk yüklemede** `GET /api/v1/providers` ile giriş durumuna bakar. Sayfayı kaplayan bant
  yalnız **hiçbir** sağlayıcıda giriş yokken çıkar (2026-09-20 kullanıcı kararı): o zaman iş başlatmak
  imkânsızdır. Biri çalışıyorsa kurulmamış sağlayıcı (ör. Codex) büyük uyarı olarak durmaz; üst bardaki
  soluk çip "giriş yok" der ve tıklanınca Ayarlar'ı açar.
- **Ayarlar ekranı (2026-09-19, kullanıcı isteği):** LLM bağlantıları tek yerden yönetilir: giriş
  durumu, hesap, modeller, **tek tıkla giriş** ve çıkış. Giriş, runtime'ın kullanıcının makinesinde
  `claude auth login` başlatmasıyla olur (yeni konsol penceresi + tarayıcı onayı); şifre/token
  hiçbir katmandan geçmez, CLI kendi OAuth akışını yürütür. UI girişten sonra 3 dakika boyunca
  5 s'de bir `refresh=true` ile yoklar ve tamamlanınca gösterir.
- **Kullanım görünümü:** `GET /api/v1/usage` bizim kayıtlarımızdan (kayıtlı turlar: tur, token,
  sağlayıcının bildirdiği maliyet) sağlayıcı+model bazında toplar.
- **Kota kullanımı (üst bar, 2026-09-19 kullanıcı isteği):** `GET /api/v1/limits` aktif her sağlayıcı
  için kota pencerelerini verir (Anthropic: saatlik oturum, haftalık, modele özel pencereler). Halka
  **kullanılanı** gösterir (2026-09-20 kullanıcı kararı): sağlayıcının kendi `/usage` ekranı da böyle
  okur, "%87 doldu" ile "%13 kaldı" arasında gidip gelmek karışıklığı büyütüyordu. Modele özel pencerenin
  adı `scope`'tan gelir; bu alan nesne döner ve düz string bekleyen okuyucu onu sessizce düşürüp pencereyi
  haftalıkla aynı etikete bindiriyordu.
  Kaynak Claude Code'un kendi `/usage` ekranının okuduğu uçtur; CLI bunu komut olarak sunmadığı için
  runtime, CLI'nin makinede sakladığı oturumla aynı ucu sorgular. Belirteç hiçbir yanıta ve günlüğe
  yazılmaz. Uç belgesiz olduğu için şekli değişebilir; değişirse bar "kalan kullanım yok" der, akış
  etkilenmez — bilinçli risk.
- Model listesi (Anthropic): `claude-fable-5-1`, `claude-opus-5`, `claude-sonnet-5`,
  `claude-haiku-4-5-20251001`. NVIDIA, Ollama sonra eklenir; sözleşme (`provider` enum'u)
  buna hazır, üye **sona** eklenir.
- **OpenAI (2026-09-20, kullanıcı isteği):** `provider: openai`, hedef `openai` (hassasiyet `Anthropic` olan
  çalışma OpenAI'ye çıkamaz; `Open` çıkar). İki kimlik yolu, öncelik sırasıyla:
  1. **API anahtarı** kayıtlıysa: OpenAI Responses API doğrudan; fatura OpenAI hesabına. Bu yolda ajan
     döngüsü yoktur (runtime araç uygulamaz, CLAUDE.md §1): araçlı adım (developer/testçi) `501
     runtime.tools_unsupported` ile **açıkça** düşer, sessizce araçsız koşmaz.
  2. **ChatGPT aboneliği** (kullanıcının ilk denediği yol): makinedeki **Codex CLI** oturumu
     (`npm i -g @openai/codex`, `codex login`; Windows kullanıcısına bağlı, Claude Code ile aynı mantık). Tur
     `codex exec --json` ile koşar; sistem promptu Codex'te ayrı alan olmadığı için metnin başına
     "# Sistem talimatı" olarak gider (varsayımla ilerlenir). Araçlı adımda Codex **kendi** araçlarıyla
     (komut, dosya değişikliği) `cwd` içinde çalışır, `--sandbox workspace-write` dizin dışına yazmayı keser;
     araçsız adım `read-only` + boş geçici dizin. Yapısal çıktı `--output-schema`. Efor `low|medium|high|max`
     → `low|medium|high|xhigh`. Kalan kullanım: Codex CLI dışa vermiyor → `available=false`, limit koruması
     sessizce geçer (bilinçli; ChatGPT ayarlarından bakılır). Maliyet liste fiyatına göre **eşdeğer**
     (`PRICE_PER_M`, yaklaşık; bilinmeyen model → boş).
  Katalog sabit (`gpt-5.2`, `gpt-5.2-codex`, `gpt-5.1`, `gpt-5.1-codex`, `gpt-5.1-codex-mini`, `gpt-5-mini`);
  `OPENAI_MODELS` ortam değişkeniyle değişir. `reachable = giriş var` (Anthropic'le aynı gevşetme).
- **API anahtarı girişi (2026-09-20, kullanıcı isteği; Anthropic ve OpenAI):** Ayarlar → "API anahtarı ile
  kullan". Anahtar runtime'a gider, sağlayıcıda doğrulanır (`GET /v1/models`, token harcamaz), kullanıcı
  profiline yazılır: `%USERPROFILE%\.mrhobist-aiteam\credentials.json` — depo dışı, git'e girmez, Windows
  kullanıcısına bağlı (CLI oturumlarıyla aynı davranış). Bu bir **iş durumu değil kimlik bilgisidir**; §1
  "runtime dosya yazmaz" kuralının kapsamı dışında (bilinçli). Öncelik: ortam değişkeni
  (`ANTHROPIC_API_KEY` / `OPENAI_API_KEY`) > dosya > CLI oturumu. Anahtar kayıtlıyken CLI oturumu
  kullanılmaz; Anthropic'te SDK'ya `ANTHROPIC_API_KEY` ortamla gider (fatura Console'a), kota penceresi yok
  → limit koruması geçer, üst bar "API anahtarı · kota yok" der. "Anahtarı sil" oturuma döndürür.
  Anahtar hiçbir yanıta ve günlüğe yazılmaz; UI maskeli sonu (`sk-…ab12`) görür. `method` alanı hangi yolun
  aktif olduğunu söyler (`session | apikey | null`).
- `reachable` yorumu: runtime katalog için gerçek çağrı yapmaz (her model için bir tur token
  harcar); `reachable = giriş var`. `detail` alanı bunu söyler. LESSONS'taki "katalogda görünmek
  erişilebilir olmak değildir" dersi burada bilinçli olarak gevşetildi — varsayımla ilerlenir.

## Giriş (2026-09-19, kullanıcı kararı)

- **Bugün:** kodda gömülü tek kullanıcı **`admin / admin`** (`EmbeddedUserDirectory`, rol `owner`).
  Kullanıcı deposu, LDAP ya da tam kullanıcı mimarisi ileride `IUserDirectory` arayüzünü uygular;
  giriş ekranı ve belirteç akışı değişmez.
- **Belirteç:** JWT tek şema (HS256), 12 saat, `sub` / `name` / `role` claim'leri. Yetki ayrımını
  **rol claim'i** yapar (CLAUDE.md sapmalar: tek şema). Anahtar `AITeam:JwtKey`; boşsa geliştirme
  anahtarı — host yalnız loopback dinlediği için kabul edilir, dışa açılırsa zorunlu olur.
- **Kapı:** `/api/v1/*` kimlik ister; `auth/login`, `jobs/health` ve `OPTIONS` hariç. Kimliksiz istek
  **401 `auth.required`**, yanlış şifre **401 `auth.invalid_credentials`**. SSE (`scene/events`)
  başlık taşıyamadığından belirteci `?access_token=` ile alır; yalnız o yolda.
- **UI:** belirteç `localStorage`'da; her istek `Authorization: Bearer`. 401 gelince oturum düşer ve giriş
  ekranı gelir. Profil çipi sağ üstte (ad + çıkış). LLM oturumları (Ayarlar) bundan bağımsızdır: biri
  çalışma alanına giriş, öteki modele erişimdir.

## Sahne eşlemesi

| Olay | Ne zaman |
|---|---|
| `workflow.set { key }` | çalışma başlarken (dondurulan akış) |
| `agent.state { analyst, working/thinking }` · `run.stage { Analiz }` | analiz turu |
| `agent.state { analyst, done, note: "plan onay bekliyor" }` | `AwaitingApproval` |
| `agent.state { analyst, working, note: "plan revize" }` | revize turu |
| `board.set { tasks[] }` | onay; görevler ilk görev-sütununda `queued` |
| `meet { organizer → rol, handoff }` · `board.move` · `agent.state { rol, working }` | atama |
| `agent.state { organizer, idle }` | bekleyen iş yok |

## Açık kararlar (sonraki turda sorulacak; şimdi kodlanmaz)

1. ~~Red geri dönüşü~~ → kapı başına, aradakiler yeniden koşar (yukarıda, varsayımla).
2. `design` adımı görev başına çalışıyor (varsayımla); çalışma başına tek rehber istenirse akışa `design` görevi eklenir.
3. ~~`implement`~~ → araçlarla, hedef dizinde; test komutu ajanın kendi kararı, kabul ölçütleri yol gösterir.
4. ~~`canAsk` hedefi akışta olmayan bir ajan olabilir mi~~ → kapandı (2026-09-21): hedef **akış** düzeyinde `askRole` ile verilir; `null` bırakılırsa ajanın `can_ask`'i kullanılır.
5. `stage.officeRole` ile ajanın `office_roles` çakışması denetlenecek mi.
6. `kind: handoff` adımı ile `handoffRole` ikiliği; birinin kaldırılması.
7. Pano: çalışma düzeyindeki analiz için ayrı kart mı (bugün: analiz sütunu boş kalır, görevler
   onaydan sonra açılır).
