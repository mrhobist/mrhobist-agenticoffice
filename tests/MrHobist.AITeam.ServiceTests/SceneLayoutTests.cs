using System.Text.Json;
using MrHobist.AITeam.Infrastructure.Storage;

namespace MrHobist.AITeam.ServiceTests;

/// <summary>Yeni ajan sahneye yerlesir: bos sprite + bos masa, masa yoksa ziyaretci (home: {}); silinince duser (docs/SCENE.md).</summary>
public sealed class SceneLayoutTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private JsonDocument Scene() => JsonDocument.Parse(File.ReadAllText(_fx.Paths.ConfigFile("scene.json")));

    [Fact]
    public async Task Ekipte_olup_sahnede_olmayan_ajan_kayitta_kendiliginden_yerlesir()
    {
        // Md dosyasi elle eklendiyse sahne kaydi yoktur ve ajan ofiste gorunmez. Sahne kaydi TURETILMIS
        // durumdur: herhangi bir kayit onu onarmali (2026-09-21). Onceden yalniz AD degisince yaziliyordu.
        var store = new JsonSceneLayoutStore(_fx.Paths);
        var ct = CancellationToken.None;

        Assert.True(await store.UpsertAgentAsync("yeni-ajan", "Yeni Ajan", ct));   // eklendi
        Assert.False(await store.UpsertAgentAsync("yeni-ajan", "Yeni Ajan", ct));  // degisiklik yok → yazma yok
        Assert.True(await store.UpsertAgentAsync("yeni-ajan", "Baska Ad", ct));    // ad degisti

        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(_fx.Paths.ConfigFile("scene.json"), ct))!;
        var agents = json["agents"]!.AsArray();
        var added = agents.First(a => a!["key"]!.GetValue<string>() == "yeni-ajan")!;
        Assert.Equal("Baska Ad", added["name"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(added["sprite"]!.GetValue<string>()));

        // Sprite ve masa ATANMIS olmali. Benzersizlik degismez DEGILDIR: havuz tukenince sprite bilincli
        // olarak tekrar kullanilir, masa kalmayinca ajan "ziyaretci" olur (docs/SCENE.md → Ajan yerlesimi).
    }

    /// <summary>Kullanici istegi 2026-09-23: ekipteki karakterin gorunumu secilsin. Secim yazilir, null korur, bilinmeyen reddedilir.</summary>
    [Fact]
    public async Task Karakter_secilir_bos_birakilinca_korunur_bilinmeyen_reddedilir()
    {
        var store = new JsonSceneLayoutStore(_fx.Paths);
        var ct = CancellationToken.None;
        var available = (await store.SpritesAsync(ct)).Available;
        var pick = available[^1];

        Assert.True(await store.UpsertAgentAsync("secen", "Seçen", ct, pick));          // yeni ajan secilen karakterle
        Assert.Equal(pick, (await store.SpritesAsync(ct)).ByAgent["secen"]);
        Assert.False(await store.UpsertAgentAsync("secen", "Seçen", ct));               // null: korunur, yazma yok
        Assert.True(await store.UpsertAgentAsync("secen", "Seçen", ct, available[0]));  // degistirildi
        Assert.Equal(available[0], (await store.SpritesAsync(ct)).ByAgent["secen"]);

        var ex = await Assert.ThrowsAsync<Domain.DomainException>(() => store.UpsertAgentAsync("secen", "Seçen", ct, "yok-boyle"));
        Assert.Equal(Domain.ErrorCodes.AgentUnknownSprite, ex.ErrorCode);
    }

    [Fact]
    public async Task Yeni_ajan_bos_sprite_ve_bos_masa_alir_masa_bitince_ziyaretci_olur_silinince_duser()
    {
        var store = new JsonSceneLayoutStore(_fx.Paths);
        var ct = CancellationToken.None;

        // Sevk edilen sahnede bos masa OLMAYABILIR (2026-09-21: 10 ajan, 9 masa). Kural masa sayisina bagli
        // degil: bos masa varsa alinir, bitince ziyaretci. Testi kurala baglamak icin once en az iki masa acilir.
        using (var initial = Scene())
        {
            var agents = initial.RootElement.GetProperty("agents").EnumerateArray().ToList();
            var seatCount = initial.RootElement.GetProperty("seats").EnumerateObject().Count();
            var seated = agents.Where(a => a.GetProperty("home").TryGetProperty("seat", out _)).Select(a => a.GetProperty("key").GetString()!).ToList();
            // Sondan basa: en az 2 masa bosalana kadar koltuklu ajan kaldir.
            while (seated.Count > 0 && seatCount - seated.Count < 2)
            {
                await store.RemoveAgentAsync(seated[^1], ct);
                seated.RemoveAt(seated.Count - 1);
            }
        }

        int baseCount;
        List<string> freeSeats;
        int spriteCount;
        using (var before = Scene())
        {
            var agents = before.RootElement.GetProperty("agents").EnumerateArray().ToList();
            baseCount = agents.Count;
            var taken = agents.Select(a => a.GetProperty("home").TryGetProperty("seat", out var s) ? s.GetString() : null).Where(s => s is not null).ToHashSet();
            freeSeats = before.RootElement.GetProperty("seats").EnumerateObject().Select(p => p.Name).Where(n => !taken.Contains(n)).ToList();
            spriteCount = before.RootElement.GetProperty("sprites").GetArrayLength();
            Assert.NotEmpty(freeSeats);
        }

        // Bos masa sayisi + 1 ajan: her biri siradaki bos masayi alir, sonuncusu masasiz (ziyaretci, home: {}) kalir.
        var keys = new List<string>();
        for (var i = 0; i <= freeSeats.Count; i++)
        {
            keys.Add($"yeni-{i}");
            await store.UpsertAgentAsync(keys[i], $"Yeni {i}", ct);
        }

        using (var after = Scene())
        {
            var agents = after.RootElement.GetProperty("agents").EnumerateArray().ToDictionary(a => a.GetProperty("key").GetString()!);
            Assert.Equal(baseCount + keys.Count, agents.Count);
            for (var i = 0; i < freeSeats.Count; i++)
            {
                Assert.Equal(freeSeats[i], agents[keys[i]].GetProperty("home").GetProperty("seat").GetString());
            }

            Assert.False(agents[keys[^1]].GetProperty("home").TryGetProperty("seat", out _)); // masa kalmadi → ziyaretci
            var sprites = agents.Values.Select(a => a.GetProperty("sprite").GetString()!).ToList();
            Assert.Equal(Math.Min(spriteCount, agents.Count), sprites.Distinct().Count()); // once kullanilmayan sprite'lar, sonra tekrar
            Assert.True(after.RootElement.TryGetProperty("_comment", out _)); // dosyanin geri kalani korunur
        }

        // Ad guncelleme yeni kayit acmaz; silme duser.
        await store.UpsertAgentAsync(keys[^1], "Dokümantasyon", ct);
        await store.RemoveAgentAsync(keys[0], ct);
        using var final = Scene();
        var list = final.RootElement.GetProperty("agents").EnumerateArray().ToList();
        Assert.Equal(baseCount + keys.Count - 1, list.Count);
        Assert.DoesNotContain(list, a => a.GetProperty("key").GetString() == keys[0]);
        Assert.Equal("Dokümantasyon", list.Single(a => a.GetProperty("key").GetString() == keys[^1]).GetProperty("name").GetString());
    }
}
