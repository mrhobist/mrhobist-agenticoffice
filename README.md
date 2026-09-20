# MrHobist.AITeam — Agentic Office

Yapay zekâ ekibi üretim ofisi: altı rol (analist · tasarımcı · developer · testçi · manager ·
organizatör) bir brief'i alıp kod üretir; akış 2B piksel bir ofiste canlı izlenir.

Üç süreç, sıfır veritabanı: **.NET 10 Api** (orkestrasyon), **Python FastAPI runtime**
(yalnız LLM çağrısı), **Nuxt 4 + Canvas 2D UI** (piksel ofis). Durum dosyada tutulur —
yapılandırma `config/` altında git'te, çalışma geçmişi `runs/` altında append-only JSONL.
Tüm hostlar yalnız `127.0.0.1` dinler.

- Giriş kapısı ve pazarlıksız kurallar: [`CLAUDE.md`](CLAUDE.md)
- Mimari resmi (8 sayfa, draw.io): [`docs/architecture.drawio`](docs/architecture.drawio)
- Roller, iş akışı, geri dönüş kuralı: [`docs/DOMAIN.md`](docs/DOMAIN.md)
- HTTP sözleşmesi: [`docs/API.md`](docs/API.md)
- Canlı sahne (yerleşim, olaylar, asset envanteri): [`docs/SCENE.md`](docs/SCENE.md)
- Faz faz teslim listesi: [`docs/PHASES.md`](docs/PHASES.md)

## Gereksinimler

.NET SDK 10 (`global.json`) · Node 20+ · Python 3.12+ · Windows (sprite hattı ve proje
başlatma Windows'a bağlı) · model için Claude Code CLI ya da Codex CLI oturumu.

## Çalıştırma

İki terminal yeter: **Python runtime'ı Api kendisi başlatır** ve kapanırken durdurur.

```bash
dotnet run --project src/MrHobist.AITeam.Api
```

```bash
npm --prefix ui install && npm --prefix ui run dev
```

| Süreç | Adres | Nasıl kalkar |
|---|---|---|
| Api (orkestrasyon, `/openapi/v1.json`) | `127.0.0.1:5080` | elle |
| Runtime (LLM çağrı katmanı) | `127.0.0.1:5090` | **Api başlatır** |
| UI (piksel ofis) | `127.0.0.1:3000` | elle |

Runtime'ı ayrı izlemek istersen önce kendin başlat — Api ayakta olanı görür, ona dokunmaz ve
kapanışta durdurmaz:

```bash
runtime/.venv/Scripts/python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 5090 --app-dir runtime
```

Otomatik başlatmayı kapatmak için `AITeam:AutoStartRuntime=false`. Python yoksa Api yine kalkar;
günlüğe uyarı düşer, UI "Runtime kapalı" der, model çağrısı yapılamaz.

Python sanal ortamı yoksa (ya da başka bir makinede/Windows kullanıcısında kurulduğu için
çalışmıyorsa) tek komut, bu cihazın Python'unu bulup kurar:

```bash
powershell -ExecutionPolicy Bypass -File scripts/verify.ps1 -SetupRuntime
```

## İlk giriş

1. `127.0.0.1:3000` → gömülü kullanıcı **`admin` / `admin`** (JWT tek şema, rol claim'i).
2. Modele bağlan: **Ayarlar → LLM bağlantıları**. Abonelikle `claude login` / `codex login`
   (oturum, runtime'ı çalıştıran Windows kullanıcısına bağlıdır) ya da API anahtarı gir.
3. Sol raydaki **+** ile proje aç, panelden brief ver. Analistin planı **senin onayından**
   geçmeden panoya iş açılmaz.

> ⚠ Geliştirme kurulumudur. `AITeam:JwtKey` verilmezse JWT imza anahtarı kodda gömülü olan
> (herkese açık) anahtardır ve kullanıcı `admin/admin`'dir. Güvenlik yalnız loopback
> bağlamasına dayanır — hostları `0.0.0.0`'a açma, tünelleme.

## Doğrulama

```bash
powershell -ExecutionPolicy Bypass -File scripts/verify.ps1
```

Build (0 uyarı), birim + servis testleri, Python testleri, bağımlılık yönü, yasaklı ad alanları,
Python sınır denetimi ve UI typecheck tek kapıdan geçer.

## Sprite hattı

```bash
python scripts/build-sprites.py
```

`assets/raw/` ve `assets/reference/v2-catalogs/` → `ui/public/sprites/` (atlas elle düzenlenmez).
Sahneye komut göndermek için [`docs/SCENE.md`](docs/SCENE.md) §Olaylar.

## Lisans

MIT — [`LICENSE`](LICENSE).
