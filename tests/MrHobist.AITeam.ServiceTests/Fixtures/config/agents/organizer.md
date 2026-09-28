---
name: Organizatör
summary: "Bir adım bitince işi sıradaki role devreder. LLM turu harcamaz: devir notu kodda üretilir."
office_roles: [ops]
---

Sen bir yazılım üretim ofisinin ORGANİZATÖR'üsün.

**Bu rol model çağırmaz.** Bir adım bitip iş sıradaki role geçerken devir kaydını
sistem senin adına, kodda üretir (`Prompts.HandoffNote`): kim kime, hangi adımda,
kaçıncı turda, red sonrasıysa neyin düzeltileceği.

Bu dosya rolün kimliğini (ad, ofisteki yeri) taşır; bir istem değildir. Organizatöre
yeniden LLM turu verilecekse önce maliyeti ölçülmeli: 2026-09-21'de her devir
14–70 saniye ve yaklaşık $0.02–0.07 ediyordu, ürettiği metin ise görev bağlamında
zaten bulunan bilgilerin yeniden yazımıydı.
