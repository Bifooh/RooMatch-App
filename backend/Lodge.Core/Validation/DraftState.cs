namespace Lodge.Core.Validation;

/// <summary>
/// In-memory snapshot of a draft used by <see cref="AssignmentValidator"/>.
/// Kept free of MySQL types so unit tests can construct it directly.
/// </summary>
public sealed class DraftState
{
    public required uint DraftId { get; init; }
    public required uint TermId { get; init; }
    public required string Status { get; init; }
    public required string DraftName { get; init; }

    public Dictionary<uint, ContractState> Contracts { get; } = new();
    public Dictionary<uint, ApartmentSnapshot> Apartments { get; } = new();
    /// <summary>contract_id → assignment (at most one per contract).</summary>
    public Dictionary<uint, AssignmentState> AssignmentsByContract { get; } = new();
    public List<RestrictionState> Restrictions { get; } = new();
    public List<RoommateRequestState> RoommateRequests { get; } = new();
    public List<PreferenceState> Preferences { get; } = new();

    public bool IsEditable => Status == "draft";

    public IEnumerable<AssignmentState> OccupantsOf(uint apartmentId) =>
        AssignmentsByContract.Values.Where(a => a.ApartmentId == apartmentId);

    public DraftState Clone()
    {
        var copy = new DraftState
        {
            DraftId = DraftId,
            TermId = TermId,
            Status = Status,
            DraftName = DraftName,
        };
        foreach (var (k, v) in Contracts) copy.Contracts[k] = v;
        foreach (var (k, v) in Apartments) copy.Apartments[k] = v;
        foreach (var (k, v) in AssignmentsByContract) copy.AssignmentsByContract[k] = v with { };
        copy.Restrictions.AddRange(Restrictions);
        copy.RoommateRequests.AddRange(RoommateRequests);
        copy.Preferences.AddRange(Preferences);
        return copy;
    }
}

public sealed record ContractState(
    uint ContractId,
    uint TenantId,
    string TenantCode,
    string FirstName,
    string LastName,
    uint TermId,
    uint FloorPlanId,
    string FloorPlanCode,
    string TenantType,
    string? StayPreference = null);

public sealed record ApartmentSnapshot(
    uint ApartmentId,
    string ApartmentNumber,
    uint FloorPlanId,
    string FloorPlanCode,
    string Building,
    int Floor,
    int EffectiveCapacity,
    bool IsActive);

public sealed record AssignmentState(
    uint? AssignmentId,
    uint ContractId,
    uint ApartmentId,
    bool IsLocked,
    string? LockedBy,
    DateTime? LockedAt,
    string Source);

public sealed record RestrictionState(
    uint RestrictionId,
    uint ContractId,
    string RestrictionType,
    uint? ApartmentId,
    uint? OtherTenantId,
    string? Value,
    string Description);

public sealed record RoommateRequestState(
    uint RequestId,
    uint ContractId,
    string TenantCode,
    string RequesterName,
    string RawText,
    string Status,
    string? ExclusionReason,
    uint? ResolvedTenantId = null);

public sealed record PreferenceState(
    uint ContractId,
    bool HasAnimal,
    bool AnimalAllergyRestriction);
