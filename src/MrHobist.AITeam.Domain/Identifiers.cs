using System.Text.RegularExpressions;

namespace MrHobist.AITeam.Domain;

/// <summary>Dosya adina donusen anahtarlar: ajan, bilgi dosyasi, adim, gorev.</summary>
public static partial class Identifiers
{
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial Regex SafeKey();

    public static bool IsValidKey(string? value) => value is not null && SafeKey().IsMatch(value);

    /// <summary>Gecerli degilse <see cref="DomainException"/> firlatir; hata kodu cagirana gore degisir.</summary>
    public static string Require(string? value, string errorCode, string what)
    {
        if (!IsValidKey(value))
        {
            throw new DomainException(errorCode, $"Gecersiz {what} anahtari: '{value}'. Kucuk harf, rakam, '-' ve '_' kullanin.");
        }

        return value!;
    }
}
