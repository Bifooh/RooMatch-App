namespace Lodge.Core.Models;

public sealed record AutoMatchRequest(
    string? DraftName,
    uint? CopyFromDraftId,
    bool CopyLocks,
    string PerformedBy,
    string? Notes);

public sealed record AutoMatchPlacement(
    uint ContractId,
    string TenantCode,
    uint ApartmentId,
    string ApartmentNumber,
    string Reason,
    ulong? HistoryId);

public sealed record AutoMatchSkip(
    uint ContractId,
    string TenantCode,
    string Reason);

public sealed record AutoMatchResult(
    bool Ok,
    DraftInfo? Draft,
    int PlacedCount,
    int StillUnassigned,
    int MutualPairsPlaced,
    IReadOnlyList<AutoMatchPlacement> Placements,
    IReadOnlyList<AutoMatchSkip> Skips,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ValidationIssueDto> Issues);
