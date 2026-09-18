---
title: Mimari Kurallar
---

Bu kurallar analist ve developer için bağlayıcıdır.

- Katmanlar tek yönlü bağımlı olsun: arayüz → servis → veri. Ters yönde import yok.
- Dış dünyaya çıkan her şey (HTTP, dosya, LLM) bir adaptör arkasında dursun;
  iş mantığı doğrudan kütüphane çağırmasın.
- Yan etkiler açıkça olsun. Modül import edilirken ağ/dosya işlemi yapılmasın.
- Hata yutulmasın. Yakalanan her istisna ya çözülür ya da bağlam eklenip
  yeniden fırlatılır; sessiz `except: pass` yasak.
- Yapılandırma koda gömülmez; config dosyasından ya da ortamdan okunur.
- Gizli bilgi (anahtar, token) asla koda, loga veya hata mesajına yazılmaz.
