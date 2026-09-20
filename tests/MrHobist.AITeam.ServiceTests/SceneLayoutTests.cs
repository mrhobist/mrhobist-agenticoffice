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
    public async Task Yeni_ajan_bos_sprite_ve_bos_masa_alir_masa_bitince_ziyaretci_olur_silinince_duser()
    {
        var store = new JsonSceneLayoutStore(_fx.Paths);
        var ct = CancellationToken.None;

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
