namespace MrHobist.AITeam.Application.Abstractions;

/// <summary>
/// Sahne yerlesimi (<c>config/scene.json</c> → <c>agents[]</c>) ile ekip (<c>config/agents/*.md</c>) arasindaki bag
/// (kullanici karari 2026-09-20): yeni ajan eklenince sahneye bos bir karakter sprite'i ve bos masa atanir; masa yoksa
/// ajan "ziyaretci" olur — sahnede evi yoktur, arada kapidan girip panoya bakar, cikar. Silinince sahneden de duser.
/// Api her degisiklikte <c>scene.reload</c> yayimlar; UI sahneyi yeniden kurar.
/// </summary>
public interface ISceneLayout
{
    /// <summary>
    /// Ajani sahneye ekler (yoksa) ya da adini gunceller (varsa). Sprite ve masa secimi burada; is kurali degil, yerlesim.
    /// Cagrilmasi UCUZDUR ve tekrarlanabilir: sahne kaydi TURETILMIS durumdur, ekipte olup sahnede olmayan ajan
    /// her kayitta kendiliginden yerlesir. Doner: sahne fiilen degisti mi (UI'a yeniden kurma yayini bunun icin).
    /// </summary>
    Task<bool> UpsertAgentAsync(string key, string name, CancellationToken ct);

    Task RemoveAgentAsync(string key, CancellationToken ct);
}
