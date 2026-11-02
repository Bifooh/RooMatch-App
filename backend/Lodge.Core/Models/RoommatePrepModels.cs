namespace Lodge.Core.Models;

public sealed record RoommateCandidate(
    uint TenantId,
    string TenantCode,
    string FirstName,
    string LastName,
    uint ContractId,
    string FloorPlanCode,
    string MatchKind);

public sealed record RoommatePrepItem(
    uint RequestId,
    uint ContractId,
    uint RequesterTenantId,
    string RequesterTenantCode,
    string RequesterName,
    string RequesterFloorPlanCode,
    string RawText,
    string Status,
    string? ExclusionReason,
    string ConflictKind,
    string SuggestedAction,
    IReadOnlyList<RoommateCandidate> Candidates);

public sealed record RoommatePrepSummary(
    int Pending,
    int Ambiguous,
    int Unresolved,
    int Excluded,
    int NeedsAttention,
    int ExactReady,
    int Duplicates,
    int Missing,
    int NearOnly);

public sealed record RoommatePrepPayload(
    uint TermId,
    string TermCode,
    RoommatePrepSummary Summary,
    IReadOnlyList<RoommatePrepItem> Items);

public sealed record ResolveRoommateRequest(
    uint ResolvedTenantId,
    string PerformedBy,
    string? Note);

public sealed record MarkRoommateRequest(
    string Status,
    string ExclusionReason,
    string PerformedBy,
    string? Note);

public sealed record RoommateMutationResult(
    bool Ok,
    IReadOnlyList<ValidationIssueDto> Issues,
    string? Error = null,
    RoommatePrepItem? Item = null);
