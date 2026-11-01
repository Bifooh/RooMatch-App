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
                await using var ins = new MySqlCommand(
                    """
                    INSERT INTO assignments
                      (draft_id, term_id, contract_id, apartment_id, is_locked, locked_by, locked_at, source)
                    VALUES (@draft, @term, @contract, @apt, FALSE, NULL, NULL, 'manual')
                    """, conn, tx);
                ins.Parameters.AddWithValue("@draft", draftId);
                ins.Parameters.AddWithValue("@term", state.TermId);
                ins.Parameters.AddWithValue("@contract", request.ContractId);
                ins.Parameters.AddWithValue("@apt", request.ApartmentId.Value);
                await ins.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var upd = new MySqlCommand(
                    """
                    UPDATE assignments
                    SET apartment_id = @apt, source = 'manual', updated_at = CURRENT_TIMESTAMP
                    WHERE draft_id = @draft AND contract_id = @contract
                    """, conn, tx);
                upd.Parameters.AddWithValue("@apt", request.ApartmentId.Value);
                upd.Parameters.AddWithValue("@draft", draftId);
                upd.Parameters.AddWithValue("@contract", request.ContractId);
                await upd.ExecuteNonQueryAsync(ct);
            }

            await TouchDraftAsync(conn, tx, draftId, ct);
            await tx.CommitAsync(ct);
            return new MutationResult(true, issues.Where(i => !i.IsBlocking).Select(ToDto).ToList());
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

            // Two-step update to avoid unique-key collisions on (draft, contract) — contracts stay;
            // we only swap apartment_id. No unique on apartment, so direct updates are fine.
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

            await TouchDraftAsync(conn, tx, draftId, ct);
            await tx.CommitAsync(ct);
            return new MutationResult(true, issues.Where(i => !i.IsBlocking).Select(ToDto).ToList());
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

            await TouchDraftAsync(conn, tx, draftId, ct);
            await tx.CommitAsync(ct);
            return new MutationResult(true, Array.Empty<ValidationIssueDto>());
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
                                rr.raw_text, rr.status, rr.exclusion_reason
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
                    r.IsDBNull(r.GetOrdinal("exclusion_reason")) ? null : r.GetString("exclusion_reason")));
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
