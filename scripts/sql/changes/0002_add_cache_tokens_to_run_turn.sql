-- 0002: tur basina onbellek token kirilimi.
--
-- Neden: `input_tokens` sutunu TOPLAMI tutuyor (dogrudan + onbellege yazilan + onbellekten okunan).
-- 2026-09-21 olcumunde bir developer turu 1.271.932 girdi token'i gosterdi; bu sayi tek basina
-- "baglam bosa mi gidiyor" sorusunu CEVAPLAMIYOR, cunku buyuyen payin cogu onbellekten ucuza okunmus
-- olabilir. Kirilim olmadan optimizasyon karari tahmine dayanir.
--
-- Eski satirlarda NULL: "olculmedi" demektir, sifir demek degil. Rapor NULL'u toplamaz.

ALTER TABLE run_turn ADD COLUMN cache_read_tokens  INTEGER;
ALTER TABLE run_turn ADD COLUMN cache_write_tokens INTEGER;
