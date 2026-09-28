namespace MrHobist.AITeam.Domain.Agents;

/// <summary>
/// Bir ajanin kopyalari (docs/DOMAIN.md → Kopyalar). Kopya 1 ajan anahtarinin kendisidir (eski kayitlar ve sahnedeki kalici
/// karakter degismez); sonrakiler <c>anahtar~2</c>, <c>anahtar~3</c>… <c>~</c> anahtarda gecersiz oldugu icin (<see cref="Identifiers"/>)
/// hicbir ajanla carpismaz. Kayitlarda <c>Agent</c> hep md anahtaridir; kilit, "mesgul" ve sahne kopya kimligini kullanir.
/// </summary>
public static class Workers
{
    public const char Separator = '~';

    /// <summary>Bir ajanin en fazla kopyasi: is havuzu ve kota bunun otesini tasimaz.</summary>
    public const int MaxInstances = 8;

    public static string Id(string agentKey, int instance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentKey);
        return instance <= 1 ? agentKey : $"{agentKey}{Separator}{instance}";
    }

    /// <summary>Kopyanin ajan anahtari; kopya degilse kendisi.</summary>
    public static string BaseOf(string worker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        var i = worker.LastIndexOf(Separator);
        return i > 0 && int.TryParse(worker.AsSpan(i + 1), out _) ? worker[..i] : worker;
    }

    /// <summary>Kopya numarasi (1'den); kopya degilse 1.</summary>
    public static int InstanceOf(string worker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        var i = worker.LastIndexOf(Separator);
        return i > 0 && int.TryParse(worker.AsSpan(i + 1), out var n) && n > 1 ? n : 1;
    }

    /// <summary>Ajanin butun kopya kimlikleri, 1'den <paramref name="instances"/>'a.</summary>
    public static IEnumerable<string> All(string agentKey, int instances)
        => Enumerable.Range(1, Math.Clamp(instances, 1, MaxInstances)).Select(n => Id(agentKey, n));
}
