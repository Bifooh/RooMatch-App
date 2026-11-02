namespace Lodge.Core.Services;

/// <summary>
/// Candidate matching for roommate-request prep. Never invents identity:
/// exact and first-name matches only; near (edit-distance) is soft hint only.
/// Auto-matching / auto-resolve is out of scope.
/// </summary>
public static class RoommateNameMatcher
{
    public const string KindExact = "exact";
    public const string KindFirstName = "firstName";
    public const string KindNear = "near";

    public const string ConflictExact = "exact";
    public const string ConflictDuplicate = "duplicate";
    public const string ConflictMissing = "missing";
    public const string ConflictNearOnly = "nearOnly";

    public sealed record TenantName(
        uint TenantId,
        string TenantCode,
        string FirstName,
        string LastName,
        uint ContractId,
        string FloorPlanCode);

    public sealed record Candidate(
        TenantName Tenant,
        string MatchKind,
        int Distance);

    public static string Normalize(string? text) =>
        string.Join(' ', (text ?? string.Empty).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static IReadOnlyList<Candidate> FindCandidates(
        string rawText,
        IEnumerable<TenantName> pool,
        uint? excludeTenantId = null,
        int nearMaxDistance = 2)
    {
        var raw = Normalize(rawText);
        if (raw.Length == 0) return Array.Empty<Candidate>();

        var list = new List<Candidate>();
        foreach (var t in pool)
        {
            if (excludeTenantId is uint ex && t.TenantId == ex) continue;

            var first = Normalize(t.FirstName);
            var last = Normalize(t.LastName);
            var full = Normalize($"{first} {last}");
            var inverted = Normalize($"{last}, {first}");
            var lastFirst = Normalize($"{last} {first}");

            if (EqualsIgnoreCase(raw, full) || EqualsIgnoreCase(raw, inverted) || EqualsIgnoreCase(raw, lastFirst))
            {
                list.Add(new Candidate(t, KindExact, 0));
                continue;
            }

            // Single-token first-name match (e.g. "Jordan") — never auto-picked when multiple.
            if (!raw.Contains(' ') && !raw.Contains(',') && EqualsIgnoreCase(raw, first))
            {
                list.Add(new Candidate(t, KindFirstName, 0));
                continue;
            }

            // Soft near match on full name only — manager must confirm; never auto-resolve.
            var dist = Levenshtein(raw.ToLowerInvariant(), full.ToLowerInvariant());
            if (dist > 0 && dist <= nearMaxDistance)
                list.Add(new Candidate(t, KindNear, dist));
        }

        return list
            .OrderBy(c => c.MatchKind switch
            {
                KindExact => 0,
                KindFirstName => 1,
                _ => 2,
            })
            .ThenBy(c => c.Distance)
            .ThenBy(c => c.Tenant.LastName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Tenant.FirstName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static (string ConflictKind, string SuggestedAction) Classify(IReadOnlyList<Candidate> candidates)
    {
        var exact = candidates.Count(c => c.MatchKind == KindExact);
        var first = candidates.Count(c => c.MatchKind == KindFirstName);
        var near = candidates.Count(c => c.MatchKind == KindNear);

        if (exact == 1 && first == 0)
            return (ConflictExact, "Confirm the exact name match, or mark unresolved if wrong.");

        if (exact > 1 || first > 1 || (exact >= 1 && first >= 1))
            return (ConflictDuplicate, "Pick which person was meant, or leave ambiguous/unresolved.");

        if (exact == 0 && first == 1)
            return (ConflictExact, "Confirm the first-name match, or mark ambiguous if unsure.");

        if (exact == 0 && first == 0 && near > 0)
            return (ConflictNearOnly,
                "No exact match. Near spellings are hints only — pick one only if you know, otherwise mark unresolved.");

        return (ConflictMissing,
            "No tenant matches this wording. Mark unresolved (person not in list) or excluded with a reason.");
    }

    private static bool EqualsIgnoreCase(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Classic Levenshtein; used only for soft near hints.</summary>
    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
