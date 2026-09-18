# MrHobist.AITeam — Agentic Office

Yapay zekâ ekibi üretim ofisi: altı rol (analist · tasarımcı · developer · testçi · manager ·
organizatör) bir brief'i alıp kod üretir; akış 2B piksel bir ofiste canlı izlenir.

- Giriş kapısı ve kurallar: [`CLAUDE.md`](CLAUDE.md)
- Canlı sahne (yerleşim, olaylar, asset envanteri): [`docs/SCENE.md`](docs/SCENE.md)
- Faz faz teslim listesi: [`docs/PHASES.md`](docs/PHASES.md)

## Çalıştırma

```bash
dotnet run --project src/MrHobist.AITeam.Api      # 127.0.0.1:5080
npm --prefix ui install && npm --prefix ui run dev # 127.0.0.1:3000
python scripts/build-sprites.py                    # sprite atlasını yeniden üret
```

Sahneye komut göndermek için `docs/SCENE.md` §Olaylar.
