using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MrHobist.AITeam.Infrastructure.Persistence;

/// <summary>
/// Database-first sema uygulayicisi (ARCHITECTURE.md §8). <c>scripts/sql/changes/*.sql</c> betikleri derlemeye
/// gomulu gelir; uygulanmamis olanlar SOZLUK SIRASINDA, her biri KENDI islemi icinde kosar ve deftere yazilir:
/// <c>schema_change_log(script_name, checksum, applied_at, applied_by)</c>.
///
/// Iki sert kural:
///   * Uygulanmis bir betigin checksum'i degistiyse kalkis DURUR -- "yayindan sonra betik degismez" kuralinin
///     sessizce ihlal edilmesindense gurultulu hata (docs/LESSONS.md: sessiz kabul en kotu hata).
///   * Betik icerigi burada UYDURULMAZ; dosyada ne varsa o kosar. Sema tek kaynaktan gelir.
///
/// Referans mimariden sapma: orada DDL ayri bir dagitim rolunde, replikalar kalkista DDL kosmaz. Burada tek
/// makinede tek surec var, dagitim adimi yok; kalkista uygulanir. Cok kopya gerekirse bu kaldirilir.
/// </summary>
internal static class SchemaMigrator
{
    private const string AppliedBy = "aiteam-api";

    // Tembel: statik baslatici icinde firlayan istisna TypeInitializationException'a sarilir ve asil mesaj gizlenir.
    private static readonly Lazy<IReadOnlyList<(string Name, string Sql)>> LazyScripts = new(LoadScripts);

    /// <summary>Gomulu betikler: ad -> icerik, sozluk sirasinda.</summary>
    internal static IReadOnlyList<(string Name, string Sql)> Scripts => LazyScripts.Value;

    /// <summary>Veritabanini hazirlar: dosya yolunu acar, WAL'a gecer, uygulanmamis betikleri kosar. Kosulan betik sayisini doner.</summary>
    public static int Apply(string databaseFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFile);
        var dir = Path.GetDirectoryName(Path.GetFullPath(databaseFile));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databaseFile }.ToString());
        connection.Open();

        // WAL: okuyucu (uclar) yazici (JobWorker) tarafindan engellenmez. Tek yazici kurali korunur (CLAUDE.md §2).
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA foreign_keys=ON;");
        Execute(connection, "PRAGMA busy_timeout=5000;");

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS schema_change_log (
                script_name TEXT NOT NULL PRIMARY KEY,
                checksum    TEXT NOT NULL,
                applied_at  TEXT NOT NULL,
                applied_by  TEXT NOT NULL
            );
            """);

        var applied = ReadLedger(connection);
        var count = 0;
        foreach (var (name, sql) in Scripts)
        {
            var checksum = Checksum(sql);
            if (applied.TryGetValue(name, out var recorded))
            {
                if (!string.Equals(recorded, checksum, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Sema betigi uygulandiktan sonra degismis: {name}. Ileri-yonlu kural geregi duzeltme YENI betikle gelir (ARCHITECTURE.md §8).");
                }

                continue;
            }

            using var tx = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            using (var log = connection.CreateCommand())
            {
                log.Transaction = tx;
                log.CommandText = "INSERT INTO schema_change_log (script_name, checksum, applied_at, applied_by) VALUES ($n, $c, $a, $b);";
                log.Parameters.AddWithValue("$n", name);
                log.Parameters.AddWithValue("$c", checksum);
                log.Parameters.AddWithValue("$a", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                log.Parameters.AddWithValue("$b", AppliedBy);
                log.ExecuteNonQuery();
            }

            tx.Commit();
            count++;
        }

        return count;
    }

    private static Dictionary<string, string> ReadLedger(SqliteConnection connection)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT script_name, checksum FROM schema_change_log;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetString(0)] = reader.GetString(1);
        }

        return map;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Satir sonu bicimi (CRLF/LF) checksum'i degistirmesin: Windows'ta klonlanan depo ayni sonucu vermeli.</summary>
    private static string Checksum(string sql)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n", StringComparison.Ordinal))));

    private static List<(string Name, string Sql)> LoadScripts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var list = new List<(string Name, string Sql)>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            // Gomulu ad "…Infrastructure.Sql.0001_xxx.sql" (csproj LinkBase="Sql"); sozluk sirasi icin yalniz dosya adi.
            // Bulunamazsa uydurma degil hata: bozuk ad deftere yazilirsa betik ileride ikinci kez kosar.
            var marker = resource.LastIndexOf(".Sql.", StringComparison.Ordinal);
            if (marker < 0)
            {
                throw new InvalidOperationException($"Sema betigi beklenmeyen kaynak adiyla gomulmus: {resource}. csproj'daki LinkBase 'Sql' olmali.");
            }

            list.Add((resource[(marker + ".Sql.".Length)..], reader.ReadToEnd()));
        }

        if (list.Count == 0)
        {
            throw new InvalidOperationException("Sema betikleri gomulmemis: scripts/sql/changes/*.sql bos ya da csproj EmbeddedResource kaydi dusmus.");
        }

        return [.. list.OrderBy(x => x.Name, StringComparer.Ordinal)];
    }
}
