-- 0001: cekirdek tablolar. Ileri-yonlu; yayindan sonra DEGISTIRILMEZ (ARCHITECTURE.md §8).
-- Duzeltme yeni betikle gelir. Uygulanan betigin checksum'i degisirse kalkis hata verir.
--
-- Bicim kurallari:
--   * Enum'lar metin olarak ADIYLA tutulur (CLAUDE.md §5) -- sayi degil, ki yeni uye eklemek eski satiri bozmasin.
--   * Zaman damgalari ISO-8601 metin (DateTimeOffset), UTC ofsetiyle.
--   * Para (cost) metin: SQLite'ta ondalik tur yok, REAL yuvarlama hatasi getirir.
--   * Buyuk/degisken yuk `data` sutununda JSON; sorgulanan alanlar ayrica sutuna kopyalanir.
--     Okuma daima `data`'dan nesneyi kurar, sutunlar yalniz filtre/toplama icindir.

CREATE TABLE project (
    key         TEXT    NOT NULL PRIMARY KEY,
    title       TEXT    NOT NULL,
    description TEXT    NOT NULL DEFAULT '',
    workflow    TEXT    NOT NULL,
    target_dir  TEXT    NOT NULL,              -- depo kokune gore goreli yol, daima '/'
    owner_id    TEXT    NOT NULL DEFAULT 'local',
    color       TEXT    NOT NULL DEFAULT '',
    sort_order  INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT    NOT NULL,
    updated_at  TEXT    NOT NULL
);

CREATE INDEX project_order_idx ON project (sort_order, created_at);

CREATE TABLE run (
    id                TEXT    NOT NULL PRIMARY KEY,
    project_key       TEXT    NOT NULL DEFAULT '',
    label             TEXT    NOT NULL,
    brief             TEXT    NOT NULL,
    sensitivity       TEXT    NOT NULL,
    status            TEXT    NOT NULL,
    step              TEXT,
    workflow_key      TEXT    NOT NULL DEFAULT 'default',
    owner_id          TEXT    NOT NULL DEFAULT 'local',
    detail            TEXT,
    question          TEXT,                    -- JSON: UserQuestion
    total_cost_usd    TEXT    NOT NULL DEFAULT '0',
    max_cost_usd      TEXT,
    retries           INTEGER NOT NULL DEFAULT 0,
    started_at        TEXT    NOT NULL,
    finished_at       TEXT,
    resume_at         TEXT,
    waiting_since     TEXT,
    spec              TEXT,                    -- JSON: Spec (analist ciktisi)
    workflow_snapshot TEXT,                    -- JSON: calisma baslarken donan akis kopyasi
    created_at        TEXT    NOT NULL,
    updated_at        TEXT    NOT NULL
);

-- Liste sorgusu daima yeni -> eski, istege bagli proje suzmesi.
CREATE INDEX run_started_idx         ON run (started_at DESC);
CREATE INDEX run_project_started_idx ON run (project_key, started_at DESC);
CREATE INDEX run_status_idx          ON run (status);

-- Bir ajanin tek LLM cagrisi. Sira = `id`: AUTOINCREMENT tekrar kullanilmaz, monoton artar; JSONL satir
-- sirasinin karsiligi ve ileride SSE'nin imleci. Ayri bir `seq` sutunu YOK: MAX+1 hesaplamak istek yolu ile
-- is kanali ayni calismaya ayni anda yazdiginda yarisir (tek yazici kurali istek yolunu kapsamaz).
CREATE TABLE run_turn (
    id            INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    run_id        TEXT    NOT NULL REFERENCES run (id) ON DELETE CASCADE,
    agent         TEXT    NOT NULL,
    ts            TEXT    NOT NULL,
    stage         TEXT,
    task          TEXT,
    round         INTEGER,
    provider      TEXT    NOT NULL,
    model         TEXT    NOT NULL,
    destination   TEXT    NOT NULL,
    duration_s    REAL    NOT NULL DEFAULT 0,
    cost_usd      TEXT,
    input_tokens  INTEGER,
    output_tokens INTEGER,
    data          TEXT    NOT NULL             -- JSON: Turn'un tamami (prompt, output, toolUses dahil)
);

CREATE INDEX run_turn_run_idx       ON run_turn (run_id, id);
CREATE INDEX run_turn_agent_idx     ON run_turn (run_id, agent, id);
CREATE INDEX run_turn_dest_idx      ON run_turn (run_id, destination);

CREATE TABLE run_message (
    id         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    run_id     TEXT    NOT NULL REFERENCES run (id) ON DELETE CASCADE,
    ts         TEXT    NOT NULL,
    kind       TEXT    NOT NULL,
    from_agent TEXT    NOT NULL,
    to_agent   TEXT    NOT NULL,
    task       TEXT,
    data       TEXT    NOT NULL                -- JSON: Message'in tamami
);

CREATE INDEX run_message_run_idx ON run_message (run_id, id);

CREATE TABLE run_phase (
    id     INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    run_id TEXT    NOT NULL REFERENCES run (id) ON DELETE CASCADE,
    task   TEXT    NOT NULL,
    ts     TEXT    NOT NULL,
    stage  TEXT    NOT NULL,
    agent  TEXT    NOT NULL,
    round  INTEGER NOT NULL DEFAULT 0,
    status TEXT    NOT NULL,
    cause  TEXT,
    data   TEXT    NOT NULL                    -- JSON: Phase'in tamami
);

CREATE INDEX run_phase_run_idx      ON run_phase (run_id, id);
CREATE INDEX run_phase_run_task_idx ON run_phase (run_id, task, id);

-- Calisma alani ayarlari: tek satir. Sema alan alan buyumesin diye govde JSON.
CREATE TABLE app_settings (
    id         INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
    data       TEXT    NOT NULL,
    updated_at TEXT    NOT NULL
);
