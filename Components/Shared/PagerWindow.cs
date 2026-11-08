namespace AiPulse.Components.Shared;

/// <summary>Which page buttons a pager shows: always the first and last page, a few around the current one, and gaps (0) between.</summary>
public static class PagerWindow
{
    /// <summary>Marks a gap ("…") in the result.</summary>
    public const int Gap = 0;

    /// <summary>
    /// Page numbers to render for <paramref name="current"/> of <paramref name="total"/>, with <see cref="Gap"/> where pages are skipped.
    /// <paramref name="neighbours"/> pages are shown on each side of the current one. A gap only ever replaces two or more pages; a single
    /// hidden page is shown instead, since "…" would take the same room. E.g. (5, 10) -> 1 … 4 5 6 … 10; (2, 10) -> 1 2 3 … 10.
    /// </summary>
    public static IReadOnlyList<int> Pages(int current, int total, int neighbours = 1)
    {
        if (total <= 0) return Array.Empty<int>();
        current = Math.Clamp(current, 1, total);
        neighbours = Math.Max(0, neighbours);

        var keep = new SortedSet<int> { 1, total };
        for (var p = current - neighbours; p <= current + neighbours; p++)
            if (p >= 1 && p <= total) keep.Add(p);

        var result = new List<int>();
        var previous = 0;
        foreach (var p in keep)
        {
            if (p - previous == 2) result.Add(p - 1);   // exactly one page skipped: show it rather than a gap
            else if (p - previous > 2) result.Add(Gap);
            result.Add(p);
            previous = p;
        }
        return result;
    }
}
