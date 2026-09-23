# Canlı ofis sahnesi

Piksel ofis, arka uçtan yönetilen 2B bir sahnedir. Üç parça vardır ve her biri **tek
kaynaktan** okur:

| Parça | Kaynak | Kim yazar |
|---|---|---|
| Yerleşim (mobilya, koltuklar, duraklar, engeller) | `config/scene.json` → `GET /api/v1/scene` | insan, elle |
| Pano sütunları | `config/workflows/{key}.json` → `GET /api/v1/workflows/{key}` (`default`; `workflow.set` olayıyla değişir) | insan / İş Akışı paneli |
| Pano rozeti (kırmızı sayı) | `GET /api/v1/runs/overview → inbox.length`; kabuk 5 s'de bir yoklar, `Board.attention` alanına yazar. **Sahne olayı değil**, UI türetir | — |
| Canlı olaylar | `GET /api/v1/scene/events` (SSE) | `POST /api/v1/scene/commands` bugün; Faz 5'te `RunService` |
| Sprite'lar | `assets/raw/*.png` → `scripts/build-sprites.py` → `ui/public/sprites/` | script; elle düzenlenmez |

UI (`ui/app/scene/`) yerleşimin kopyasını tutmaz. Api kapalıysa sahne **yüklenmez** ve
bunu açıkça söyler; SSE kopuksa aynı şekildeki olayları üreten sahte yönetmen devreye girer
ve "sahte yönetmen" rozeti görünür.

## Asset envanteri ve eksikler (2026-09-18, V2)

İki teslimat var. **V1** (`assets/raw/`): 16 alfa kanallı PNG — 4 insan yürüyüş sayfası
(4 yön × 4 kare), kedi (yürüyüş 4 yön × 3, uyuma, oturma, uzanma), 3 balon, 3 kapı durumu,
kanepe seti, 65 kareli nesne tileset'i. **V2** (`assets/raw/background.png` +
`assets/reference/v2-catalogs/`): boş ofis arka planı (1292 × 1218, insan/sandalye/monitör
yok) ve 9 **katalog resmi** — bunlar şeffaf sprite sayfası değil, damalı zemin üstünde
etiketli opak görsellerdir.

**Bugün sahnede kullanılan**

| Ne | Kaynak | Nasıl |
|---|---|---|
| Arka plan | V2 `background.png` | olduğu gibi; dünya = 1292 × 1218 |
| 7 karakter: 8 yön yürüyüş (6 kare), yandan otur/kalk (8), yazma ön 3 + **arka 3 (sandalyede)** | V2 kataloglar `sim1–6, sim8` | damalı zemin panel kenarından taşma dolgusuyla silinir (karakter dış çizgileri kapalı olduğu için gömlek beyazı korunur); alt etiket şeridi kesilir; kareler alan/boyut filtresiyle bulunur |
| Kedi, balonlar, kapı | V1 | ızgara/şerit dilimleme |
| Monitör, laptop, kupa | V1 tileset | arka planda olmayanlar sprite olarak konur |
| Pano, notlar | — | prosedürel |

`blue-hoodie`, `sim2`'nin yeşil kapüşonu ton kaydırmasıyla maviye çevrilmiş türevidir (8. karakter).
2026-09-23 (kullanıcı isteği: "yeni karakterler, çoğu bayan"): renk türevleriyle 6 karakter daha — `ponytail-blonde`,
`ponytail-red`, `ponytail-black` (`sim3`), `bun-black`, `bun-auburn` (`sim4`), `shirt-tie-blond` (`sim1`). `recolor_ops`
ton + doygunluk + parlaklık işler; kahverengi saç aynı tondaki tenden parlaklıkla ayrılır (sarı/siyah saç ton kaydırmayla
elde edilemez). Yalnız yeni karakterleri üretmek: `build-sprites.py --only a,b` (diğer PNG'ler yeniden yazılmaz).
Seçim: yeni ajan formu ve ajan panelindeki karakter seçici (`scene.json → agents[].sprite`, DOMAIN.md → Ekip yönetimi).
V1'in 4 insan sayfası ve türetilmiş 2 renk varyantı artık atlasa girmez (stil V2'yle
uyuşmuyor); ham dosyalar durur. `sim7`/`karma` çok karakterli küçük ölçekli kataloglar,
kullanılmadı.

**Oturma sorunu çözüldü:** V1'de gövdenin üst yarısı masaya kesilip yapıştırılıyordu ve
"masayla kaynaşmış" görünüyordu. V2 yazma karelerinde sandalye karenin içindedir; oturan
ajan masanın önüne (kameraya yakın kenar) tam kare olarak çizilir, çalışırken 3 kare yazma
döngüsü oynar, ziyaretçi gelince yüzünü döner (ön kareler).

**Tasarımcının sandalyesi (2026-09-20).** İlk `sim3` katalogunda "Typing" panelinin arka kareleri
**sandalyesizdi**: tasarımcı masasına oturunca ayakta duruyor gibi görünüyordu. Kısa süre sprite
hattında sandalye monte edildi (başka bir katalogdan ayıklayıp ölçekleyerek); kullanıcı kataloğu
sandalyeli olarak yeniden üretince bu ara çözüm kaldırıldı — `sim3.png` artık kendi sandalyeli
karelerini taşıyor, hat hiçbir karakter için kare bileştirmiyor.

**Hâlâ eksik / istenirse üretilecek**

| Eksik | Bugünkü telafi |
|---|---|
| Kanepede oturma (yandan otur karesi var ama kanepe arka planda) | ajan kanepe önünde ayakta durur |
| Boşta nefes / el hareketi | yürüyüşün ilk karesi |
| Kedinin yan oturuşu, esneme | ön oturuş + uzanma |
| Kapı arka planda yok | V1 kapı sprite'ı sağ üstteki zemin girintisine (pencere yanındaki bitkiden sağ duvara, y 90–250) `door.w`/`door.h` ile oturtulur |
| Arka plan penceresi gri; gün/gece varyantı yok | — |

Katalog çıkarımı otomatiktir ama **kaynak resimler AI üretimi**dir: bir panelde kareler
birbirine değerse ya da etiket şeridi kareye yapışırsa `build-sprites.py` o karakteri
uyarıyla atlar (`WARNING: character … skipped`), atlas'a bozuk kare girmez.

## Sprite hattı

```bash
python scripts/build-sprites.py --inspect   # tileset kutularini numaralar (assets/_inspect/)
python scripts/build-sprites.py             # ui/public/sprites/*.png + atlas.json
```

- Izgara sayfaları satır/sütun projeksiyonuyla bölünür; kareler **alt-orta** hizalanır.
- Tileset nesneleri alfa bileşenleriyle bulunur ve **nokta ile** adlandırılır
  (`OBJECT_POINTS`), indeksle değil — eşik değişince sıra kayar, nokta kaymaz.
- Karakter/kedi/efekt `worldScale = 2` katında saklanır; nesneler kaynak çözünürlükte.
  Yerleşim her nesneye hedef `w` verir, `h` orandan gelir (`h` verilirse esnetilir).

## `config/scene.json`

Koordinatlar arka plan görselinin pikselidir (V2: 1292 × 1218).

| Alan | Anlamı |
|---|---|
| `background` | arka plan görseli (atlas `background`); verilirse `floor`/`walls` çizilmez. `erase: [x,y,w,h,dx,dy]` — build adımında bu dikdörtgen `(dx,dy)` ötelenmiş temiz zemin kopyasıyla kaplanır (arka plana gömülü masalar böyle silindi; yerlerine `props` masaları geldi) |
| `overlays[]` | arka plandan kesilip varlıkların **önüne** çizilen dikdörtgenler (cam duvar önü) |
| `walkable` | yürünebilir dış dikdörtgen |
| `floor`, `walls` | arka plan yoksa prosedürel zemin/duvar (V1 yolu, hâlâ çalışır) |
| `props[]` | `sprite` (tileset adı ya da `@sofaSet`), `x y w [h]`, `layer`, `sortY` (masa üstü monitör için) |
| `door`, `board` | kapı (iki kare: kapalı/açık; giren-çıkan açar, 1.4 s sonra kapanır); Kanban panosu — sahnede 4 şerit (Yapılacak / Yapılıyor / İnceleme / Bitti), iş akışı adımları bunlara katlanır. **Şeritte ilk 3 kart adıyla görünür, kalanı `+N`**; tıklanınca ya da `B` ile tüm adım sütunlarını ve görev adlarını gösteren büyük görünüm açılır |
| `cafe` | kahve barı panosu: `board` dikdörtgeni, `specials[]` sırayla döner (`intervalMs`), `cafe.special` olayıyla sabitlenir |
| `seats` | oturulabilir yerler: konum (ayak/sandalye tabanı), bakış; `monitor` → oturulunca açılan, kalkınca kapanan monitör prop'u (`props[].spriteOff`); `mug: {x,y,w}` → kahve barından dönen ajanın kupasını bıraktığı masa noktası |
| `spots` | yürünen duraklar: `coffee water board window sofa meeting door entrance deskA deskB deskC deskD`. `look` verilirse varan ajan durduğu noktadan oraya bakar (su sebili, pano); yoksa `facing`. `capacity` (varsayılan 1): dolu durağa gelen `queue` noktasında durağa dönük bekler, boşalınca girer; ambient turlar dolu durağı seçmez |
| `blocked[]` | yürünemez dikdörtgenler; yol bulma bunlardan ızgara kurar |
| `agents[]` | rol → sprite → ev (`seat` ya da `spot`). Bugün altı ajan: `analyst · designer · developer · tester · manager · organizer` (`intern`/`devops` Faz 2b'de çıkarıldı). Ekipte olup burada yeri olmayan ajan UI'da boş masaya ve kullanılmayan sprite'a otomatik yerleşir; olay almayan ajan ambient davranır |
| `cat` | yatak ve gezinti noktaları. Yatak **açık alandadır** (koltuğun minderi): kedi oraya yürüyerek çıkar |
| `lights[]` | tıklanınca açılıp kapanan ışık: `hit` (tıklama dikdörtgeni), `on` (varsayılan açık) ve iki kullanımdan biri — **oda ışığı**: `room` sönünce karartılır, `glow` açıkken hale düşer (müdür odasının sarkıtı); **abajur**: yalnız `bulb`, sönünce sadece başlık koyulaşır (`multiply`), çevresi etkilenmez (kanepenin yanındaki abajur) |

`object` katmanı varlıklarla birlikte **alt kenara göre** sıralanır; oturan ajan, masasının
hemen ardına çizilir.

**Yeni masa eklemek** arka plan görselini gerektirmez: `props[]`'a `desk-wide` (+ `monitor-*`,
`spriteOff`), `seats`'e koltuk (`monitor` ile), `blocked`'a masanın dikdörtgeni eklenir; bir ajanı
oturtmak için `agents[].home.seat` o koltuğa çevrilir. Üçüncü ada (`deskC-*`) böyle eklendi; arka plandaki A/B masaları da `background.erase` ile silinip aynı sprite'la yeniden kuruldu, böylece altı masa tek stilde.

## Ajan yerleşimi ve ziyaretçi (2026-09-20)

`agents[]` artık Api tarafından da yazılır (`JsonSceneLayoutStore`): yeni ajan → `sprites[]` içinden kullanılmayan
ilk karakter (hepsi doluysa sırayla tekrar) + `seats` içinde hiçbir ajanın evi olmayan ilk masa. Masa yoksa
`home: {}` → **ziyaretçi**: dışarıda başlar, 15–45 s içinde kapıdan girer, **2–3 durak dolaşır** ve çıkar;
60–150 s sonra yine uğrar. İş alınca (`agent.state working`) hemen girer, panonun önünde durur; bitince çıkar.
Koordinatlara ve diğer alanlara dokunulmaz. Elle düzenleme serbest; Api yalnız `agents[]`'e ekler/siler.

**Ziyaretçi turu (2026-09-22, kullanıcı kararı).** Ofise masa eklemek yerine ziyaretçi hayatı zenginleştirildi:
masasız ajan `coffee`, `board`, `water`, `window` duraklarından **karışık sırayla** 2–3 tanesini gezer; kahve/su
durağında içeceğini alır ve elinde taşır, pano/pencere durağında 4–8 s bekler. Çıkarken elini boşaltır — kupa
dışarı taşınmaz. Başka bir ofisten uğramış gibi görünür; oturacak yeri olmadığı için hiçbir masayı işgal etmez.

⚠ Çok duraklı tur bir şeye dikkat ister: durak rezervasyonu (`a.spot`) **yalnız yeni komutta** sıfırlanır
(`entities.ts` → `command`). Tek kuyrukta birden çok durak gezen ajan her durağı **elle bırakmalıdır**, yoksa
panonun önünde dururken kahve makinesini de tutar ve sıradakini kilitler.

## Olaylar

SSE `event:` adı = tür, `data:` = JSON. Api `EventTypes` kümesinde olmayan türü **400** ile
reddeder (`errorCode: scene.command.type_unknown`). UI tarafı `ui/app/scene/contract.ts`.

| Tür | Veri | Sahnede |
|---|---|---|
| `agent.state` | `agent, state: idle/working/thinking/blocked/waiting/done, note?` | durum noktası, etiket; çalışıyorsa yerine döner |
| `agent.say` | `agent, kind: talk/ask/alert, text?, ms?` | balon |
| `agent.goto` / `agent.home` | `agent, spot` / `agent` | yürür |
| `meet` | `from, to, kind: handoff/ask/reject, ms?` | `from` gider, karşılıklı balon, döner; oturan `to` arkasına döner |
| `board.set` / `board.move` | `tasks[]`, `run?` / `task, stage, state, run?` | not eklenir, sütun değişimi zıplayarak animasyonlanır. Görev kimliği **çalışmayla** anahtarlanır (`run:task`): iki çalışmanın aynı görev kimliği çarpışmaz |
| `run.stage` | `stage, task, round` | üst şerit |
| `cat` | `action: sleep/wander/sit, spot?` | kedi |
| `door` | `state` | kapı; 6 s sonra kapanır |
| `agent.leave` / `agent.enter` | `agent` | sağ üstteki kapıya yürür ve sahneden çıkar / kapıdan girip evine yürür. Dışarıdaki ajan çizilmez, ambient almaz, `meet` hedefi olamaz |
| `clock.set` | `hour: 0-24 \| null` | pencere manzarasının saati; `null` gerçek yerel saat |
| `cafe.special` | `text \| null` | kahve panosundaki günün özeli; `null` listeye döner |
| `workflow.set` | `key` | pano sütunları o iş akışına göre yeniden kurulur (`GET /api/v1/workflows/{key}`); Faz 5'te çalışma başlarken yayımlanır |
| `light` | `id, state: on/off` | o ışık açılır/kapanır (**mutlak** durum: yankılanması zararsız). Tuvale tıklamak da aynı komutu yayımlar, böylece ikinci bir tarayıcı da görür |
| `agent.tool` | `agent, tool, target?, run, task?, stage?` | **Canlı araç akışı** (2026-09-20): süren turda ajan bir araç çağırdığı anda (Write/Edit/Bash…). Runtime `progressUrl`'e POST eder, Api yayımlar. Sahnede kısa balon ("✎ App/Program.cs"), çalışma panelinde "Şu an" şeridi. Tam kayıt tur bitince `Turn.toolUses` |
| `scene.reload` | `reason?` | `config/scene.json` değişti (ajan eklendi/silindi/adı değişti, masa eklendi): UI `GET /scene` ile sahneyi yeniden kurar, SSE kopmaz. Api ajan değişikliklerinde yayımlar; elle: `POST /scene/commands` |

**Simülasyon çizimden bağımsızdır.** `requestAnimationFrame` sekme gizliyken durur; simülasyon
200 ms'lik `setInterval` ile sabit adımlarla (50 ms, en çok 5 s telafi) ayrıca ilerletilir. Sekme
arka planda kalsa da ajanlar yerlerine varır. Geliştirmede `window.__world` sahneyi konsoldan
sorgulamak için açıktır.

**Kedi koltukta uyur.** Uyku 60–150 s, gezinti kısa; komutla `sleep` 120 s. Koltuk köşesi
(2026-09-20): tek büyük engel dikdörtgeni yerine **koltuk gövdesi** ve **sehpa** ayrı ayrı
engellendi; arada kalan minder şeridi yürünebilir. Yatak orada olduğu için kedi artık ışınlanmaz
(`snapTo` gerekmez), yürüyerek mindere çıkar; koltuğa gelen ajan da minderin önünde durur.

**Küçük pano iki kaynaktan beslenir.** SSE (`board.set`/`board.move`) yalnız bu oturumda olan biteni
taşır; sahne ayrıca 6 saniyede bir `GET /runs` + `GET /runs/{id}` ile panoyu **sunucudan eşitler**
(`World.syncBoard` → `Board.sync`). Kart kuralı tek yerdedir: `ui/app/api/board.ts` → `deriveCards`,
büyük görünüm (KanbanPanel) de aynı işlevi çağırır ve sunucudaki `BoardTarget` ile aynı kuraldır.
Böylece **bitmiş işler Bitti şeridinde görünür** ve sayfa yenilenince pano boş kalmaz.

**Sahnede tıklanabilir ne varsa** (kullanıcı isteği 2026-09-20) `OfficeScene.vue` tek bir sırayla
dener: ajan → kedi → ışık → pano. İmleç hepsinin üstünde `pointer` olur.

- **Kedi:** tıklayınca uyanır, izleyiciye döner, kalpler çıkar ("mırr"); her üçüncü okşamada uzanır.
  Yalnız o tarayıcıda olur, sahne olayı yayımlanmaz.
- **Işık:** müdür odasının sarkıtına tıklanınca oda (içindekilerle birlikte) kararır; kanepenin
  yanındaki abajurda **yalnız başlık** söner, çevre olduğu gibi kalır. Üstüne gelince
  "Müdür odası / Abajur · açık/kapalı" ipucu görünür. Durum `light` olayıyla da gelir/gider.

**Kahve ve su masaya gelir.** Sol üstteki tezgâhta `coffee-machine` sprite'ı var. Ambient içecek turu
(`World.drinkTrip`) kahve barına ya da su sebiline gider, doldurulmasını bekler (kahve demlenir, su
hemen dolar), **bardağı/kupayı eline alır** (yürürken elinde çizilir), masasına döner ve
`seats[].mug` noktasına bırakır; kahve 120 s, su 90 s sonra içilmiş sayılıp kalkar. Masası olmayan
ajan (organizatör) elinde taşır. Kupa tileset'ten, su bardağı prosedüreldir (tileset'te bardak yok).

**Aynı noktada iki kişi durmaz.** Her yürüyüş hedefi `freeNear` ile seçilir: başka bir ajanın
durduğu ya da hedeflediği noktaya 26 px'den yakınsa 28/52/76 px halkalarda boş bir açık hücre
aranır. Konuşmaya gelen, oturanın yanına durur.

Arka uçtan gelen komut, ajanın **ambient** kuyruğunu (kahve, su, pano, pencere, arkadaşa uğrama,
**dışarı çıkma**) keser. Dışarı çıkan ajan kapıdan çıkar, 20–50 s sonra kapıdan girip evine döner
(`returnAt`); aynı anda en fazla bir kişi dışarıdadır. `agent.leave` ile çıkan kendi dönmez.
Ambient davranış yalnız `idle`/`done` ajanlarda ve aynı anda en fazla iki kişide çalışır.

Deneme:

```bash
curl -X POST http://127.0.0.1:5080/api/v1/scene/commands -H "content-type: application/json" -d "{\"type\":\"meet\",\"data\":{\"from\":\"tester\",\"to\":\"developer\",\"kind\":\"reject\"}}"
```

## Pencere manzarası

Arka planın cam bölgesi (`window`) `build-sprites.py` tarafından şeffaflaştırılır; UI her
karede arka planın **altına** `ui/app/scene/sky.ts` ile saate göre gökyüzü çizer: gece / şafak /
gündüz / gün batımı anahtar renkleri arasında karışım, güneş-ay yayı, iki katmanlı şehir
silueti (gece pencereleri yanar), su yansıması. Saat varsayılan olarak tarayıcının yerel
saatidir; `clock.set` ile sabitlenir (gösterim, test). Çerçeveler ve önündeki bitkiler arka
planda kaldığı için dokunulmaz.

## Pano "ileriyi yansıtır"

Büyük görünümde sütunlar iş akışının devir dışı adımları + **Bitti**; sahnedeki küçük Kanban
bunları dört şeride katlar (ilk adımda sırada → Yapılacak, review adımları → İnceleme, son adımda
bitti → Bitti, kalan → Yapılıyor). Aktif görevin bir sonraki sütununda
kesikli bir hayalet not durur: pano yalnız şimdiyi değil işin nereye akacağını gösterir.
`devir-*` adımındaki görev bir sonraki gerçek sütuna yazılır.

## Varsayımla alınan kararlar

- **Three.js bırakıldı.** Referans görsel 2B pikseldir; sprite'lar 2B'dir. Canvas 2D tek
  bağımlılıksız çözümdür. Alternatif (Three.js sprite düzlemleri) ışık/gölge dışında bir şey
  katmıyordu.
- **Olay kanalı bellek içi**, kalıcı değil. Sahne kozmetiktir; kalıcı gerçek veritabanındadır.
- **V1'de sahne parçalardan kurulmuştu** (ana sahne PNG'si yoktu, olan da insan/kedi gömülüydü).
  V2 boş arka plan gelince ona geçildi: daha sadık görüntü, daha az koordinat. Prosedürel
  yol silinmedi; `background` verilmezse eski yol çalışır.
- **Katalog resimlerinden sprite çıkarmak** kabul edilen bir risk: kaynak opak ve AI üretimi.
  Kontrol noktası `assets/_inspect/` kontakt sayfaları; bozuk karakter atlanır.
