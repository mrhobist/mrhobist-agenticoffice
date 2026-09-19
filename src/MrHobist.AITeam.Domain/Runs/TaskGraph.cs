namespace MrHobist.AITeam.Domain.Runs;

public static class TaskGraph
{
    /// <summary>
    /// Bagimlilik sirasina gore topolojik siralama. Bilinmeyen bagimlilik yok sayilir;
    /// donguye girenler verildigi sirada sona eklenir — calisma patlamaz, sira korunur.
    /// </summary>
    public static IReadOnlyList<RunTask> Order(IReadOnlyList<RunTask> tasks)
    {
        var known = tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var remaining = tasks.ToList();
        var ordered = new List<RunTask>(tasks.Count);

        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(t => t.DependsOn.All(d => done.Contains(d) || !known.Contains(d)))
                .ToList();
            if (ready.Count == 0)
            {
                ordered.AddRange(remaining);
                break;
            }

            foreach (var t in ready)
            {
                ordered.Add(t);
                done.Add(t.Id);
                remaining.Remove(t);
            }
        }

        return ordered;
    }
}
