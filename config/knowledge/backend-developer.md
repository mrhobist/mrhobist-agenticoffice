---
title: Backend Developer — .NET Onion (kurum referans deseni)
---

Kurumun .NET backend'lerinin ortak deseni. Kaynak: referans backend projesi (2026-09-23 tarandı).
Yer tutucular: `<Urun>` ürün adı, `<Kok>` kök namespace (`<Sirket>.<Urun>`) — gerçek adları hedef projenin kodundan oku.
**Hedef projenin kökünde `CLAUDE.md`, `ARCHITECTURE.md`, `LESSONS.md` varsa önce onları oku; çelişirse proje
kazanır.** Mevcut desene birebir uy, yeni desen icat etme; emin değilsen komşu modüle (`Examples`) bak.
Kullanıcıya dönen metin ve kod yorumu **Türkçe**, identifier **İngilizce**.

## Yığın ve katmanlar

- **.NET 10**, `Nullable=enable`, `ImplicitUsings=enable`, `.slnx`. Merkezi paket yönetimi **yok** —
  sürüm her `.csproj`'da. Paket eklemeden önce sor. Paketi **sürümüyle** ekle (`dotnet add package X --version N`),
  hedef çerçeveyle aynı ana sürüm ailesinden (EF Core 10 ↔ .NET 10); sürümsüz ekleme ön sürüm/uyumsuz ana sürüm getirebilir.
- Yön: `Api → Application ← Infrastructure`; Domain merkezde, Common paylaşılan çekirdek. Mimari testlerle
  zorlanır (`LayerDependencyTests`, `NamespaceConventionTests`).

| Proje | İçerik | Kural |
|---|---|---|
| `Domain` | Entity, marker interface, enum, `Constants/ScreenCodes` | Paket yok, referans yok, exception fırlatmaz |
| `Common` | `AppException` ailesi, `Security/` (Aes, Hmac, ClaimProtector, PasswordHasher), `PhoneHelper` | Referans yok |
| `Application` | Modül servisleri, DTO, validator, `Abstractions/` | ASP.NET ve Infrastructure referansı **yasak**; EF Core serbest |
| `Infrastructure` | EF/Npgsql, interceptor, JWT, LDAP, SMS, FusionCache, Serilog, job | |
| `Presentation/Api` (5080) | Controller'lar, filtreler, ProblemDetails, OpenAPI | |
| `Presentation/Task.Api` (5081) | Zamanlanmış işler (`BackgroundService`) | |
| `Presentation/Gateway` (8080) | YARP DMZ geçidi — bugün devre dışı | |

**Yok (ekleme):** MediatR/CQRS, repository, AutoMapper/Mapster, `Result` tipi, domain event, value object,
UoW sınıfı, `IOptions<T>`, `TimeProvider`, outbox/mesajlaşma, Hangfire/Quartz, Dapper.

## Domain

- Her entity `BaseEntity`'den: `Guid Id = Guid.CreateVersion7()`, `DateTime CreatedAt = DateTime.UtcNow`,
  `DateTime? UpdatedAt`. Anemik: public get/set, navigation `= null!`, koleksiyon `= []`.
- Marker'lar: `ITenantEntity { Guid CompanyId }`, `ISoftDelete { bool IsDeleted }`,
  `IIntegritySigned { string? RowSig; string BuildSignaturePayload(); }`. İmza payload'ı
  `string.Join('|', Id, CompanyId, Name, SignatureFormat.B(IsActive), …)` — `B/S/D/M` biçimleyicileri.
- Klasör `Domain/<Urun>/<Alan>/` ama namespace **düz**: `<Kok>.Domain.<Urun>`
  (tek istisna; başka her yerde namespace = proje kökü + klasör yolu, file-scoped).
- Para `decimal(18,2)`; zaman `DateTime.UtcNow` (Postgres `timestamptz`).

## Application — modül deseni

```
Application/<Module>/<Module>Service.cs            # sealed class <Module>Service(...) : I<Module>Service
Application/<Module>/Models/<Module>Models.cs      # DTO + request: sealed record
Application/<Module>/Validators/<Module>Validators.cs
Application/Abstractions/I<Module>Service.cs
```

- Servis primary constructor ile `I<Urun>Context`, `ICurrentUser`, `IIntegrityService`… alır; `AddApplication`
  içinde `AddScoped` kaydedilir. Servis doğrudan LINQ yazar (`Include`, `IgnoreQueryFilters`) — repository yok.
- **İş başına tek `SaveChangesAsync`.** Yardımcı servisler (ör. `ISessionService.RevokeAllAsync`) SaveChanges çağırmaz.
- Modüller birbirinin entity'sine yazmaz; servis interface'i üzerinden konuşur.
- Mapping elle: `private static XDto ToDto(X x)`.
- **Validation:** FluentValidation, `AddValidatorsFromAssembly(..., ServiceLifetime.Singleton)`, Api'deki global
  `ValidationFilter` çalıştırır → 422 `COMMON-422`. Mesaj Türkçe: `NotEmpty().WithMessage("Ad zorunludur.").MaximumLength(200)`.
  Request **`sealed record` (class)** olmalı — `record struct` sessizce doğrulanmadan geçer.
- Tenant: `currentUser.ResolveCompanyId(request.CompanyId)` (master istenen firmaya, diğerleri kendi firmasına).
  Sorguda elle `CompanyId ==` **yazma**, named filter halleder; bypass `IgnoreQueryFilters([QueryFilterNames.Tenant])`.
- Benzersizlik kontrolü silinmişleri de görmeli: `IgnoreQueryFilters().AnyAsync(...)` → `ConflictException(..., "EXM-409")`.
- Bulunamayan kayıt `NotFoundException("Kayıt bulunamadı.", "EXM-404")` — başka firmanın kaydı da 404 döner (filtre).
- **İmzalı entity'de mutasyondan ÖNCE `integrity.Verify(entity)`** (bozuksa `TamperedDataException` SEC-001).
- Silme `context.X.Remove(item)` — interceptor soft delete'e çevirir. Para/iz taşıyan kayıt hard delete edilmez.
- Async metot `CancellationToken cancellationToken = default` alır ve aşağı geçirir. `ConfigureAwait` kullanılmaz.

## Hatalar

`Common/Exceptions`: `AppException(message, errorCode, statusCode)` alt tipleri —
`NotFoundException` 404 · `BusinessException` 400 · `AppValidationException(+Errors)` 422 ·
`UnauthorizedException` 401 · `ForbiddenException` 403 · `ConflictException` 409 · `TamperedDataException` SEC-001/500.
Kod öneki modülden: `AUTH-`, `CMP-`, `USR-`, `ROL-`, `EXM-`, `SEC-`, `COMMON-` (yeni modül kendi önekini alır).
`GlobalExceptionHandler` hepsini ProblemDetails'e çevirir (`title` Türkçe, `status`, `errorCode`, `correlationId`,
`errors`); `DbUpdateConcurrencyException` ve Postgres 23505 → `COMMON-409`, kalan → `COMMON-500`.
**Controller'da try/catch yok.** İstemci `errorCode`'a göre dallanır.

## Infrastructure

- EF Core 10 + **Npgsql**, `UseSnakeCaseNamingConvention()`, **code-first migration**
  (`Persistence/Migrations/`, geçmiş tablosu `<urun>_v2.__EFMigrationsHistory`). Dev'de `Database:AutoMigrate`;
  üretime `dotnet ef migrations script --idempotent`. Migration üretmeden/uygulamadan önce sor.
- Şemalar `SchemaNames`: `identity_v2`, `logs_v2`, `<urun>_v2` — `public` boş kalır.
- Config: `sealed class XConfiguration : IEntityTypeConfiguration<X>`, şemaya göre gruplu dosyada
  (`<Urun>Configurations.cs` …): `ToTable("snake_name", SchemaNames.X)`, `HasMaxLength`, tenant içi benzersiz
  `HasIndex(e => new { e.CompanyId, e.Name }).IsUnique()`, firma FK `OnDelete(DeleteBehavior.Restrict)`, JSON `jsonb`.
- `<Urun>Context` named filter'ları reflection ile bağlar (SoftDelete `!e.IsDeleted`; Tenant
  `IsMaster || e.CompanyId == CompanyId`); `RowSig` max 128 konvansiyonla.
- Interceptor sırası: `AuditSaveChangesInterceptor` (delete→soft delete, `UpdatedAt`, `AuditLog` satırı,
  `Password|Hash|Secret|Token` maskeli) → `IntegritySignInterceptor` (`RowSig = HmacHelper.Sign`).
  **Elle audit satırı yazma**; `AuditLogs` `I<Urun>Context`'te bilinçli olarak yok. Ham SQL audit'i atlar.
- Cache `ICacheService` (FusionCache L1 + opsiyonel Redis L2), anahtarlar `CacheKeys`'te. Log Serilog
  (Console + opsiyonel Elastic). Dış HTTP typed `HttpClient` arkasında (`SmsOtpSender` örneği).
- Options: `configuration.GetSection("X").Get<T>()` → singleton. **Davranış anahtarları** (`Database:AutoMigrate`,
  `Seed:Enabled`, `OpenApi:Enabled`, `Https:Enforce`, `Sms:Enabled`, `Elastic:Enabled`, dev `Cors:Origins`)
  `AppProfile`'a aittir, **appsettings'e yazılmaz**. Sırlar user-secrets'ta; izlenen dosyaya asla.

## Api

```csharp
[ApiController][ApiVersion("1.0")][Route("api/v{version:apiVersion}/example-items")][Screen(ScreenCodes.Screen1)]
public class ExampleItemsController(IExampleItemService exampleItemService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ExampleItemDto>> Create(CreateExampleItemRequest request, CancellationToken cancellationToken)
        => Ok(await exampleItemService.CreateAsync(request, cancellationToken));

    [HttpDelete("{id:guid}")][ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    { await exampleItemService.DeleteAsync(id, cancellationToken); return NoContent(); }
}
```

- Controller ince: doğrulama, iş kuralı, try/catch **yok**. Rota kebab-case, `[controller]` token'ı **yasak**, kısıt `{id:guid}`.
- `[ProducesResponseType]` yazılmaz (OpenAPI transformer türetir); yalnız 204 ve standart dışı 403 (o zaman 200 de).
- Yetki: policy değil `[Screen(ScreenCodes.X)]` + `ScreenPermissionFilter`; operasyon HTTP verb'den
  (GET=Read, POST/PUT/PATCH=Write, DELETE=Delete), override yok. `FallbackPolicy` default-deny, açık uç `[AllowAnonymous]`.
  `[Authorize(AuthenticationSchemes = AuthSchemes.Otp)]` **yazılmaz**.
- Kimlik `ICurrentUser` (claim'ler `ClaimProtector` ile AES şifreli) — claim'i elle okuma.

## Yeni entity / modül kontrol listesi

1. Entity (`BaseEntity` + gereken marker'lar; imzalıysa `BuildSignaturePayload`).
2. `DbSet` hem `<Urun>Context`'e hem `I<Urun>Context`'e.
3. `IEntityTypeConfiguration` (şema, uzunluk, tenant içi unique, Restrict FK).
4. İmzalıysa `IntegrityScanner.RunAsync` listesine ekle.
5. Yeni ekran `ScreenCodes` + `DbInitializer` seed listesi.
6. Models + Validators + Service + interface; `AddApplication`'a kayıt.
7. Controller (`[Screen]`, versiyonlu rota).
8. Migration (önce sor). Yeni proje açıldıysa `NamespaceConventionTests.Projects`'e satır.
9. Testler (aşağıda).

## Testler

- xUnit + düz `Assert`; **Moq / NSubstitute / FluentAssertions / Testcontainers yok** — fake'ler elle
  (`TestKit/Fakes.cs`: `FakeCurrentUser`, `FakeCacheService`). Test adı Türkçe: `Create_AyniAd_ConflictDoner`.
- `UnitTests`: I/O yok (mimari, imza, security). `ServiceTests`: gerçek `<Urun>Context` + SQLite in-memory
  (`TestDb.CreateContext(currentUser, out connection)`, `TestCrypto.EnsureConfigured()`), Api referansı yok,
  modül başına klasör. Şablon `ServiceTests/Examples/ExampleItemServiceTests.cs`: constructor'da fake kullanıcı
  + seed + `ChangeTracker.Clear()`, sınıf `IDisposable`. Senaryolar: happy path, conflict (`ex.ErrorCode == "EXM-409"`),
  tenant izolasyonu, soft delete, tamper (`ExecuteSqlAsync("UPDATE …")` → SEC-001, ad `..._SEC001`).
- `IntegrationTests`: `ApiFactory : WebApplicationFactory<Program>`; **tek paylaşılan host** —
  `[Collection(ApiHostCollection.Name)]` + constructor'dan `ApiHostFixture` (yoksa Serilog "logger is already frozen").
- SQLite `xmin`, 23505, `timestamptz`, `jsonb`, partial index, şema ayrımını doğrulamaz: bunlara dokunduysan
  çıktıda "gerçek Postgres'te denenmedi" yaz.

## Doğrulama — bitti demeden önce

Ham verbose `dotnet build/test` koşma; hedef projenin kapısı:

```bash
powershell -ExecutionPolicy Bypass -File scripts/verify.ps1 2>&1 | tail -30
```

(Api'yi kaldırıp `/api/v1/system/ping` + `/health/ready` denetimi için `-Smoke`.) Kapı yoksa
`dotnet build 2>&1 | tail -20` ve `dotnet test 2>&1 | tail -20`. Yeşil değilse iş bitmemiştir.
