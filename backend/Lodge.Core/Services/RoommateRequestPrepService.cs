using Lodge.Core.Models;
using MySqlConnector;

namespace Lodge.Core.Services;

/// <summary>
/// Pre-matching roommate request prep: list conflicts and let managers resolve or set aside.
/// Does not run auto-matching. Never overwrites raw_text.
/// </summary>
public sealed class RoommateRequestPrepService
{
    private static readonly HashSet<string> AttentionStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "pending", "ambiguous", "unresolved" };

    private static readonly HashSet<string> MarkStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "unresolved", "ambiguous", "excluded" };

    private readonly MySqlDataSource _db;

    public RoommateRequestPrepService(MySqlDataSource db) => _db = db;

    public async Task<RoommatePrepPayload?> ListPrepAsync(uint termId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);

        string? termCode;
        await using (var termCmd = new MySqlCommand(
            "SELECT code FROM terms WHERE term_id = @termId", conn))
        {
            termCmd.Parameters.AddWithValue("@termId", termId);
            var codeObj = await termCmd.ExecuteScalarAsync(ct);
            if (codeObj is null || codeObj is DBNull) return null;
            termCode = Convert.ToString(codeObj)!;
        }

        var pool = await LoadTermTenantsAsync(conn, termId, ct);
        var requests = await LoadRequestsAsync(conn, termId, ct);

        var items = new List<RoommatePrepItem>();
        var pending = 0;
        var ambiguous = 0;
        var unresolved = 0;
        var excluded = 0;
        var exactReady = 0;
        var duplicates = 0;
        var missing = 0;
        var nearOnly = 0;

        foreach (var req in requests)
        {
            switch (req.Status.ToLowerInvariant())
            {
                case "pending": pending++; break;
                case "ambiguous": ambiguous++; break;
                case "unresolved": unresolved++; break;
                case "excluded": excluded++; break;
            }

            if (!AttentionStatuses.Contains(req.Status)) continue;

            var candidates = RoommateNameMatcher.FindCandidates(
                req.RawText, pool, excludeTenantId: req.RequesterTenantId);
            var (conflict, suggestion) = RoommateNameMatcher.Classify(candidates);

            switch (conflict)
            {
                case RoommateNameMatcher.ConflictExact: exactReady++; break;
                case RoommateNameMatcher.ConflictDuplicate: duplicates++; break;
                case RoommateNameMatcher.ConflictMissing: missing++; break;
                case RoommateNameMatcher.ConflictNearOnly: nearOnly++; break;
            }

            items.Add(ToItem(req, conflict, suggestion, candidates));
        }

        // Missing / duplicates first, then near, then exact-ready.
        items = items
            .OrderBy(i => i.ConflictKind switch
            {
                RoommateNameMatcher.ConflictMissing => 0,
                RoommateNameMatcher.ConflictDuplicate => 1,
                RoommateNameMatcher.ConflictNearOnly => 2,
                _ => 3,
            })
            .ThenBy(i => i.RequesterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.RequestId)
            .ToList();

        var summary = new RoommatePrepSummary(
            pending, ambiguous, unresolved, excluded,
            items.Count, exactReady, duplicates, missing, nearOnly);

        return new RoommatePrepPayload(termId, termCode, summary, items);
    }

    public async Task<RoommateMutationResult> ResolveAsync(
        uint termId, uint requestId, ResolveRoommateRequest body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(body.PerformedBy))
            return Fail("OPERATOR_REQUIRED", "Fictional operator identity is required.");

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var req = await LoadRequestForUpdateAsync(conn, tx, termId, requestId, ct);
            if (req is null)
            {
                await tx.RollbackAsync(ct);
                return Fail("REQUEST_NOT_FOUND", "Roommate request was not found for this term.");
            }

            var pool = await LoadTermTenantsAsync(conn, termId, ct, tx);
            var target = pool.FirstOrDefault(t => t.TenantId == body.ResolvedTenantId);
            if (target is null)
            {
                await tx.RollbackAsync(ct);
                return Fail("TENANT_NOT_IN_TERM",
                    "Resolved tenant must have a contract in this term. Do not invent identity.");
            }

            if (target.TenantId == req.RequesterTenantId)
            {
                await tx.RollbackAsync(ct);
                return Fail("CANNOT_RESOLVE_TO_SELF", "A request cannot resolve to the requester.");
            }

            await using (var upd = new MySqlCommand(
                """
                UPDATE roommate_requests
                SET resolved_tenant_id = @tenantId,
                    status = 'resolved',
                    exclusion_reason = NULL
                WHERE request_id = @id
                """, conn, tx))
            {
                upd.Parameters.AddWithValue("@tenantId", body.ResolvedTenantId);
                upd.Parameters.AddWithValue("@id", requestId);
                await upd.ExecuteNonQueryAsync(ct);
            }

            var note = string.IsNullOrWhiteSpace(body.Note)
                ? $"Resolved to {target.TenantCode} by {body.PerformedBy.Trim()}."
                : body.Note.Trim();
            await UpsertReviewQueueAsync(conn, tx, termId, req, "resolved", note, closeOpen: true, ct);

            await tx.CommitAsync(ct);
            var item = await ReloadItemAsync(termId, requestId, ct);
            return new RoommateMutationResult(true, Array.Empty<ValidationIssueDto>(), Item: item);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<RoommateMutationResult> MarkAsync(
        uint termId, uint requestId, MarkRoommateRequest body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(body.PerformedBy))
            return Fail("OPERATOR_REQUIRED", "Fictional operator identity is required.");

        var status = (body.Status ?? "").Trim().ToLowerInvariant();
        if (!MarkStatuses.Contains(status))
            return Fail("INVALID_STATUS", "Status must be unresolved, ambiguous, or excluded.");

        var reason = (body.ExclusionReason ?? "").Trim();
        if (reason.Length == 0)
            return Fail("REASON_REQUIRED", "exclusion_reason is required for unresolved, ambiguous, and excluded.");
        if (reason.Length > 500)
            return Fail("REASON_TOO_LONG", "exclusion_reason must be 500 characters or fewer.");

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var req = await LoadRequestForUpdateAsync(conn, tx, termId, requestId, ct);
            if (req is null)
            {
                await tx.RollbackAsync(ct);
                return Fail("REQUEST_NOT_FOUND", "Roommate request was not found for this term.");
            }

            await using (var upd = new MySqlCommand(
                """
                UPDATE roommate_requests
                SET resolved_tenant_id = NULL,
                    status = @status,
                    exclusion_reason = @reason
                WHERE request_id = @id
                """, conn, tx))
            {
                upd.Parameters.AddWithValue("@status", status);
                upd.Parameters.AddWithValue("@reason", reason);
                upd.Parameters.AddWithValue("@id", requestId);
                await upd.ExecuteNonQueryAsync(ct);
            }

            var itemType = status switch
            {
                "ambiguous" => "ambiguous_roommate_request",
                "unresolved" => "unresolved_roommate_request",
                _ => "other",
            };
            var note = string.IsNullOrWhiteSpace(body.Note)
                ? $"Marked {status} by {body.PerformedBy.Trim()}."
                : body.Note.Trim();
            await UpsertReviewQueueAsync(conn, tx, termId, req with { Status = status, ExclusionReason = reason },
                itemType, note, closeOpen: false, ct);

            await tx.CommitAsync(ct);
            var item = await ReloadItemAsync(termId, requestId, ct);
            return new RoommateMutationResult(true, Array.Empty<ValidationIssueDto>(), Item: item);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<RoommatePrepItem?> ReloadItemAsync(uint termId, uint requestId, CancellationToken ct)
    {
        var payload = await ListPrepAsync(termId, ct);
        var fromAttention = payload?.Items.FirstOrDefault(i => i.RequestId == requestId);
        if (fromAttention is not null) return fromAttention;

        // Resolved/excluded drop out of attention list — rebuild a row for the response.
        await using var conn = await _db.OpenConnectionAsync(ct);
        var all = await LoadRequestsAsync(conn, termId, ct);
        var req = all.FirstOrDefault(r => r.RequestId == requestId);
        if (req is null) return null;
        var pool = await LoadTermTenantsAsync(conn, termId, ct);
        var candidates = RoommateNameMatcher.FindCandidates(req.RawText, pool, req.RequesterTenantId);
        var (conflict, suggestion) = RoommateNameMatcher.Classify(candidates);
        return ToItem(req, conflict, suggestion, candidates);
    }

    private static RoommatePrepItem ToItem(
        RequestRow req,
        string conflict,
        string suggestion,
        IReadOnlyList<RoommateNameMatcher.Candidate> candidates) =>
        new(
            req.RequestId,
            req.ContractId,
            req.RequesterTenantId,
            req.RequesterTenantCode,
            req.RequesterName,
            req.RequesterFloorPlanCode,
            req.RawText,
            req.Status,
            req.ExclusionReason,
            conflict,
            suggestion,
            candidates.Select(c => new RoommateCandidate(
                c.Tenant.TenantId,
                c.Tenant.TenantCode,
                c.Tenant.FirstName,
                c.Tenant.LastName,
                c.Tenant.ContractId,
                c.Tenant.FloorPlanCode,
                c.MatchKind)).ToList());

    private static async Task<List<RoommateNameMatcher.TenantName>> LoadTermTenantsAsync(
        MySqlConnection conn, uint termId, CancellationToken ct, MySqlTransaction? tx = null)
    {
        await using var cmd = new MySqlCommand(
            """
            SELECT t.tenant_id, t.tenant_code, t.first_name, t.last_name,
                   c.contract_id, fp.code AS floor_plan_code
            FROM contracts c
            JOIN tenants t ON t.tenant_id = c.tenant_id
            JOIN floor_plans fp ON fp.floor_plan_id = c.floor_plan_id
            WHERE c.term_id = @termId
            ORDER BY t.last_name, t.first_name
            """, conn, tx);
        cmd.Parameters.AddWithValue("@termId", termId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<RoommateNameMatcher.TenantName>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new RoommateNameMatcher.TenantName(
                r.GetUInt32("tenant_id"),
                r.GetString("tenant_code"),
                r.GetString("first_name"),
                r.GetString("last_name"),
                r.GetUInt32("contract_id"),
                r.GetString("floor_plan_code")));
        }
        return list;
    }

    private static async Task<List<RequestRow>> LoadRequestsAsync(
        MySqlConnection conn, uint termId, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(
            """
            SELECT rr.request_id, rr.contract_id, rr.raw_text, rr.status, rr.exclusion_reason,
                   t.tenant_id AS requester_tenant_id, t.tenant_code, t.first_name, t.last_name,
                   fp.code AS floor_plan_code
            FROM roommate_requests rr
            JOIN contracts c ON c.contract_id = rr.contract_id
            JOIN tenants t ON t.tenant_id = c.tenant_id
            JOIN floor_plans fp ON fp.floor_plan_id = c.floor_plan_id
            WHERE c.term_id = @termId
            ORDER BY rr.request_id
            """, conn);
        cmd.Parameters.AddWithValue("@termId", termId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<RequestRow>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new RequestRow(
                r.GetUInt32("request_id"),
                r.GetUInt32("contract_id"),
                r.GetUInt32("requester_tenant_id"),
                r.GetString("tenant_code"),
                $"{r.GetString("first_name")} {r.GetString("last_name")}",
                r.GetString("floor_plan_code"),
                r.GetString("raw_text"),
                r.GetString("status"),
                r.IsDBNull(r.GetOrdinal("exclusion_reason")) ? null : r.GetString("exclusion_reason")));
        }
        return list;
    }

    private static async Task<RequestRow?> LoadRequestForUpdateAsync(
        MySqlConnection conn, MySqlTransaction tx, uint termId, uint requestId, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(
            """
            SELECT rr.request_id, rr.contract_id, rr.raw_text, rr.status, rr.exclusion_reason,
                   t.tenant_id AS requester_tenant_id, t.tenant_code, t.first_name, t.last_name,
                   fp.code AS floor_plan_code
            FROM roommate_requests rr
            JOIN contracts c ON c.contract_id = rr.contract_id
            JOIN tenants t ON t.tenant_id = c.tenant_id
            JOIN floor_plans fp ON fp.floor_plan_id = c.floor_plan_id
            WHERE rr.request_id = @id AND c.term_id = @termId
            FOR UPDATE
            """, conn, tx);
        cmd.Parameters.AddWithValue("@id", requestId);
        cmd.Parameters.AddWithValue("@termId", termId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new RequestRow(
            r.GetUInt32("request_id"),
            r.GetUInt32("contract_id"),
            r.GetUInt32("requester_tenant_id"),
            r.GetString("tenant_code"),
            $"{r.GetString("first_name")} {r.GetString("last_name")}",
            r.GetString("floor_plan_code"),
            r.GetString("raw_text"),
            r.GetString("status"),
            r.IsDBNull(r.GetOrdinal("exclusion_reason")) ? null : r.GetString("exclusion_reason"));
    }

    private static async Task UpsertReviewQueueAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        uint termId,
        RequestRow req,
        string itemTypeOrResolved,
        string note,
        bool closeOpen,
        CancellationToken ct)
    {
        if (closeOpen || itemTypeOrResolved == "resolved")
        {
            await using var close = new MySqlCommand(
                """
                UPDATE review_queue
                SET status = 'resolved',
                    resolution_note = @note
                WHERE roommate_request_id = @rid AND status = 'open'
                """, conn, tx);
            close.Parameters.AddWithValue("@note", Truncate(note, 500));
            close.Parameters.AddWithValue("@rid", req.RequestId);
            await close.ExecuteNonQueryAsync(ct);
            return;
        }

        await using var find = new MySqlCommand(
            """
            SELECT item_id FROM review_queue
            WHERE roommate_request_id = @rid AND status = 'open'
            LIMIT 1
            FOR UPDATE
            """, conn, tx);
        find.Parameters.AddWithValue("@rid", req.RequestId);
        var existing = await find.ExecuteScalarAsync(ct);

        var reason = req.ExclusionReason ?? note;
        if (existing is not null && existing is not DBNull)
        {
            await using var upd = new MySqlCommand(
                """
                UPDATE review_queue
                SET item_type = @type,
                    original_wording = @wording,
                    reason = @reason,
                    resolution_note = @note
                WHERE item_id = @id
                """, conn, tx);
            upd.Parameters.AddWithValue("@type", itemTypeOrResolved);
            upd.Parameters.AddWithValue("@wording", Truncate(req.RawText, 500));
            upd.Parameters.AddWithValue("@reason", Truncate(reason, 1000));
            upd.Parameters.AddWithValue("@note", Truncate(note, 500));
            upd.Parameters.AddWithValue("@id", Convert.ToUInt32(existing));
            await upd.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var ins = new MySqlCommand(
                """
                INSERT INTO review_queue
                    (term_id, draft_id, contract_id, roommate_request_id, item_type,
                     original_wording, reason, status, resolution_note)
                VALUES
                    (@termId, NULL, @contractId, @rid, @type, @wording, @reason, 'open', @note)
                """, conn, tx);
            ins.Parameters.AddWithValue("@termId", termId);
            ins.Parameters.AddWithValue("@contractId", req.ContractId);
            ins.Parameters.AddWithValue("@rid", req.RequestId);
            ins.Parameters.AddWithValue("@type", itemTypeOrResolved);
            ins.Parameters.AddWithValue("@wording", Truncate(req.RawText, 500));
            ins.Parameters.AddWithValue("@reason", Truncate(reason, 1000));
            ins.Parameters.AddWithValue("@note", Truncate(note, 500));
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static RoommateMutationResult Fail(string code, string message) =>
        new(false, [new ValidationIssueDto(code, "error", message, null, null, null, null, null, null)]);

    private sealed record RequestRow(
        uint RequestId,
        uint ContractId,
        uint RequesterTenantId,
        string RequesterTenantCode,
        string RequesterName,
        string RequesterFloorPlanCode,
        string RawText,
        string Status,
        string? ExclusionReason);
}
