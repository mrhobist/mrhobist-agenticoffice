-- 0004: MCP sunuculari ve is ekleri (2026-09-23 kullanici istegi: "is verilirken pdf/resim verilebilsin;
-- MCP yonetim ekrani olsun, ekipteki kisilere MCP yetkisi verilsin").
--
-- `mcp_server`: kayitli MCP sunuculari. Neden config/ degil de veritabani: tanim makineye ozgudur
-- (calistirilabilir yol, yerel adres) ve belirtec tasir (ortam degiskeni, Authorization basligi) -- git'e
-- girmemeli. Hangi AJANIN kullanacagi ise ajan md'sindedir (`mcp: [...]`): yetki ekibin tanimidir, ekip
-- config/'dadir (CLAUDE.md §2). Anahtar ajan aracinin adina donusur (`mcp__{key}__{arac}`).
--   * `transport`  -- stdio | http | sse, ADIYLA (0001 bicim kurali).
--   * `data`       -- JSON: args, env, headers. Sir tasir; Api hicbir yanita acik yazmaz (maskeler).
--   * `enabled`    -- 0 = kayitli ama ajanlara verilmez (yetkiyi silmeden gecici kapatma).
--
-- `run.attachments`: is verilirken eklenen dosyalarin listesi (JSON: RunAttachment[]). Dosyalarin kendisi
-- `data/attachments/{run_id}/` altinda; satir yalniz ustveri. Ayri tablo degil sutun: ekler calismayla
-- birlikte yazilir, birlikte okunur, calisma silinince birlikte gider -- sorgulanmaz, suzulmez. NULL = ek yok,
-- eski satirlarin davranisi DEGISMEZ.

CREATE TABLE mcp_server (
    key         TEXT    NOT NULL PRIMARY KEY,
    name        TEXT    NOT NULL,
    description TEXT    NOT NULL DEFAULT '',
    transport   TEXT    NOT NULL,
    command     TEXT,
    url         TEXT,
    enabled     INTEGER NOT NULL DEFAULT 1,
    data        TEXT    NOT NULL DEFAULT '{}',
    created_at  TEXT    NOT NULL,
    updated_at  TEXT    NOT NULL
);

ALTER TABLE run ADD COLUMN attachments TEXT;
