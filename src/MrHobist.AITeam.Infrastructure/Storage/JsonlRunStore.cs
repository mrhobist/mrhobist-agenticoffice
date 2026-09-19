using System.Text.Json;
using System.Text.Json.Serialization;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Domain.Runs;

namespace MrHobist.AITeam.Infrastructure.Storage;

/// <summary>
/// <c>runs/{id}/</c>:
/// <c>run.json</c> ustveri · <c>spec.json</c> gorev grafigi · <c>conversations/{agent}.jsonl</c> ajan basina turlar ·
/// <c>messages.jsonl</c> ajanlar arasi · <c>tasks/{task}/phases.jsonl</c> gorev basina fazlar.
/// Neden ayri dosyalar: bir ajanin gecmisini okumak (panel) ile bir gorevin faz akisini okumak (pano) farkli sorular.
/// </summary>
public sealed class JsonlRunStore(StoragePaths paths) : IRunStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions JsonIndented = new(Json) { WriteIndented = true };

    public Task CreateAsync(Run run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var dir = RunDir(run.Id);
        Directory.CreateDirectory(Path.Combine(dir, "conversations"));
        Directory.CreateDirectory(Path.Combine(dir, "tasks"));
        return WriteJsonAsync(Path.Combine(dir, "run.json"), run, ct);
    }

    public Task UpdateAsync(Run run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        return WriteJsonAsync(Path.Combine(RunDir(run.Id), "run.json"), run, ct);
    }

    public Task WriteSpecAsync(string runId, Spec spec, CancellationToken ct)
        => WriteJsonAsync(Path.Combine(RunDir(runId), "spec.json"), spec, ct);

    public Task AppendTurnAsync(string runId, Turn turn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return AppendAsync(Path.Combine(RunDir(runId), "conversations", Safe(turn.Agent) + ".jsonl"), turn, ct);
    }

    public Task AppendMessageAsync(string runId, Message message, CancellationToken ct)
        => AppendAsync(Path.Combine(RunDir(runId), "messages.jsonl"), message, ct);

    public Task AppendPhaseAsync(string runId, Phase phase, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(phase);
        return AppendAsync(Path.Combine(RunDir(runId), "tasks", Safe(phase.Task), "phases.jsonl"), phase, ct);
    }

    public Task<Run?> GetAsync(string runId, CancellationToken ct)
        => ReadJsonAsync<Run>(Path.Combine(RunDir(runId), "run.json"), ct);

    public async Task<IReadOnlyList<Run>> ListAsync(int limit, CancellationToken ct)
    {
        if (!Directory.Exists(paths.RunsRoot))
        {
            return [];
        }

        var dirs = Directory.EnumerateDirectories(paths.RunsRoot)
            .Where(d => File.Exists(Path.Combine(d, "run.json")))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Take(limit);
        var runs = new List<Run>();
        foreach (var d in dirs)
        {
            var run = await ReadJsonAsync<Run>(Path.Combine(d, "run.json"), ct).ConfigureAwait(false);
            if (run is not null)
            {
                runs.Add(run);
            }
        }

        return runs;
    }

    public Task<Spec?> ReadSpecAsync(string runId, CancellationToken ct)
        => ReadJsonAsync<Spec>(Path.Combine(RunDir(runId), "spec.json"), ct);

    public Task<IReadOnlyList<Turn>> ReadTurnsAsync(string runId, string agent, CancellationToken ct)
        => AtomicFile.ReadJsonLinesAsync(Path.Combine(RunDir(runId), "conversations", Safe(agent) + ".jsonl"), ParseLine<Turn>, ct);

    public Task<IReadOnlyList<Message>> ReadMessagesAsync(string runId, CancellationToken ct)
        => AtomicFile.ReadJsonLinesAsync(Path.Combine(RunDir(runId), "messages.jsonl"), ParseLine<Message>, ct);

    public Task<IReadOnlyList<Phase>> ReadPhasesAsync(string runId, string task, CancellationToken ct)
        => AtomicFile.ReadJsonLinesAsync(Path.Combine(RunDir(runId), "tasks", Safe(task), "phases.jsonl"), ParseLine<Phase>, ct);

    public Task<IReadOnlyList<string>> ListTasksAsync(string runId, CancellationToken ct)
    {
        var dir = Path.Combine(RunDir(runId), "tasks");
        IReadOnlyList<string> tasks = Directory.Exists(dir)
            ? Directory.EnumerateDirectories(dir).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).Order(StringComparer.Ordinal).ToList()
            : [];
        return Task.FromResult(tasks);
    }

    private string RunDir(string runId)
    {
        var dir = Path.GetFullPath(Path.Combine(paths.RunsRoot, Safe(runId)));
        if (!dir.StartsWith(Path.GetFullPath(paths.RunsRoot), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"runs/ disina erisim reddedildi: {runId}");
        }

        return dir;
    }

    /// <summary>Dosya adina donusen degerler: yalniz harf, rakam, nokta, tire, alt cizgi.</summary>
    private static string Safe(string value)
    {
        var chars = (value ?? "").Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray();
        // Bas/son tire ve nokta atilir: '..' gibi adlar hic olusmaz.
        var s = new string(chars).Trim('-', '.');
        return s.Length == 0 ? "x" : s[..Math.Min(s.Length, 60)];
    }

    private static Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
        => AtomicFile.WriteAsync(path, JsonSerializer.Serialize(value, JsonIndented) + "\n", ct);

    private static Task AppendAsync<T>(string path, T value, CancellationToken ct)
        => AtomicFile.AppendLineAsync(path, JsonSerializer.Serialize(value, Json), ct);

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T? ParseLine<T>(string line)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(line, Json);
        }
        catch (JsonException)
        {
            // Yarim yazilmis satir: yok say, geri kalani kurtar.
            return null;
        }
    }
}
