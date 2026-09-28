---
title: Çalışma Ortamı
---

Ortamı deneyerek keşfetme — aşağıdakiler ölçülmüş gerçeklerdir. Geçici iskele proje
kurup silme, `dotnet new` çıktısını sorgulama, aynı dosyayı farklı araçlarla üç kez
yeniden yazma: bunların her biri bir LLM turu ve bağlam her turda büyüdüğü için
en pahalı turlar en sondakilerdir.

## Makine

- **Windows.** Kabuk olarak Git Bash var: `ls`, `grep`, `sed`, `awk`, `od` çalışır.
- **.NET SDK 10** kurulu. Node 20+ ve Python 3.12+ da var.
- Yol ayıracı olarak `/` kullan; hem Bash hem MSBuild kabul eder.

## Hedef dizin bağımsızdır

Çalıştığın klasörün kökünde boş bir `Directory.Build.props` ve
`ManagePackageVersionsCentrally=false` diyen bir `Directory.Packages.props` **zaten var**.
Bunlar ofisin kendi deposundan miras almayı keser.

- Paket sürümlerini **kendi csproj'unda** yaz; merkezi sürüm yönetimi kapalıdır.
- Üst klasörlerdeki `TreatWarningsAsErrors`, `TargetFramework` gibi ayarlar **sana geçmez**.
- Bu dosyaları sorgulamana gerek yok; kendi ayarını istiyorsan kökteki
  `Directory.Build.props` içine ekle.

## .NET tuzakları

- `dotnet new sln` .NET 10'da **`.slnx`** üretir. Klasik `.sln` istiyorsan:
  `dotnet new sln -n Ad --format sln`.
- Test projesi için gereken üç paket: `Microsoft.NET.Test.Sdk`, `xunit`,
  `xunit.runner.visualstudio`. Başka test kütüphanesi ekleme.
- `dotnet build` ve `dotnet test` çıktısı uzundur; `2>&1 | tail -20` ile kırp.

## Dosya yazımı

- Yazma araçları satır sonunu **LF** yazar. `.cmd` / `.bat` dosyaları CRLF ister.
  Dosyayı normal yaz, sonra tek komutla çevir:
  `awk '{printf "%s\r\n", $0}' run.cmd > run.tmp && mv run.tmp run.cmd`
- Türkçe karakterli çıktı veren konsol uygulaması için `run.cmd` başına
  `chcp 65001 >nul` koy.
- Kaynak dosyalar **UTF-8, BOM'suz**.

## Çalıştırılabilirlik

Proje kökündeki `run.cmd` uygulamayı başlatır; kullanıcı arayüzdeki "Projeyi başlat"
düğmesi bunu arar. İş çalıştırılabilir bir şey ürettiyse `run.cmd` yaz.
