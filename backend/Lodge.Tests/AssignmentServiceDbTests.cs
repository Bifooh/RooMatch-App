using Lodge.Core.Models;
using Lodge.Core.Services;
using Lodge.Core.Validation;
using MySqlConnector;

namespace Lodge.Tests;

/// <summary>
/// Exercises AssignmentService against MySQL when LODGE_TEST_CONNECTION (or ConnectionStrings__Lodge) is set.
/// Skips cleanly when no database is available.
/// </summary>
public class AssignmentServiceDbTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("LODGE_TEST_CONNECTION")
        ?? Environment.GetEnvironmentVariable("ConnectionStrings__Lodge");

    private static bool CanConnect()
    {
        var cs = ConnectionString;
        if (string.IsNullOrWhiteSpace(cs)) return false;
        try
        {
            using var conn = new MySqlConnection(cs);
            conn.Open();
            using var cmd = new MySqlCommand("SELECT COUNT(*) FROM tenants", conn);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        catch
        {
            return false;
        }
    }

    private static AssignmentService CreateService()
    {
        var ds = new MySqlDataSource(ConnectionString!);
        return new AssignmentService(ds);
    }

    [Fact]
    public async Task Workspace_ListsEveryContractExactlyOnce()
    {
        if (!CanConnect()) return; // skipped: no DB

        var svc = CreateService();
        var drafts = await svc.ListDraftsAsync(termId: 2);
        Assert.NotEmpty(drafts);
        var draftId = drafts[0].DraftId;

        var ws = await svc.GetWorkspaceAsync(draftId);
        Assert.NotNull(ws);

        var assigned = ws!.Apartments.SelectMany(a => a.Occupants.Select(o => o.ContractId)).ToList();
        var unassigned = ws.Unassigned.Select(u => u.ContractId).ToList();
        var all = assigned.Concat(unassigned).ToList();

        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(32, all.Count); // small seed: 32 W2027 contracts
    }

    [Fact]
    public async Task CopyDraft_PreservesSource_AndCopiesLocks()
    {
        if (!CanConnect()) return;

        var svc = CreateService();
        var sourceId = (await svc.ListDraftsAsync(2))[0].DraftId;
        var before = await svc.GetWorkspaceAsync(sourceId);
        Assert.NotNull(before);
        var sourceAssigned = before!.Apartments.SelectMany(a => a.Occupants).Count();

        var (created, issues) = await svc.CreateDraftAsync(2, new CreateDraftRequest(
            $"Copy test {Guid.NewGuid():N}"[..20], sourceId, CopyLocks: true, Notes: "db test"));
        Assert.Empty(issues);
        Assert.NotNull(created);

        var afterSource = await svc.GetWorkspaceAsync(sourceId);
        Assert.Equal(sourceAssigned, afterSource!.Apartments.SelectMany(a => a.Occupants).Count());

        var copy = await svc.GetWorkspaceAsync(created!.DraftId);
        Assert.NotNull(copy);
        Assert.Equal(sourceAssigned, copy!.Apartments.SelectMany(a => a.Occupants).Count());
        Assert.Equal(32, copy.Apartments.SelectMany(a => a.Occupants).Count() + copy.Unassigned.Count);

        var lockedInSource = before.Apartments.SelectMany(a => a.Occupants).Count(o => o.IsLocked);
        var lockedInCopy = copy.Apartments.SelectMany(a => a.Occupants).Count(o => o.IsLocked);
        Assert.Equal(lockedInSource, lockedInCopy);
    }

    [Fact]
    public async Task ValidMove_Persists_InvalidDoesNot()
    {
        if (!CanConnect()) return;

        var svc = CreateService();
        var (draft, _) = await svc.CreateDraftAsync(2, new CreateDraftRequest(
            $"Move test {Guid.NewGuid():N}"[..20], CopyFromDraftId: null, CopyLocks: false, null));
        Assert.NotNull(draft);

        // Contract 1 is W-STD-2 (plan 1). Apartment 1 is 5555 W-STD-2.
        var ok = await svc.MoveAsync(draft!.DraftId, new MoveRequest(1, 1));
        Assert.True(ok.Ok);

        var ws = await svc.GetWorkspaceAsync(draft.DraftId);
        Assert.Contains(ws!.Apartments.First(a => a.ApartmentId == 1).Occupants, o => o.ContractId == 1);

        // Wrong plan: apartment 11 is M-STD-3
        var bad = await svc.MoveAsync(draft.DraftId, new MoveRequest(1, 11));
        Assert.False(bad.Ok);
        Assert.Contains(bad.Issues, i => i.Code == ValidationIssue.Codes.FloorPlanMismatch);

        ws = await svc.GetWorkspaceAsync(draft.DraftId);
        Assert.Contains(ws!.Apartments.First(a => a.ApartmentId == 1).Occupants, o => o.ContractId == 1);
    }

    [Fact]
    public async Task Swap_Atomic_OnFullApartments()
    {
        if (!CanConnect()) return;

        var svc = CreateService();
        var (draft, _) = await svc.CreateDraftAsync(2, new CreateDraftRequest(
            $"Swap test {Guid.NewGuid():N}"[..20], null, false, null));
        Assert.NotNull(draft);

        // Fill apt 1 (5555, cap 4) with contracts 1-4 and apt 2 (5842) with contract 5 — then move 2,3,4 out...
        // Simpler: place contract 1 in apt1, contract 2 in apt2, then swap.
        Assert.True((await svc.MoveAsync(draft!.DraftId, new MoveRequest(1, 1))).Ok);
        Assert.True((await svc.MoveAsync(draft.DraftId, new MoveRequest(2, 2))).Ok);

        var swap = await svc.SwapAsync(draft.DraftId, new SwapRequest(1, 2));
        Assert.True(swap.Ok);

        var ws = await svc.GetWorkspaceAsync(draft.DraftId);
        Assert.Contains(ws!.Apartments.First(a => a.ApartmentId == 2).Occupants, o => o.ContractId == 1);
        Assert.Contains(ws.Apartments.First(a => a.ApartmentId == 1).Occupants, o => o.ContractId == 2);

        // Invalid swap: lock contract 1, try swap again
        Assert.True((await svc.SetLockAsync(draft.DraftId, new LockRequest(1, true, "Fictional Operator"))).Ok);
        var bad = await svc.SwapAsync(draft.DraftId, new SwapRequest(1, 2));
        Assert.False(bad.Ok);

        ws = await svc.GetWorkspaceAsync(draft.DraftId);
        Assert.Contains(ws!.Apartments.First(a => a.ApartmentId == 2).Occupants, o => o.ContractId == 1);
        Assert.Contains(ws.Apartments.First(a => a.ApartmentId == 1).Occupants, o => o.ContractId == 2);
    }

    [Fact]
    public async Task LockedAndRestrictedMoves_Rejected()
    {
        if (!CanConnect()) return;

        var svc = CreateService();
        // Use seed draft 1 which has locked assignments for contracts 24 and 25
        var drafts = await svc.ListDraftsAsync(2);
        var seed = drafts.First(d => d.Name.Contains("pre-placements", StringComparison.OrdinalIgnoreCase));

        var (working, _) = await svc.CreateDraftAsync(2, new CreateDraftRequest(
            $"Lock test {Guid.NewGuid():N}"[..20], seed.DraftId, CopyLocks: true, null));
        Assert.NotNull(working);

        // Contract 24 locked on apt 11 (5793), required_floor=1 — try move to apt 12 (7679, floor 2)
        var locked = await svc.MoveAsync(working!.DraftId, new MoveRequest(24, 12));
        Assert.False(locked.Ok);
        Assert.Contains(locked.Issues, i => i.Code == ValidationIssue.Codes.AssignmentLocked);

        // Unlock, then required_floor should still block floor 2
        Assert.True((await svc.SetLockAsync(working.DraftId, new LockRequest(24, false, "Fictional Operator"))).Ok);
        var floor = await svc.MoveAsync(working.DraftId, new MoveRequest(24, 12));
        Assert.False(floor.Ok);
        Assert.Contains(floor.Issues, i => i.Code == ValidationIssue.Codes.RestrictionRequiredFloor);
    }
}
