using Lodge.Core.Validation;

namespace Lodge.Core.Services;

/// <summary>
/// Deterministic V1 placement plan over a <see cref="DraftState"/> clone.
/// Prefers mutual <c>resolved</c> roommate pairs; soft-ignores pending/unresolved/ambiguous.
/// Does not invent identities or optimize weights.
/// </summary>
public static class AutoMatchPlanner
{
    public sealed record Step(uint ContractId, uint ApartmentId, string Reason);

    public sealed record Skip(uint ContractId, string TenantCode, string Reason);

    public sealed record Plan(
        IReadOnlyList<Step> Steps,
        IReadOnlyList<Skip> Skips,
        int MutualPairsPlaced,
        IReadOnlyList<string> Notes);

    public static Plan Build(DraftState initial)
    {
        var state = initial.Clone();
        var validator = new AssignmentValidator();
        var steps = new List<Step>();
        var skips = new List<Skip>();
        var notes = new List<string>
        {
            "V1 auto-match: greedy fill by floor plan; prefer mutual resolved roommate pairs.",
            "Unresolved / ambiguous / pending / excluded requests are soft-ignored (no invented matches).",
            "Every placement must still pass AssignmentValidator (capacity, plan, locks, restrictions).",
        };

        var tenantToContract = state.Contracts.Values
            .GroupBy(c => c.TenantId)
            .ToDictionary(g => g.Key, g => g.First().ContractId);

        var mutualPairs = FindMutualResolvedPairs(state, tenantToContract);
        notes.Add($"Mutual resolved pairs considered: {mutualPairs.Count}.");

        var mutualPlaced = 0;
        foreach (var (a, b) in mutualPairs.OrderBy(p => Math.Min(p.A, p.B)).ThenBy(p => Math.Max(p.A, p.B)))
        {
            if (!state.Contracts.TryGetValue(a, out var ca) || !state.Contracts.TryGetValue(b, out var cb))
                continue;

            if (ca.FloorPlanId != cb.FloorPlanId)
            {
                skips.Add(new Skip(a, ca.TenantCode,
                    $"Mutual with {cb.TenantCode} skipped: different floor plans ({ca.FloorPlanCode} vs {cb.FloorPlanCode})."));
                continue;
            }

            state.AssignmentsByContract.TryGetValue(a, out var asgA);
            state.AssignmentsByContract.TryGetValue(b, out var asgB);

            if (asgA is not null && asgB is not null)
            {
                if (asgA.ApartmentId == asgB.ApartmentId) mutualPlaced++;
                continue;
            }

            uint? targetApt = null;
            string reason;

            if (asgA is not null && asgB is null)
            {
                targetApt = asgA.ApartmentId;
                reason = $"Join mutual roommate {ca.TenantCode} in apartment.";
                if (TryPlace(state, validator, steps, skips, b, cb, targetApt.Value, reason))
                    mutualPlaced++;
            }
            else if (asgB is not null && asgA is null)
            {
                targetApt = asgB.ApartmentId;
                reason = $"Join mutual roommate {cb.TenantCode} in apartment.";
                if (TryPlace(state, validator, steps, skips, a, ca, targetApt.Value, reason))
                    mutualPlaced++;
            }
            else
            {
                var apt = FindApartmentWithFreeSlots(state, ca.FloorPlanId, needed: 2);
                if (apt is null)
                {
                    skips.Add(new Skip(a, ca.TenantCode,
                        $"No apartment with 2 free slots for mutual pair with {cb.TenantCode} ({ca.FloorPlanCode})."));
                    continue;
                }

                reason = $"Mutual pair with {cb.TenantCode}.";
                var okA = TryPlace(state, validator, steps, skips, a, ca, apt.ApartmentId, reason);
                var okB = TryPlace(state, validator, steps, skips, b, cb, apt.ApartmentId, reason);
                if (okA && okB) mutualPlaced++;
            }
        }

        // Greedy remainder: stable order by contract id.
        foreach (var contract in state.Contracts.Values.OrderBy(c => c.ContractId))
        {
            if (state.AssignmentsByContract.ContainsKey(contract.ContractId))
                continue;

            var apt = FindApartmentWithFreeSlots(state, contract.FloorPlanId, needed: 1);
            if (apt is null)
            {
                skips.Add(new Skip(contract.ContractId, contract.TenantCode,
                    $"No free capacity left for plan {contract.FloorPlanCode}."));
                continue;
            }

            TryPlace(state, validator, steps, skips, contract.ContractId, contract, apt.ApartmentId,
                "Greedy fill (same floor plan, first apartment with space).");
        }

        return new Plan(steps, skips, mutualPlaced, notes);
    }

    public static IReadOnlyList<(uint A, uint B)> FindMutualResolvedPairs(
        DraftState state, IReadOnlyDictionary<uint, uint> tenantToContract)
    {
        var resolved = state.RoommateRequests
            .Where(r => string.Equals(r.Status, "resolved", StringComparison.OrdinalIgnoreCase)
                        && r.ResolvedTenantId is not null)
            .ToList();

        var pairs = new HashSet<(uint, uint)>();
        foreach (var req in resolved)
        {
            if (!state.Contracts.TryGetValue(req.ContractId, out var requester))
                continue;
            if (!tenantToContract.TryGetValue(req.ResolvedTenantId!.Value, out var otherContractId))
                continue;

            var reverse = resolved.FirstOrDefault(r =>
                r.ContractId == otherContractId
                && r.ResolvedTenantId == requester.TenantId);
            if (reverse is null) continue;

            var lo = Math.Min(req.ContractId, otherContractId);
            var hi = Math.Max(req.ContractId, otherContractId);
            if (lo == hi) continue;
            pairs.Add((lo, hi));
        }

        return pairs.Select(p => (p.Item1, p.Item2)).ToList();
    }

    private static bool TryPlace(
        DraftState state,
        AssignmentValidator validator,
        List<Step> steps,
        List<Skip> skips,
        uint contractId,
        ContractState contract,
        uint apartmentId,
        string reason)
    {
        if (state.AssignmentsByContract.ContainsKey(contractId))
            return false;

        var issues = validator.ValidateMove(state, contractId, apartmentId);
        if (issues.Any(i => i.IsBlocking))
        {
            skips.Add(new Skip(contractId, contract.TenantCode,
                $"Validator blocked: {issues.First(i => i.IsBlocking).Code}"));
            return false;
        }

        state.AssignmentsByContract[contractId] = new AssignmentState(
            null, contractId, apartmentId, false, null, null, "generated");
        steps.Add(new Step(contractId, apartmentId, reason));
        return true;
    }

    private static ApartmentSnapshot? FindApartmentWithFreeSlots(DraftState state, uint floorPlanId, int needed)
    {
        return state.Apartments.Values
            .Where(a => a.IsActive && a.FloorPlanId == floorPlanId)
            .Select(a => (Apt: a, Free: a.EffectiveCapacity - state.OccupantsOf(a.ApartmentId).Count()))
            .Where(x => x.Free >= needed)
            .OrderByDescending(x => x.Free) // pack into roomier first so pairs fit
            .ThenBy(x => x.Apt.ApartmentId)
            .Select(x => x.Apt)
            .FirstOrDefault();
    }
}
