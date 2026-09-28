---
title: Kodlama Standartları
---

Bu standartlar developer için bağlayıcı, testçi için denetim ölçütüdür. Genel kurallardır:
hedef projenin kendi kuralları (kökteki `CLAUDE.md`, ek bilgi dosyası) çelişirse **proje kazanır**.

- Her public fonksiyonun tipi açık olacak (C#'ta zaten zorunlu; Python'da type hint).
- Public API ne yaptığını söyleyen bir belge yorumu taşır (C#: `///` XML doc, Python: docstring)
  — projenin kendi alışkanlığı farklıysa ona uy.
- Fonksiyon adları ne yaptığını söylesin; `process`, `handle`, `data` gibi
  boş adlar kullanılmasın.
- Sihirli sabit yok; anlamı olan değer adlandırılmış sabite çıkarılır.
- Tekrar eden üç veya daha fazla blok ortak bir yardımcıya çıkarılır.
- Testler davranışı sınasın, uygulamayı değil. İç değişkene değil,
  dışarıdan gözlemlenebilir sonuca bakılır.
