using Lodge.Core.Models;
using Lodge.Core.Validation;
using MySqlConnector;

namespace Lodge.Core.Services;

/// <summary>
/// Central assignment mutation + workspace load service.
/// All draft edits go through here so a later milestone can add history/undo.
/// </summary>
public sealed class AssignmentService
{
    private readonly MySqlDataSource _db;
    private readonly AssignmentValidator _validator = new();

    public AssignmentService(MySqlDataSource db) => _db = db;

    public async Task<IReadOnlyList<TermInfo>> ListTermsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new MySqlCommand(
            "SELECT term_id, code, name, start_date, end_date FROM terms ORDER BY start_date", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<TermInfo>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new TermInfo(
                r.GetUInt32("term_id"),
                r.GetString("code"),
                r.GetString("name"),
                DateOnly.FromDateTime(r.GetDateTime("start_date")),
                DateOnly.FromDateTime(r.GetDateTime("end_date"))));
        }
        return list;
    }

    public async Task<IReadOnlyList<DraftInfo>> ListDraftsAsync(uint termId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new MySqlCommand(
            """
            SELECT draft_id, term_id, name, source, status, notes, created_at, updated_at
            FROM assignment_drafts WHERE term_id = @termId ORDER BY draft_id
            """, conn);
        cmd.Parameters.AddWithValue("@termId", termId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<DraftInfo>();
        while (await r.ReadAsync(ct))
            list.Add(ReadDraft(r));
        return list;
    }

    public async Task<(DraftInfo? Draft, IReadOnlyList<ValidationIssueDto> Issues)> CreateDraftAsync(
        uint termId, CreateDraftRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return (null, [Err("DRAFT_NAME_REQUIRED", "Draft name is required.")]);
        }

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var termCmd = new MySqlCommand("SELECT COUNT(*) FROM terms WHERE term_id = @id", conn, tx))
            {
                termCmd.Parameters.AddWithValue("@id", termId);
                if (Convert.ToInt64(await termCmd.ExecuteScalarAsync(ct)) == 0)
                {
                    await tx.RollbackAsync(ct);
                    return (null, [Err("TERM_NOT_FOUND", $"Term {termId} was not found.")]);
                }
            }

            if (request.CopyFromDraftId is uint sourceId)
            {
                await using var srcCmd = new MySqlCommand(
                    "SELECT draft_id, term_id, status FROM assignment_drafts WHERE draft_id = @id FOR UPDATE",
                    conn, tx);
                srcCmd.Parameters.AddWithValue("@id", sourceId);
                await using var srcR = await srcCmd.ExecuteReaderAsync(ct);
                if (!await srcR.ReadAsync(ct))
                {
                    await srcR.DisposeAsync();
                    await tx.RollbackAsync(ct);
                    return (null, [Err(ValidationIssue.Codes.DraftNotFound, $"Source draft {sourceId} was not found.")]);
                }
                var srcTerm = srcR.GetUInt32("term_id");
                await srcR.DisposeAsync();
                if (srcTerm != termId)
                {
                    await tx.RollbackAsync(ct);
                    return (null, [Err(ValidationIssue.Codes.ContractTermMismatch,
                        $"Source draft {sourceId} belongs to term {srcTerm}, not {termId}.")]);
                }
            }

            uint newDraftId;
            await using (var ins = new MySqlCommand(
                             """
                             INSERT INTO assignment_drafts (term_id, name, source, status, notes)
                             VALUES (@termId, @name, 'manual', 'draft', @notes)
                             """, conn, tx))
            {
                ins.Parameters.AddWithValue("@termId", termId);
                ins.Parameters.AddWithValue("@name", request.Name.Trim());
                ins.Parameters.AddWithValue("@notes", (object?)request.Notes ?? DBNull.Value);
                await ins.ExecuteNonQueryAsync(ct);
            }

            await using (var idCmd = new MySqlCommand("SELECT LAST_INSERT_ID()", conn, tx))
            {
                newDraftId = Convert.ToUInt32(await idCmd.ExecuteScalarAsync(ct));
            }

            if (request.CopyFromDraftId is uint copyFrom)
            {
                await using var copy = new MySqlCommand(
                    """
                    INSERT INTO assignments
                      (draft_id, term_id, contract_id, apartment_id, is_locked, locked_by, locked_at, source, explanation)
                    SELECT @newDraft, term_id, contract_id, apartment_id,
                           CASE WHEN @copyLocks THEN is_locked ELSE FALSE END,
                           CASE WHEN @copyLocks AND is_locked THEN locked_by ELSE NULL END,
                           CASE WHEN @copyLocks AND is_locked THEN locked_at ELSE NULL END,
                           'carried_over',
                           explanation
                    FROM assignments WHERE draft_id = @source
                    """, conn, tx);
                copy.Parameters.AddWithValue("@newDraft", newDraftId);
                copy.Parameters.AddWithValue("@source", copyFrom);
                copy.Parameters.AddWithValue("@copyLocks", request.CopyLocks);
                await copy.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            var drafts = await ListDraftsAsync(termId, ct);
            var created = drafts.First(d => d.DraftId == newDraftId);
            return (created, Array.Empty<ValidationIssueDto>());
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<WorkspacePayload?> GetWorkspaceAsync(uint draftId, CancellationToken ct = default)
    {
        var state = await LoadStateAsync(draftId, ct);
        if (state is null) return null;

        await using var conn = await _db.OpenConnectionAsync(ct);

        TermInfo term;
        await using (var cmd = new MySqlCommand(
                         "SELECT term_id, code, name, start_date, end_date FROM terms WHERE term_id = @id", conn))
        {
            cmd.Parameters.AddWithValue("@id", state.TermId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            term = new TermInfo(
                r.GetUInt32("term_id"), r.GetString("code"), r.GetString("name"),
                DateOnly.FromDateTime(r.GetDateTime("start_date")),
                DateOnly.FromDateTime(r.GetDateTime("end_date")));
        }

        DraftInfo draft;
        await using (var cmd = new MySqlCommand(
                         """
                         SELECT draft_id, term_id, name, source, status, notes, created_at, updated_at
                         FROM assignment_drafts WHERE draft_id = @id
                         """, conn))
        {
            cmd.Parameters.AddWithValue("@id", draftId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            draft = ReadDraft(r);
        }

        var apartments = state.Apartments.Values
            .Where(a => a.IsActive)
            .OrderBy(a => a.ApartmentNumber)
            .Select(a =>
            {
                var occupants = state.OccupantsOf(a.ApartmentId)
                    .Select(asg =>
                    {
                        var c = state.Contracts[asg.ContractId];
                        return new AssignedTenantInfo(
                            c.ContractId, c.TenantId, c.TenantCode, c.FirstName, c.LastName,
                            asg.AssignmentId, asg.IsLocked, asg.LockedBy, asg.LockedAt, asg.Source);
                    })
                    .OrderBy(t => t.LastName).ThenBy(t => t.FirstName).ThenBy(t => t.TenantCode)
                    .ToList();
                return new ApartmentWorkspaceRow(
                    a.ApartmentId, a.ApartmentNumber, a.FloorPlanCode, a.FloorPlanId,
                    a.Building, a.Floor, a.EffectiveCapacity, a.IsActive, occupants);
            })
            .ToList();

        var assignedIds = state.AssignmentsByContract.Keys.ToHashSet();
        var unassigned = state.Contracts.Values
            .Where(c => !assignedIds.Contains(c.ContractId))
            .OrderBy(c => c.LastName).ThenBy(c => c.FirstName).ThenBy(c => c.TenantCode)
            .Select(c => new UnassignedTenantInfo(
                c.ContractId, c.TenantId, c.TenantCode, c.FirstName, c.LastName,
                c.FloorPlanCode, c.FloorPlanId, c.TenantType))
            .ToList();

        var requests = state.RoommateRequests
            .OrderBy(r => r.TenantCode).ThenBy(r => r.RequestId)
            .Select(r => new RoommateRequestInfo(
                r.RequestId, r.ContractId, r.TenantCode, r.RequesterName,
                r.RawText, r.Status, r.ExclusionReason))
            .ToList();

        var restrictions = state.Restrictions
            .Select(r => new RestrictionInfo(
                r.RestrictionId, r.ContractId, r.RestrictionType,
                r.ApartmentId, r.OtherTenantId, r.Value, r.Description))
            .ToList();

        var issues = _validator.ValidateDraft(state).Select(ToDto).ToList();

        return new WorkspacePayload(
            draft, term, apartments, unassigned, requests, restrictions, issues,
            AssignmentValidator.ValidationLimitations);
    }

    public async Task<MutationResult> MoveAsync(uint draftId, MoveRequest request, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var state = await LoadStateAsync(draftId, conn, tx, ct);
            if (state is null)
            {
                await tx.RollbackAsync(ct);
                return Fail(ValidationIssue.Codes.DraftNotFound, $"Draft {draftId} was not found.");
            }

            var issues = _validator.ValidateMove(state, request.ContractId, request.ApartmentId);
            if (issues.Any(i => i.IsBlocking))
            {
                await tx.RollbackAsync(ct);
                return new MutationResult(false, issues.Select(ToDto).ToList());
            }

            var before = AssignmentHistory.FromState(state, request.ContractId);
            state.AssignmentsByContract.TryGetValue(request.ContractId, out var existing);

            if (request.ApartmentId is null)
            {
                await using var del = new MySqlCommand(
                    "DELETE FROM assignments WHERE draft_id = @draft AND contract_id = @contract", conn, tx);
                del.Parameters.AddWithValue("@draft", draftId);
                del.Parameters.AddWithValue("@contract", request.ContractId);
                await del.ExecuteNonQueryAsync(ct);
            }
            else if (existing is null)
            {
                var source = string.IsNullOrWhiteSpace(request.Source) ? "manual" : request.Source.Trim();
                await using var ins = new MySqlCommand(
                    """
                    INSERT INTO assignments
                      (draft_id, term_id, contract_id, apartment_id, is_locked, locked_by, locked_at, source)
                    VALUES (@draft, @term, @contract, @apt, FALSE, NULL, NULL, @source)
                    """, conn, tx);
                ins.Parameters.AddWithValue("@draft", draftId);
                ins.Parameters.AddWithValue("@term", state.TermId);
                ins.Parameters.AddWithValue("@contract", request.ContractId);
                ins.Parameters.AddWithValue("@apt", request.ApartmentId.Value);
                ins.Parameters.AddWithValue("@source", source);
                await ins.ExecuteNonQueryAsync(ct);
            }
            else
            {
                var source = string.IsNullOrWhiteSpace(request.Source) ? "manual" : request.Source.Trim();
                await using var upd = new MySqlCommand(
                    """
                    UPDATE assignments
                    SET apartment_id = @apt, source = @source, updated_at = CURRENT_TIMESTAMP
                    WHERE draft_id = @draft AND contract_id = @contract
                    """, conn, tx);
                upd.Parameters.AddWithValue("@apt", request.ApartmentId.Value);
                upd.Parameters.AddWithValue("@source", source);
                upd.Parameters.AddWithValue("@draft", draftId);
                upd.Parameters.AddWithValue("@contract", request.ContractId);
                await upd.ExecuteNonQueryAsync(ct);
            }

            // Reload snapshot for after_state (apartment number / lock fields).
            var afterState = await LoadStateAsync(draftId, conn, tx, ct);
            var after = AssignmentHistory.FromState(afterState!, request.ContractId);
            var changeType = request.ApartmentId is null ? "unassign" : "move";
            var historyId = await InsertHistoryAsync(
                conn, tx, draftId, state.TermId, changeType,
                AssignmentHistory.OperatorOrDefault(request.PerformedBy), request.Reason,
                AssignmentHistory.Serialize([before]), AssignmentHistory.Serialize([after]),
                undoesHistoryId: null, ct);

            await TouchDraftAsync(conn, tx, draftId, ct);
            await tx.CommitAsync(ct);
            return new MutationResult(true, issues.Where(i => !i.IsBlocking).Select(ToDto).ToList(), historyId);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<MutationResult> SwapAsync(uint draftId, SwapRequest request, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var state = await LoadStateAsync(draftId, conn, tx, ct);
            if (state is null)
            {
                await tx.RollbackAsync(ct);
                return Fail(ValidationIssue.Codes.DraftNotFound, $"Draft {draftId} was not found.");
            }

            var issues = _validator.ValidateSwap(state, request.ContractIdA, request.ContractIdB);
            if (issues.Any(i => i.IsBlocking))
            {
                await tx.RollbackAsync(ct);
                return new MutationResult(false, issues.Select(ToDto).ToList());
            }

            var a = state.AssignmentsByContract[request.ContractIdA];
            var b = state.AssignmentsByContract[request.ContractIdB];
            if (a.ApartmentId == b.ApartmentId)
            {
                await tx.CommitAsync(ct);
                return new MutationResult(true, Array.Empty<ValidationIssueDto>());
            }

            var beforeA = AssignmentHistory.FromState(state, request.ContractIdA);
            var beforeB = AssignmentHistory.FromState(state, request.ContractIdB);

            await using (var updA = new MySqlCommand(
                             """
                             UPDATE assignments SET apartment_id = @apt, source = 'manual', updated_at = CURRENT_TIMESTAMP
                             WHERE draft_id = @draft AND contract_id = @contract
                             """, conn, tx))
            {
                updA.Parameters.AddWithValue("@apt", b.ApartmentId);
                updA.Parameters.AddWithValue("@draft", draftId);
                updA.Parameters.AddWithValue("@contract", request.ContractIdA);
                await updA.ExecuteNonQueryAsync(ct);
            }

            await using (var updB = new MySqlCommand(
                             """
                             UPDATE assignments SET apartment_id = @apt, source = 'manual', updated_at = CURRENT_TIMESTAMP
                             WHERE draft_id = @draft AND contract_id = @contract
                             """, conn, tx))
            {
                updB.Parameters.AddWithValue("@apt", a.ApartmentId);
                updB.Parameters.AddWithValue("@draft", draftId);
                updB.Parameters.AddWithValue("@contract", request.ContractIdB);
                await updB.ExecuteNonQueryAsync(ct);
            }

            var afterState = await LoadStateAsync(draftId, conn, tx, ct);
            var afterA = AssignmentHistory.FromState(afterState!, request.ContractIdA);
            var afterB = AssignmentHistory.FromState(afterState!, request.ContractIdB);
            var historyId = await InsertHistoryAsync(
                conn, tx, draftId, state.TermId, "swap",
                AssignmentHistory.OperatorOrDefault(request.PerformedBy), request.Reason,
                AssignmentHistory.Serialize([beforeA, beforeB]),
                AssignmentHistory.Serialize([afterA, afterB]),
                undoesHistoryId: null, ct);

            await TouchDraftAsync(conn, tx, draftId, ct);
            await tx.CommitAsync(ct);
            return new MutationResult(true, issues.Where(i => !i.IsBlocking).Select(ToDto).ToList(), historyId);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<MutationResult> SetLockAsync(uint draftId, LockRequest request, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var state = await LoadStateAsync(draftId, conn, tx, ct);
            if (state is null)
            {
                await tx.RollbackAsync(ct);
                return Fail(ValidationIssue.Codes.DraftNotFound, $"Draft {draftId} was not found.");
            }

            var issues = _validator.ValidateLock(state, request.ContractId, request.Locked, request.LockedBy);
            if (issues.Any(i => i.IsBlocking))
            {
                await tx.RollbackAsync(ct);
                return new MutationResult(false, issues.Select(ToDto).ToList());
            }

            var before = AssignmentHistory.FromState(state, request.ContractId);

            if (request.Locked)
            {
                await using var upd = new MySqlCommand(
                    """
                    UPDATE assignments
                    SET is_locked = TRUE, locked_by = @by, locked_at = UTC_TIMESTAMP()
                    WHERE draft_id = @draft AND contract_id = @contract
                    """, conn, tx);
                upd.Parameters.AddWithValue("@by", request.LockedBy.Trim());
                upd.Parameters.AddWithValue("@draft", draftId);
                upd.Parameters.AddWithValue("@contract", request.ContractId);
                await upd.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var upd = new MySqlCommand(
                    """
                    UPDATE assignments
                    SET is_locked = FALSE, locked_by = NULL, locked_at = NULL
                    WHERE draft_id = @draft AND contract_id = @contract
                    """, conn, tx);
                upd.Parameters.AddWithValue("@draft", draftId);
                upd.Parameters.AddWithValue("@contract", request.ContractId);
                await upd.ExecuteNonQueryAsync(ct);
            }

            var afterState = await LoadStateAsync(draftId, conn, tx, ct);
            var after = AssignmentHistory.FromState(afterState!, request.ContractId);
            var historyId = await InsertHistoryAsync(
                conn, tx, draftId, state.TermId, request.Locked ? "lock" : "unlock",
                AssignmentHistory.OperatorOrDefault(request.LockedBy), request.Reason,
                AssignmentHistory.Serialize([before]), AssignmentHistory.Serialize([after]),
                undoesHistoryId: null, ct);

            await TouchDraftAsync(conn, tx, draftId, ct);
            await tx.CommitAsync(ct);
            return new MutationResult(true, Array.Empty<ValidationIssueDto>(), historyId);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<HistoryListItem>> ListHistoryAsync(
        uint draftId, int limit = 50, CancellationToken ct = default)
    {
        if (limit < 1) limit = 1;
        if (limit > 200) limit = 200;

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new MySqlCommand(
            """
            SELECT history_id, draft_id, term_id, change_type, performed_by, reason,
                   before_state, after_state, undoes_history_id, undone_by_history_id, created_at
            FROM assignment_change_history
            WHERE draft_id = @draft
            ORDER BY history_id DESC
            LIMIT @limit
            """, conn);
        cmd.Parameters.AddWithValue("@draft", draftId);
        cmd.Parameters.AddWithValue("@limit", limit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<HistoryListItem>();
        while (await r.ReadAsync(ct))
            list.Add(ReadHistoryItem(r));
        return list;
    }

    public async Task<MutationResult> UndoAsync(uint draftId, UndoRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.PerformedBy))
            return Fail(ValidationIssue.Codes.UndoOperatorRequired,
                "Undo requires an explicit fictional operator identity.");

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var state = await LoadStateAsync(draftId, conn, tx, ct);
            if (state is null)
            {
                await tx.RollbackAsync(ct);
                return Fail(ValidationIssue.Codes.DraftNotFound, $"Draft {draftId} was not found.");
            }

            if (!state.IsEditable)
            {
                await tx.RollbackAsync(ct);
                return Fail(ValidationIssue.Codes.DraftNotEditable,
                    $"Draft {draftId} has status '{state.Status}' and is not editable.");
            }

            ulong targetId = 0;
            string changeType = "";
            string beforeJson = "";
            string afterJson = "";
            ulong? undoesId = null;
            ulong? undoneBy = null;

            if (request.HistoryId is ulong explicitId)
            {
                await using var cmd = new MySqlCommand(
                    """
                    SELECT history_id, change_type, before_state, after_state,
                           undoes_history_id, undone_by_history_id
                    FROM assignment_change_history
                    WHERE draft_id = @draft AND history_id = @id
                    FOR UPDATE
                    """, conn, tx);
                cmd.Parameters.AddWithValue("@draft", draftId);
                cmd.Parameters.AddWithValue("@id", explicitId);
                var found = false;
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    if (await r.ReadAsync(ct))
                    {
                        found = true;
                        targetId = r.GetUInt64("history_id");
                        changeType = r.GetString("change_type");
                        beforeJson = r.GetString("before_state");
                        afterJson = r.GetString("after_state");
                        undoesId = r.IsDBNull(r.GetOrdinal("undoes_history_id")) ? null : r.GetUInt64("undoes_history_id");
                        undoneBy = r.IsDBNull(r.GetOrdinal("undone_by_history_id")) ? null : r.GetUInt64("undone_by_history_id");
                    }
                }
                if (!found)
                {
                    await tx.RollbackAsync(ct);
                    return Fail(ValidationIssue.Codes.HistoryNotFound,
                        $"History {explicitId} was not found for draft {draftId}.");
                }
            }
            else
            {
                await using var cmd = new MySqlCommand(
                    """
                    SELECT history_id, change_type, before_state, after_state,
                           undoes_history_id, undone_by_history_id
                    FROM assignment_change_history
                    WHERE draft_id = @draft
                      AND undone_by_history_id IS NULL
                      AND change_type <> 'undo'
                    ORDER BY history_id DESC
                    LIMIT 1
                    FOR UPDATE
                    """, conn, tx);
                cmd.Parameters.AddWithValue("@draft", draftId);
                var found = false;
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    if (await r.ReadAsync(ct))
                    {
                        found = true;
                        targetId = r.GetUInt64("history_id");
                        changeType = r.GetString("change_type");
                        beforeJson = r.GetString("before_state");
                        afterJson = r.GetString("after_state");
                        undoesId = r.IsDBNull(r.GetOrdinal("undoes_history_id")) ? null : r.GetUInt64("undoes_history_id");
                        undoneBy = r.IsDBNull(r.GetOrdinal("undone_by_history_id")) ? null : r.GetUInt64("undone_by_history_id");
                    }
                }
                if (!found)
                {
                    await tx.RollbackAsync(ct);
                    return Fail(ValidationIssue.Codes.NothingToUndo,
                        $"Draft {draftId} has no undoable change.");
                }
            }

            if (changeType == "undo" || undoesId is not null)
            {
                await tx.RollbackAsync(ct);
                return Fail(ValidationIssue.Codes.HistoryNotUndoable,
                    $"History {targetId} is an undo record and cannot be undone again this way.");
            }

            if (undoneBy is not null)
            {
                await tx.RollbackAsync(ct);
                return Fail(ValidationIssue.Codes.HistoryAlreadyUndone,
                    $"History {targetId} was already undone by history {undoneBy}.");
            }

            var expectedAfter = AssignmentHistory.Deserialize(afterJson);
            if (!AssignmentHistory.MatchesLive(state, expectedAfter))
            {
                await tx.RollbackAsync(ct);
                return Fail(ValidationIssue.Codes.HistoryStateConflict,
                    $"Draft state no longer matches history {targetId} after-state; undo refused.");
            }

            var restore = AssignmentHistory.Deserialize(beforeJson);
            await ApplySnapshotAsync(conn, tx, draftId, state.TermId, restore, ct);

            var restored = await LoadStateAsync(draftId, conn, tx, ct);
            var draftIssues = _validator.ValidateDraft(restored!)
                .Where(i => i.IsBlocking && i.Code != ValidationIssue.Codes.UnresolvedRoommateRequest)
                .ToList();
            // Capacity / plan / restriction errors block undo; info roommate noise is fine.
            var blocking = draftIssues
                .Where(i => i.Severity == ValidationIssue.Severities.Error)
                .Where(i => i.Code is ValidationIssue.Codes.CapacityExceeded
                    or ValidationIssue.Codes.FloorPlanMismatch
                    or ValidationIssue.Codes.ApartmentInactive
                    or ValidationIssue.Codes.RestrictionRequiredApartment
                    or ValidationIssue.Codes.RestrictionExcludedApartment
                    or ValidationIssue.Codes.RestrictionRequiredFloor
                    or ValidationIssue.Codes.RestrictionExcludedFloor
                    or ValidationIssue.Codes.RestrictionMustNotShare)
                .ToList();
            if (blocking.Count > 0)
            {
                await tx.RollbackAsync(ct);
                return new MutationResult(false, blocking.Select(ToDto).ToList());
            }

            var liveAfterUndo = restore.Entries.Select(e => AssignmentHistory.FromState(restored!, e.ContractId)).ToList();
            var undoHistoryId = await InsertHistoryAsync(
                conn, tx, draftId, state.TermId, "undo",
                AssignmentHistory.OperatorOrDefault(request.PerformedBy), request.Reason,
                afterJson, // before undo = the change's after
                AssignmentHistory.Serialize(liveAfterUndo),
                undoesHistoryId: targetId, ct);

            await using (var mark = new MySqlCommand(
                             """
                             UPDATE assignment_change_history
                             SET undone_by_history_id = @undoId
                             WHERE history_id = @id AND undone_by_history_id IS NULL
                             """, conn, tx))
            {
                mark.Parameters.AddWithValue("@undoId", undoHistoryId);
                mark.Parameters.AddWithValue("@id", targetId);
                var n = await mark.ExecuteNonQueryAsync(ct);
                if (n != 1)
                {
                    await tx.RollbackAsync(ct);
                    return Fail(ValidationIssue.Codes.HistoryAlreadyUndone,
                        $"History {targetId} could not be marked undone (concurrent undo?).");
                }
            }

            await TouchDraftAsync(conn, tx, draftId, ct);
            await tx.CommitAsync(ct);
            return new MutationResult(true, Array.Empty<ValidationIssueDto>(), undoHistoryId);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<DraftState?> LoadStateAsync(uint draftId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await LoadStateAsync(draftId, conn, null, ct);
    }

    private async Task<DraftState?> LoadStateAsync(
        uint draftId, MySqlConnection conn, MySqlTransaction? tx, CancellationToken ct)
    {
        uint termId;
        string status, name;
        await using (var cmd = new MySqlCommand(
                         "SELECT draft_id, term_id, name, status FROM assignment_drafts WHERE draft_id = @id",
                         conn, tx))
        {
            cmd.Parameters.AddWithValue("@id", draftId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            termId = r.GetUInt32("term_id");
            status = r.GetString("status");
            name = r.GetString("name");
        }

        var state = new DraftState
        {
            DraftId = draftId,
            TermId = termId,
            Status = status,
            DraftName = name,
        };

        await using (var cmd = new MySqlCommand(
                         """
                         SELECT c.contract_id, c.tenant_id, t.tenant_code, t.first_name, t.last_name,
                                c.term_id, c.floor_plan_id, fp.code AS floor_plan_code, c.tenant_type
                         FROM contracts c
                         JOIN tenants t ON t.tenant_id = c.tenant_id
                         JOIN floor_plans fp ON fp.floor_plan_id = c.floor_plan_id
                         WHERE c.term_id = @term
                         """, conn, tx))
        {
            cmd.Parameters.AddWithValue("@term", termId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var c = new ContractState(
                    r.GetUInt32("contract_id"),
                    r.GetUInt32("tenant_id"),
                    r.GetString("tenant_code"),
                    r.GetString("first_name"),
                    r.GetString("last_name"),
                    r.GetUInt32("term_id"),
                    r.GetUInt32("floor_plan_id"),
                    r.GetString("floor_plan_code"),
                    r.GetString("tenant_type"));
                state.Contracts[c.ContractId] = c;
            }
        }

        await using (var cmd = new MySqlCommand(
                         """
                         SELECT a.apartment_id, a.apartment_number, a.floor_plan_id, fp.code AS floor_plan_code,
                                a.building, a.floor, a.is_active,
                                COALESCE(a.capacity_override, fp.resident_capacity) AS effective_capacity
                         FROM apartments a
                         JOIN floor_plans fp ON fp.floor_plan_id = a.floor_plan_id
                         """, conn, tx))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var a = new ApartmentSnapshot(
                    r.GetUInt32("apartment_id"),
                    r.GetString("apartment_number"),
                    r.GetUInt32("floor_plan_id"),
                    r.GetString("floor_plan_code"),
                    r.GetString("building"),
                    r.GetInt32("floor"),
                    r.GetInt32("effective_capacity"),
                    r.GetBoolean("is_active"));
                state.Apartments[a.ApartmentId] = a;
            }
        }

        await using (var cmd = new MySqlCommand(
                         """
                         SELECT assignment_id, contract_id, apartment_id, is_locked, locked_by, locked_at, source
                         FROM assignments WHERE draft_id = @draft
                         """, conn, tx))
        {
            cmd.Parameters.AddWithValue("@draft", draftId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var asg = new AssignmentState(
                    r.GetUInt32("assignment_id"),
                    r.GetUInt32("contract_id"),
                    r.GetUInt32("apartment_id"),
                    r.GetBoolean("is_locked"),
                    r.IsDBNull(r.GetOrdinal("locked_by")) ? null : r.GetString("locked_by"),
                    r.IsDBNull(r.GetOrdinal("locked_at")) ? null : r.GetDateTime("locked_at"),
                    r.GetString("source"));
                state.AssignmentsByContract[asg.ContractId] = asg;
            }
        }

        await using (var cmd = new MySqlCommand(
                         """
                         SELECT pr.restriction_id, pr.contract_id, pr.restriction_type, pr.apartment_id,
                                pr.other_tenant_id, pr.value, pr.description
                         FROM placement_restrictions pr
                         JOIN contracts c ON c.contract_id = pr.contract_id
                         WHERE c.term_id = @term
                         """, conn, tx))
        {
            cmd.Parameters.AddWithValue("@term", termId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                state.Restrictions.Add(new RestrictionState(
                    r.GetUInt32("restriction_id"),
                    r.GetUInt32("contract_id"),
                    r.GetString("restriction_type"),
                    r.IsDBNull(r.GetOrdinal("apartment_id")) ? null : r.GetUInt32("apartment_id"),
                    r.IsDBNull(r.GetOrdinal("other_tenant_id")) ? null : r.GetUInt32("other_tenant_id"),
                    r.IsDBNull(r.GetOrdinal("value")) ? null : r.GetString("value"),
                    r.GetString("description")));
            }
        }

        await using (var cmd = new MySqlCommand(
                         """
                         SELECT rr.request_id, rr.contract_id, t.tenant_code,
                                CONCAT(t.first_name, ' ', t.last_name) AS requester_name,
                                rr.raw_text, rr.status, rr.exclusion_reason, rr.resolved_tenant_id
                         FROM roommate_requests rr
                         JOIN contracts c ON c.contract_id = rr.contract_id
                         JOIN tenants t ON t.tenant_id = c.tenant_id
                         WHERE c.term_id = @term
                         """, conn, tx))
        {
            cmd.Parameters.AddWithValue("@term", termId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                state.RoommateRequests.Add(new RoommateRequestState(
                    r.GetUInt32("request_id"),
                    r.GetUInt32("contract_id"),
                    r.GetString("tenant_code"),
                    r.GetString("requester_name"),
                    r.GetString("raw_text"),
                    r.GetString("status"),
                    r.IsDBNull(r.GetOrdinal("exclusion_reason")) ? null : r.GetString("exclusion_reason"),
                    r.IsDBNull(r.GetOrdinal("resolved_tenant_id")) ? null : r.GetUInt32("resolved_tenant_id")));
            }
        }

        await using (var cmd = new MySqlCommand(
                         """
                         SELECT p.contract_id, p.has_animal, p.animal_allergy_restriction
                         FROM preferences p
                         JOIN contracts c ON c.contract_id = p.contract_id
                         WHERE c.term_id = @term
                         """, conn, tx))
        {
            cmd.Parameters.AddWithValue("@term", termId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                state.Preferences.Add(new PreferenceState(
                    r.GetUInt32("contract_id"),
                    r.GetBoolean("has_animal"),
                    r.GetBoolean("animal_allergy_restriction")));
            }
        }

        return state;
    }

    private static async Task TouchDraftAsync(
        MySqlConnection conn, MySqlTransaction tx, uint draftId, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(
            "UPDATE assignment_drafts SET updated_at = CURRENT_TIMESTAMP WHERE draft_id = @id", conn, tx);
        cmd.Parameters.AddWithValue("@id", draftId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<ulong> InsertHistoryAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        uint draftId,
        uint termId,
        string changeType,
        string performedBy,
        string? reason,
        string beforeJson,
        string afterJson,
        ulong? undoesHistoryId,
        CancellationToken ct)
    {
        await using var ins = new MySqlCommand(
            """
            INSERT INTO assignment_change_history
              (draft_id, term_id, change_type, performed_by, reason, before_state, after_state, undoes_history_id)
            VALUES
              (@draft, @term, @type, @by, @reason, CAST(@before AS JSON), CAST(@after AS JSON), @undoes)
            """, conn, tx);
        ins.Parameters.AddWithValue("@draft", draftId);
        ins.Parameters.AddWithValue("@term", termId);
        ins.Parameters.AddWithValue("@type", changeType);
        ins.Parameters.AddWithValue("@by", performedBy);
        ins.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
        ins.Parameters.AddWithValue("@before", beforeJson);
        ins.Parameters.AddWithValue("@after", afterJson);
        ins.Parameters.AddWithValue("@undoes", (object?)undoesHistoryId ?? DBNull.Value);
        await ins.ExecuteNonQueryAsync(ct);

        await using var idCmd = new MySqlCommand("SELECT LAST_INSERT_ID()", conn, tx);
        return Convert.ToUInt64(await idCmd.ExecuteScalarAsync(ct));
    }

    private static async Task ApplySnapshotAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        uint draftId,
        uint termId,
        AssignmentSnapshotPayload snapshot,
        CancellationToken ct)
    {
        foreach (var entry in snapshot.Entries)
        {
            if (!entry.Assigned || entry.ApartmentId is null)
            {
                await using var del = new MySqlCommand(
                    "DELETE FROM assignments WHERE draft_id = @draft AND contract_id = @contract", conn, tx);
                del.Parameters.AddWithValue("@draft", draftId);
                del.Parameters.AddWithValue("@contract", entry.ContractId);
                await del.ExecuteNonQueryAsync(ct);
                continue;
            }

            await using var upsert = new MySqlCommand(
                """
                INSERT INTO assignments
                  (draft_id, term_id, contract_id, apartment_id, is_locked, locked_by, locked_at, source)
                VALUES
                  (@draft, @term, @contract, @apt, @locked, @lockedBy, @lockedAt, COALESCE(@source, 'manual'))
                ON DUPLICATE KEY UPDATE
                  apartment_id = VALUES(apartment_id),
                  is_locked = VALUES(is_locked),
                  locked_by = VALUES(locked_by),
                  locked_at = VALUES(locked_at),
                  source = VALUES(source),
                  updated_at = CURRENT_TIMESTAMP
                """, conn, tx);
            upsert.Parameters.AddWithValue("@draft", draftId);
            upsert.Parameters.AddWithValue("@term", termId);
            upsert.Parameters.AddWithValue("@contract", entry.ContractId);
            upsert.Parameters.AddWithValue("@apt", entry.ApartmentId.Value);
            upsert.Parameters.AddWithValue("@locked", entry.IsLocked);
            upsert.Parameters.AddWithValue("@lockedBy",
                entry.IsLocked ? (object?)entry.LockedBy ?? "Fictional Operator" : DBNull.Value);
            upsert.Parameters.AddWithValue("@lockedAt",
                entry.IsLocked ? (object?)(entry.LockedAt ?? DateTime.UtcNow) : DBNull.Value);
            upsert.Parameters.AddWithValue("@source", (object?)entry.Source ?? DBNull.Value);
            await upsert.ExecuteNonQueryAsync(ct);
        }
    }

    private static HistoryListItem ReadHistoryItem(MySqlDataReader r)
    {
        var before = AssignmentHistory.Deserialize(r.GetString("before_state"));
        var after = AssignmentHistory.Deserialize(r.GetString("after_state"));
        var changeType = r.GetString("change_type");
        ulong? undoes = r.IsDBNull(r.GetOrdinal("undoes_history_id")) ? null : r.GetUInt64("undoes_history_id");
        ulong? undoneBy = r.IsDBNull(r.GetOrdinal("undone_by_history_id")) ? null : r.GetUInt64("undone_by_history_id");
        var undoable = undoneBy is null && changeType != "undo" && undoes is null;
        return new HistoryListItem(
            r.GetUInt64("history_id"),
            r.GetUInt32("draft_id"),
            r.GetUInt32("term_id"),
            changeType,
            r.GetString("performed_by"),
            r.IsDBNull(r.GetOrdinal("reason")) ? null : r.GetString("reason"),
            before.Entries.Select(AssignmentHistory.ToDto).ToList(),
            after.Entries.Select(AssignmentHistory.ToDto).ToList(),
            undoes,
            undoneBy,
            undoable,
            r.GetDateTime("created_at"));
    }

    private static DraftInfo ReadDraft(MySqlDataReader r) => new(
        r.GetUInt32("draft_id"),
        r.GetUInt32("term_id"),
        r.GetString("name"),
        r.GetString("source"),
        r.GetString("status"),
        r.IsDBNull(r.GetOrdinal("notes")) ? null : r.GetString("notes"),
        r.GetDateTime("created_at"),
        r.GetDateTime("updated_at"));

    private static ValidationIssueDto ToDto(ValidationIssue i) => new(
        i.Code, i.Severity, i.Message, i.ContractId, i.TenantCode,
        i.ApartmentId, i.ApartmentNumber, i.RelatedContractId, i.RelatedTenantCode);

    private static MutationResult Fail(string code, string message) =>
        new(false, [new ValidationIssueDto(code, ValidationIssue.Severities.Error, message, null, null, null, null, null, null)]);

    private static ValidationIssueDto Err(string code, string message) =>
        new(code, ValidationIssue.Severities.Error, message, null, null, null, null, null, null);
}
