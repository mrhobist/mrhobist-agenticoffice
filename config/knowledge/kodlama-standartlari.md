---
title: Kodlama Standartları
---

Bu standartlar developer için bağlayıcı, testçi için denetim ölçütüdür.

- Her public fonksiyonun tip imzası (type hint) olacak.
- Her public fonksiyonun ne yaptığını söyleyen bir docstring'i olacak.
- Fonksiyon adları ne yaptığını söylesin; `process`, `handle`, `data` gibi
  boş adlar kullanılmasın.
- Sihirli sabit yok; anlamı olan değer adlandırılmış sabite çıkarılır.
- Tekrar eden üç veya daha fazla blok ortak bir yardımcıya çıkarılır.
- Testler davranışı sınasın, uygulamayı değil. İç değişkene değil,
  dışarıdan gözlemlenebilir sonuca bakılır.
