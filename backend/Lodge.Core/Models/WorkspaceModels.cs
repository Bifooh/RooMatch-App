namespace Lodge.Core.Models;

public sealed record TermInfo(uint TermId, string Code, string Name, DateOnly StartDate, DateOnly EndDate);

public sealed record DraftInfo(
    uint DraftId,
    uint TermId,
    string Name,
    string Source,
    string Status,
    string? Notes,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CreateDraftRequest(
    string Name,
    uint? CopyFromDraftId,
    bool CopyLocks,
    string? Notes);

public sealed record ValidationIssueDto(
    string Code,
    string Severity,
    string Message,
    uint? ContractId,
    string? TenantCode,
    uint? ApartmentId,
    string? ApartmentNumber,
    uint? RelatedContractId,
    string? RelatedTenantCode);

public sealed record RoommateRequestInfo(
    uint RequestId,
    uint ContractId,
    string TenantCode,
    string RequesterName,
    string RawText,
    string Status,
    string? ExclusionReason);

public sealed record RestrictionInfo(
    uint RestrictionId,
    uint ContractId,
    string RestrictionType,
    uint? ApartmentId,
    uint? OtherTenantId,
    string? Value,
    string Description);

public sealed record AssignedTenantInfo(
    uint ContractId,
    uint TenantId,
    string TenantCode,
    string FirstName,
    string LastName,
    uint? AssignmentId,
    bool IsLocked,
    string? LockedBy,
    DateTime? LockedAt,
    string? AssignmentSource);

public sealed record ApartmentWorkspaceRow(
    uint ApartmentId,
    string ApartmentNumber,
    string FloorPlanCode,
    uint FloorPlanId,
    string Building,
    int Floor,
    int EffectiveCapacity,
    bool IsActive,
    IReadOnlyList<AssignedTenantInfo> Occupants);

public sealed record UnassignedTenantInfo(
    uint ContractId,
    uint TenantId,
    string TenantCode,
    string FirstName,
    string LastName,
    string FloorPlanCode,
    uint FloorPlanId,
    string TenantType);

public sealed record WorkspacePayload(
    DraftInfo Draft,
    TermInfo Term,
    IReadOnlyList<ApartmentWorkspaceRow> Apartments,
    IReadOnlyList<UnassignedTenantInfo> Unassigned,
    IReadOnlyList<RoommateRequestInfo> RoommateRequests,
    IReadOnlyList<RestrictionInfo> Restrictions,
    IReadOnlyList<ValidationIssueDto> Issues,
    IReadOnlyList<string> ValidationLimitations);
