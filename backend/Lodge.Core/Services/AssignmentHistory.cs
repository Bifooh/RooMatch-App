using System.Text.Json;
using System.Text.Json.Serialization;
using Lodge.Core.Models;
using Lodge.Core.Validation;

namespace Lodge.Core.Services;

/// <summary>JSON before/after payload stored in assignment_change_history.</summary>
internal sealed class AssignmentSnapshotPayload
{
    [JsonPropertyName("entries")]
    public List<AssignmentSnapshotEntry> Entries { get; set; } = [];
}

internal sealed class AssignmentSnapshotEntry
{
    [JsonPropertyName("contractId")]
    public uint ContractId { get; set; }

    [JsonPropertyName("tenantCode")]
    public string? TenantCode { get; set; }

    [JsonPropertyName("apartmentId")]
    public uint? ApartmentId { get; set; }

    [JsonPropertyName("apartmentNumber")]
    public string? ApartmentNumber { get; set; }

    [JsonPropertyName("assigned")]
    public bool Assigned { get; set; }

    [JsonPropertyName("isLocked")]
    public bool IsLocked { get; set; }

    [JsonPropertyName("lockedBy")]
    public string? LockedBy { get; set; }

    [JsonPropertyName("lockedAt")]
    public DateTime? LockedAt { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

internal static class AssignmentHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string OperatorOrDefault(string? performedBy) =>
        string.IsNullOrWhiteSpace(performedBy) ? "Fictional Operator" : performedBy.Trim();

    public static AssignmentSnapshotEntry FromState(DraftState state, uint contractId)
    {
        state.Contracts.TryGetValue(contractId, out var contract);
        if (!state.AssignmentsByContract.TryGetValue(contractId, out var asg))
        {
            return new AssignmentSnapshotEntry
            {
                ContractId = contractId,
                TenantCode = contract?.TenantCode,
                Assigned = false,
                ApartmentId = null,
                ApartmentNumber = null,
                IsLocked = false,
                LockedBy = null,
                LockedAt = null,
                Source = null,
            };
        }

        string? aptNumber = null;
        if (state.Apartments.TryGetValue(asg.ApartmentId, out var apt))
            aptNumber = apt.ApartmentNumber;

        return new AssignmentSnapshotEntry
        {
            ContractId = contractId,
            TenantCode = contract?.TenantCode,
            Assigned = true,
            ApartmentId = asg.ApartmentId,
            ApartmentNumber = aptNumber,
            IsLocked = asg.IsLocked,
            LockedBy = asg.LockedBy,
            LockedAt = asg.LockedAt,
            Source = asg.Source,
        };
    }

    public static string Serialize(IEnumerable<AssignmentSnapshotEntry> entries) =>
        JsonSerializer.Serialize(new AssignmentSnapshotPayload { Entries = entries.ToList() }, JsonOptions);

    public static AssignmentSnapshotPayload Deserialize(string json) =>
        JsonSerializer.Deserialize<AssignmentSnapshotPayload>(json, JsonOptions)
        ?? new AssignmentSnapshotPayload();

    public static HistoryEntryDto ToDto(AssignmentSnapshotEntry e) => new(
        e.ContractId, e.TenantCode, e.ApartmentId, e.ApartmentNumber,
        e.Assigned, e.IsLocked, e.LockedBy, e.LockedAt, e.Source);

    /// <summary>True when live DB snapshots match the stored after_state (order-independent).</summary>
    public static bool MatchesLive(DraftState live, AssignmentSnapshotPayload expectedAfter)
    {
        foreach (var exp in expectedAfter.Entries)
        {
            var cur = FromState(live, exp.ContractId);
            if (!EntriesEqual(cur, exp)) return false;
        }
        return true;
    }

    public static bool EntriesEqual(AssignmentSnapshotEntry a, AssignmentSnapshotEntry b) =>
        a.ContractId == b.ContractId
        && a.Assigned == b.Assigned
        && a.ApartmentId == b.ApartmentId
        && a.IsLocked == b.IsLocked
        && string.Equals(a.LockedBy, b.LockedBy, StringComparison.Ordinal)
        && a.LockedAt == b.LockedAt;
}
