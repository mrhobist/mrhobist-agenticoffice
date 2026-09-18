# runtime — LLM cagri katmani

> **Python durum tutmaz, veritabani gormez, is kurali bilmez.**
> Tek isi: `{systemPrompt, messages, provider, model, schema?}` alip
> `{text, structured, usage, costUsd}` dondurmek.

Bu kural pazarliga kapalidir (`../CLAUDE.md` §1). Orkestrasyon .NET tarafindadir.

Burada **olmayacak** seyler: tur sayaci, faz bilgisi, is akisi bilgisi, dosya yazma,
veritabani, gorev kavrami, rol'e gore dallanma. `scripts/verify.ps1` bunu denetler.

## Calistirma

```bash
python -m uvicorn app.main:app --host 127.0.0.1 --port 5090 --app-dir runtime
```

## Uclar

| Uc | Ne yapar |
|---|---|
| `POST /v1/turn` | Tek bir LLM cagrisi yurutur ve sonucu doner |
| `GET /v1/models` | Saglayicinin cagirilabilir modellerini yoklar |
| `GET /health` | Surec ayakta mi |
