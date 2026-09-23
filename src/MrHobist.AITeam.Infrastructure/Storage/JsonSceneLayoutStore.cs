using System.Text.Json;
using System.Text.Json.Nodes;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary>
/// <c>config/scene.json</c> icindeki <c>agents[]</c>'i duzenler; dosyanin geri kalanina (yorumlar, koordinatlar) dokunmaz.
/// Sprite: <c>sprites[]</c> listesinden kullanilmayan ilk; hepsi doluysa sirayla tekrar. Masa: <c>seats</c> icinde hicbir
/// ajanin evi olmayan ilk anahtar (dosya sirasi); yoksa <c>home: {}</c> → ziyaretci (docs/SCENE.md).
/// </summary>
public sealed class JsonSceneLayoutStore(StoragePaths paths) : ISceneLayout
{
    /// <summary>scene.json'da <c>sprites</c> yoksa: build'deki karakter sayfalari (scripts/build-sprites.py).</summary>
    private static readonly string[] FallbackSprites = ["shirt-tie", "green-hoodie", "ponytail", "bun", "blond-maroon", "curly-yellow", "hipster", "blue-hoodie"];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private string File => paths.ConfigFile("scene.json");

    public async Task<bool> UpsertAgentAsync(string key, string name, CancellationToken ct, string? sprite = null)
    {
        var root = await ReadAsync(ct).ConfigureAwait(false);
        var agents = root["agents"] as JsonArray ?? throw new DomainException(ErrorCodes.ConfigFileInvalid, "scene.json: 'agents' dizisi yok.");
        var sprites = SpriteList(root);
        if (sprite is not null && !sprites.Contains(sprite, StringComparer.Ordinal))
        {
            throw new DomainException(ErrorCodes.AgentUnknownSprite, $"'{sprite}' diye bir karakter yok ({string.Join(", ", sprites)}).");
        }

        var existing = agents.OfType<JsonObject>().FirstOrDefault(a => a["key"]?.GetValue<string>() == key);
        if (existing is not null)
        {
            // Ad ve karakter ayniysa yazma: her ajan kaydinda scene.json'i bosuna degistirmeyelim.
            var spriteChanged = sprite is not null && existing["sprite"]?.GetValue<string>() != sprite;
            if (existing["name"]?.GetValue<string>() == name && !spriteChanged)
            {
                return false;
            }

            existing["name"] = name;
            if (spriteChanged)
            {
                existing["sprite"] = sprite;
            }

            await WriteAsync(root, ct).ConfigureAwait(false);
            return true;
        }

        var used = agents.OfType<JsonObject>().Select(a => a["sprite"]?.GetValue<string>() ?? "").ToHashSet(StringComparer.Ordinal);
        sprite ??= sprites.FirstOrDefault(s => !used.Contains(s)) ?? sprites[agents.Count % Math.Max(1, sprites.Count)];

        var takenSeats = agents.OfType<JsonObject>().Select(a => a["home"]?["seat"]?.GetValue<string>()).Where(s => s is not null).ToHashSet(StringComparer.Ordinal);
        var seat = (root["seats"] as JsonObject)?.Select(kv => kv.Key).FirstOrDefault(s => !takenSeats.Contains(s));

        var home = new JsonObject();
        if (seat is not null)
        {
            home["seat"] = seat;
        }

        agents.Add(new JsonObject { ["key"] = key, ["name"] = name, ["sprite"] = sprite, ["home"] = home });
        await WriteAsync(root, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<SceneSprites> SpritesAsync(CancellationToken ct)
    {
        var root = await ReadAsync(ct).ConfigureAwait(false);
        var byAgent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in (root["agents"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (a["key"]?.GetValue<string>() is { } k && a["sprite"]?.GetValue<string>() is { } s)
            {
                byAgent[k] = s;
            }
        }

        return new SceneSprites(SpriteList(root), byAgent);
    }

    private static List<string> SpriteList(JsonObject root)
        => (root["sprites"] as JsonArray)?.Select(s => s?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList() ?? [.. FallbackSprites];

    public async Task RemoveAgentAsync(string key, CancellationToken ct)
    {
        var root = await ReadAsync(ct).ConfigureAwait(false);
        if (root["agents"] is not JsonArray agents)
        {
            return;
        }

        var item = agents.OfType<JsonObject>().FirstOrDefault(a => a["key"]?.GetValue<string>() == key);
        if (item is null)
        {
            return;
        }

        agents.Remove(item);
        await WriteAsync(root, ct).ConfigureAwait(false);
    }

    private async Task<JsonObject> ReadAsync(CancellationToken ct)
    {
        if (!System.IO.File.Exists(File))
        {
            throw new DomainException(ErrorCodes.ConfigFileMissing, "config/scene.json yok.");
        }

        try
        {
            return JsonNode.Parse(await System.IO.File.ReadAllTextAsync(File, ct).ConfigureAwait(false)) as JsonObject
                ?? throw new DomainException(ErrorCodes.ConfigFileInvalid, "scene.json nesne degil.");
        }
        catch (JsonException ex)
        {
            throw new DomainException(ErrorCodes.ConfigFileInvalid, $"scene.json gecersiz JSON: {ex.Message}");
        }
    }

    private Task WriteAsync(JsonObject root, CancellationToken ct) => AtomicFile.WriteAsync(File, root.ToJsonString(Json) + "\n", ct);
}
