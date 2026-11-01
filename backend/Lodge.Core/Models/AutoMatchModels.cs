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

/// <summary>Compact counts for UI banners / clients that ignore placement lists.</summary>
public sealed record AutoMatchSummary(
    int Placed,
    int StillUnassigned,
    int MutualPairsPlaced,
    int OneWayJoinsPlaced,
    int Skipped,
    string Headline);

public sealed record AutoMatchResult(
    bool Ok,
    DraftInfo? Draft,
    int PlacedCount,
    int StillUnassigned,
    int MutualPairsPlaced,
    int OneWayJoinsPlaced,
    AutoMatchSummary Summary,
    IReadOnlyList<AutoMatchPlacement> Placements,
    IReadOnlyList<AutoMatchSkip> Skips,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ValidationIssueDto> Issues,
    string? Error = null);
