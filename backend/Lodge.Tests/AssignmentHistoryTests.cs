using Lodge.Core.Models;
using Lodge.Core.Services;
using Lodge.Core.Validation;
using MySqlConnector;

namespace Lodge.Tests;

/// <summary>
/// History + undo against MySQL when LODGE_TEST_CONNECTION / ConnectionStrings__Lodge is set.
/// Requires migration 001_assignment_change_history.sql applied.
/// </summary>
public class AssignmentHistoryTests
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
            using var cmd = new MySqlCommand(
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'assignment_change_history'",
                conn);
            return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
        }
        catch
        {
            return false;
        }
    }

    private static AssignmentService CreateService() => new(new MySqlDataSource(ConnectionString!));

    [Fact]
    public async Task Move_WritesHistory_AndUndo_Restores()
    {
        if (!CanConnect()) return;

        var svc = CreateService();
        var (draft, _) = await svc.CreateDraftAsync(2, new CreateDraftRequest(
            $"Hist {Guid.NewGuid():N}"[..18], null, false, null));
        Assert.NotNull(draft);

        var move = await svc.MoveAsync(draft!.DraftId, new MoveRequest(1, 1, "Op A", "test move"));
        Assert.True(move.Ok);
        Assert.NotNull(move.HistoryId);

        var hist = await svc.ListHistoryAsync(draft.DraftId);
        Assert.Contains(hist, h => h.HistoryId == move.HistoryId && h.ChangeType == "move" && h.IsUndoable);

        var ws = await svc.GetWorkspaceAsync(draft.DraftId);
        Assert.Contains(ws!.Apartments.First(a => a.ApartmentId == 1).Occupants, o => o.ContractId == 1);

        var undo = await svc.UndoAsync(draft.DraftId, new UndoRequest(null, "Op B", null));
        Assert.True(undo.Ok);

        ws = await svc.GetWorkspaceAsync(draft.DraftId);
        Assert.DoesNotContain(ws!.Apartments.SelectMany(a => a.Occupants), o => o.ContractId == 1);
        Assert.Contains(ws.Unassigned, u => u.ContractId == 1);

        hist = await svc.ListHistoryAsync(draft.DraftId);
        var original = hist.First(h => h.HistoryId == move.HistoryId);
        Assert.False(original.IsUndoable);
        Assert.NotNull(original.UndoneByHistoryId);
    }

    [Fact]
    public async Task Undo_ByHistoryId_And_RejectsSecondUndo()
    {
        if (!CanConnect()) return;

        var svc = CreateService();
        var (draft, _) = await svc.CreateDraftAsync(2, new CreateDraftRequest(
            $"Hist2 {Guid.NewGuid():N}"[..18], null, false, null));
        Assert.NotNull(draft);

        Assert.True((await svc.MoveAsync(draft!.DraftId, new MoveRequest(1, 1, "Op", null))).Ok);
        Assert.True((await svc.MoveAsync(draft.DraftId, new MoveRequest(2, 2, "Op", null))).Ok);

        var hist = await svc.ListHistoryAsync(draft.DraftId);
        var older = hist.OrderBy(h => h.HistoryId).First();

        // Undo older move by id (contract 1) while newer exists — after_state for older should still match
        // if contract 1 wasn't touched by later ops. Contract 1 only in first move → OK.
        var undo = await svc.UndoAsync(draft.DraftId, new UndoRequest(older.HistoryId, "Op", null));
        Assert.True(undo.Ok);

        var again = await svc.UndoAsync(draft.DraftId, new UndoRequest(older.HistoryId, "Op", null));
        Assert.False(again.Ok);
        Assert.Contains(again.Issues, i => i.Code == ValidationIssue.Codes.HistoryAlreadyUndone);
    }

    [Fact]
    public async Task Swap_History_Undo_RestoresBoth()
    {
        if (!CanConnect()) return;

        var svc = CreateService();
        var (draft, _) = await svc.CreateDraftAsync(2, new CreateDraftRequest(
            $"Hist3 {Guid.NewGuid():N}"[..18], null, false, null));
        Assert.NotNull(draft);

        Assert.True((await svc.MoveAsync(draft!.DraftId, new MoveRequest(1, 1, "Op", null))).Ok);
        Assert.True((await svc.MoveAsync(draft.DraftId, new MoveRequest(2, 2, "Op", null))).Ok);
        var swap = await svc.SwapAsync(draft.DraftId, new SwapRequest(1, 2, "Op", null));
        Assert.True(swap.Ok);

        var undo = await svc.UndoAsync(draft.DraftId, new UndoRequest(null, "Op", null));
        Assert.True(undo.Ok);

        var ws = await svc.GetWorkspaceAsync(draft.DraftId);
        Assert.Contains(ws!.Apartments.First(a => a.ApartmentId == 1).Occupants, o => o.ContractId == 1);
        Assert.Contains(ws.Apartments.First(a => a.ApartmentId == 2).Occupants, o => o.ContractId == 2);
    }

    [Fact]
    public async Task Lock_History_And_NothingToUndo_WhenEmpty()
    {
        if (!CanConnect()) return;

        var svc = CreateService();
        var (draft, _) = await svc.CreateDraftAsync(2, new CreateDraftRequest(
            $"Hist4 {Guid.NewGuid():N}"[..18], null, false, null));
        Assert.NotNull(draft);

        var empty = await svc.UndoAsync(draft!.DraftId, new UndoRequest(null, "Op", null));
        Assert.False(empty.Ok);
        Assert.Contains(empty.Issues, i => i.Code == ValidationIssue.Codes.NothingToUndo);

        Assert.True((await svc.MoveAsync(draft.DraftId, new MoveRequest(1, 1, "Op", null))).Ok);
        Assert.True((await svc.SetLockAsync(draft.DraftId, new LockRequest(1, true, "Op", null))).Ok);

        var undoLock = await svc.UndoAsync(draft.DraftId, new UndoRequest(null, "Op", null));
        Assert.True(undoLock.Ok);

        var ws = await svc.GetWorkspaceAsync(draft.DraftId);
        var occ = ws!.Apartments.SelectMany(a => a.Occupants).First(o => o.ContractId == 1);
        Assert.False(occ.IsLocked);
    }

    [Fact]
    public async Task Undo_Rejects_WhenStateConflict()
    {
        if (!CanConnect()) return;

        var svc = CreateService();
        var (draft, _) = await svc.CreateDraftAsync(2, new CreateDraftRequest(
            $"Hist5 {Guid.NewGuid():N}"[..18], null, false, null));
        Assert.NotNull(draft);

        var move = await svc.MoveAsync(draft!.DraftId, new MoveRequest(1, 1, "Op", null));
        Assert.True(move.Ok);

        // Further change to same contract so after_state of first move no longer matches.
        Assert.True((await svc.MoveAsync(draft.DraftId, new MoveRequest(1, 2, "Op", null))).Ok);

        var conflict = await svc.UndoAsync(draft.DraftId,
            new UndoRequest(move.HistoryId, "Op", null));
        Assert.False(conflict.Ok);
        Assert.Contains(conflict.Issues, i => i.Code == ValidationIssue.Codes.HistoryStateConflict);
    }
}
