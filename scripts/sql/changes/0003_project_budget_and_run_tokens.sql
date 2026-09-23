-- 0003: proje basi butce (token ya da $) ve calisma basi token toplami.
--
-- Neden: butce bugune kadar IS BASINAYDI (`run.max_cost_usd`). Tek bir isi sinirlamak, bir projenin
-- toplamda ne harcadigini sinirlamiyor: on kucuk is ust uste ayni butceyi on kez harcayabilir.
-- 2026-09-22 kullanici karari butceyi PROJE duzeyine de tasidi; "butce projede yoktur" karari kalkti.
--
-- Iki olcu ayri ayri verilir, ikisi de NULL olabilir:
--   * `max_cost_usd`  -- $ tavani. Abonelikte ucret kesilmez, bu yuzden ESDEGER maliyettir (CLAUDE.md §4).
--   * `max_tokens`    -- token tavani. Abonelikte asil tukenen kaynak budur; $ yaniltici olabilir.
-- Ikisi de NULL = SINIRSIZ (varsayilan). Biri doluysa yalniz o denetlenir; ikisi de doluysa ONCE DOLAN durdurur.
--
-- `run.input_tokens` / `run.output_tokens`: `run_turn` satirlarinda tur basina zaten var, ama proje
-- toplamini her istekte turlardan toplamak liste sorgusunu turlara bagimli kilardi. Maliyet (`total_cost_usd`)
-- hangi yolla birikiyorsa token de ayni yoldan birikir: tur kapanirken calismaya eklenir, proje karti
-- calismalari toplar. Tutarsizlik riski maliyetinkiyle ayni, yeni bir sinif degil.
--
-- Eski satirlar: butce sutunlari NULL (sinirsiz) -- var olan projelerin davranisi DEGISMEZ.
-- Token sutunlari 0 baslar: 2026-09-22 oncesi calismalarin token'i `run_turn`'de durur, calisma
-- satirina geriye donuk yazilmaz (ileri-yonlu betik gecmisi yeniden hesaplamaz). Proje toplami
-- bu yuzden eski isler icin eksik gorunebilir; rapor bunu "olculmedi" diye okur, sifir diye degil.

ALTER TABLE project ADD COLUMN max_cost_usd TEXT;      -- NULL = sinirsiz; ondalik METIN (0001 bicim kurali)
ALTER TABLE project ADD COLUMN max_tokens   INTEGER;   -- NULL = sinirsiz

ALTER TABLE run ADD COLUMN input_tokens  INTEGER NOT NULL DEFAULT 0;
ALTER TABLE run ADD COLUMN output_tokens INTEGER NOT NULL DEFAULT 0;
