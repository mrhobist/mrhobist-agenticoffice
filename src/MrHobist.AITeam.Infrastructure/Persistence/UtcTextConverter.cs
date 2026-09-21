using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MrHobist.AITeam.Infrastructure.Persistence;

/// <summary>
/// Zaman damgasi -> SIRALANABILIR UTC metni (<c>2026-09-21T06:12:50.3730000Z</c>).
///
/// Neden elle: SQLite'in kendi <c>DateTimeOffset</c> esleme bicimi ofseti tasir ve saglayici
/// "SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses" diyerek
/// SIRALAMAYI REDDEDER. Calisma listesi (yeni -> eski) veritabaninda siralanmak zorunda oldugu icin
/// damga once UTC'ye cevrilir, sonra sabit genislikte yazilir: sozluk sirasi = zaman sirasi.
///
/// Yazma KATI (tek bicim, siralama garantisi bundan gelir), okuma TOLERANSLI: elle SQL ile ya da baska
/// bir aracla yazilmis herhangi bir ISO-8601 damga (ofsetli, daha az kesirli) okunur ve UTC'ye cekilir.
/// Aksi halde tek bozuk satir o tabloyu okuyan her sorguyu dusururdu.
///
/// Bedeli: yerel ofset bilgisi saklanmaz. Uygulama zaten her yerde <c>DateTimeOffset.UtcNow</c> uretiyor.
/// </summary>
internal sealed class UtcTextConverter() : ValueConverter<DateTimeOffset, string>(
    v => v.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture),
    v => DateTimeOffset.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))
{
    private const string Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";
}
