namespace Lodge.Core.Models;

public sealed record MoveRequest(
    uint ContractId,
    uint? ApartmentId,
    string? PerformedBy = null,
    string? Reason = null);

public sealed record SwapRequest(
    uint ContractIdA,
    uint ContractIdB,
    string? PerformedBy = null,
    string? Reason = null);

public sealed record LockRequest(
    uint ContractId,
    bool Locked,
    string LockedBy,
    string? Reason = null);

public sealed record UndoRequest(
    ulong? HistoryId,
    string PerformedBy,
    string? Reason = null);

public sealed record HistoryListItem(
    ulong HistoryId,
    uint DraftId,
    uint TermId,
    string ChangeType,
    string PerformedBy,
    string? Reason,
    IReadOnlyList<HistoryEntryDto> Before,
    IReadOnlyList<HistoryEntryDto> After,
    ulong? UndoesHistoryId,
    ulong? UndoneByHistoryId,
    bool IsUndoable,
    DateTime CreatedAt);

public sealed record HistoryEntryDto(
    uint ContractId,
    string? TenantCode,
    uint? ApartmentId,
    string? ApartmentNumber,
    bool Assigned,
    bool IsLocked,
    string? LockedBy,
    DateTime? LockedAt,
    string? Source);

public sealed record MutationResult(
    bool Ok,
    IReadOnlyList<ValidationIssueDto> Issues,
    ulong? HistoryId = null);
