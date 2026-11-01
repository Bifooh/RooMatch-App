using Lodge.Core.Models;

namespace Lodge.Core.Services;

/// <summary>
/// Thin V1 auto-matcher: creates a working draft and places via <see cref="AssignmentService.MoveAsync"/>.
/// </summary>
public sealed class AutoMatchService
{
    private readonly AssignmentService _assignments;

    public AutoMatchService(AssignmentService assignments) => _assignments = assignments;

    public async Task<AutoMatchResult> RunAsync(uint termId, AutoMatchRequest? request, CancellationToken ct = default)
    {
        if (request is null)
            return Fail("BODY_REQUIRED", "Request body is required (performedBy at minimum).");

        if (string.IsNullOrWhiteSpace(request.PerformedBy))
            return Fail("OPERATOR_REQUIRED", "Fictional operator identity (performedBy) is required.");

        if (request.DraftName is { Length: > 100 })
            return Fail("DRAFT_NAME_TOO_LONG", "draftName must be 100 characters or fewer.");

        var name = string.IsNullOrWhiteSpace(request.DraftName)
            ? $"Auto match {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z"
            : request.DraftName.Trim();

        var (draft, createIssues) = await _assignments.CreateDraftAsync(
            termId,
            new CreateDraftRequest(name, request.CopyFromDraftId, request.CopyLocks, request.Notes),
            ct);

        if (draft is null)
        {
            var msg = createIssues.FirstOrDefault()?.Message ?? "Draft creation failed.";
            return new AutoMatchResult(
                false, null, 0, 0, 0, 0,
                EmptySummary(msg),
                Array.Empty<AutoMatchPlacement>(),
                Array.Empty<AutoMatchSkip>(),
                ["Draft creation failed — no placements were attempted."],
                createIssues,
                Error: msg);
        }

        var state = await _assignments.LoadStateAsync(draft.DraftId, ct);
        if (state is null)
        {
            return Fail("DRAFT_NOT_FOUND",
                $"Created draft {draft.DraftId} could not be loaded; matching aborted.");
        }

        var plan = AutoMatchPlanner.Build(state);
        var placements = new List<AutoMatchPlacement>();
        var skips = plan.Skips
            .Select(s => new AutoMatchSkip(s.ContractId, s.TenantCode, s.Reason))
            .ToList();
        var issues = new List<ValidationIssueDto>();

        foreach (var step in plan.Steps)
        {
            MutationResult move;
            try
            {
                move = await _assignments.MoveAsync(
                    draft.DraftId,
                    new MoveRequest(
                        step.ContractId,
                        step.ApartmentId,
                        request.PerformedBy.Trim(),
                        step.Reason,
                        Source: "generated"),
                    ct);
            }
            catch (Exception ex)
            {
                var code = state.Contracts.TryGetValue(step.ContractId, out var c0) ? c0.TenantCode : "?";
                skips.Add(new AutoMatchSkip(step.ContractId, code,
                    $"Move failed unexpectedly ({ex.GetType().Name}); remaining plan continues."));
                continue;
            }

            if (!move.Ok)
            {
                var code = state.Contracts.TryGetValue(step.ContractId, out var c) ? c.TenantCode : "?";
                var detail = move.Issues.FirstOrDefault();
                skips.Add(new AutoMatchSkip(step.ContractId, code,
                    detail is null
                        ? "Move rejected."
                        : $"Move rejected: {detail.Code} — {detail.Message}"));
                issues.AddRange(move.Issues);
                continue;
            }

            state = await _assignments.LoadStateAsync(draft.DraftId, ct) ?? state;
            state.Apartments.TryGetValue(step.ApartmentId, out var apt);
            state.Contracts.TryGetValue(step.ContractId, out var contract);
            placements.Add(new AutoMatchPlacement(
                step.ContractId,
                contract?.TenantCode ?? "?",
                step.ApartmentId,
                apt?.ApartmentNumber ?? "?",
                step.Reason,
                move.HistoryId));
        }

        state = await _assignments.LoadStateAsync(draft.DraftId, ct) ?? state;
        var stillUnassigned = state.Contracts.Keys.Count(id => !state.AssignmentsByContract.ContainsKey(id));
        var summary = BuildSummary(
            placements.Count, stillUnassigned, plan.MutualPairsPlaced, plan.OneWayJoinsPlaced, skips.Count,
            draft.DraftId);

        return new AutoMatchResult(
            true,
            draft,
            placements.Count,
            stillUnassigned,
            plan.MutualPairsPlaced,
            plan.OneWayJoinsPlaced,
            summary,
            placements,
            skips,
            plan.Notes,
            issues);
    }

    private static AutoMatchSummary BuildSummary(
        int placed, int unassigned, int mutual, int oneWay, int skipped, uint draftId) =>
        new(placed, unassigned, mutual, oneWay, skipped,
            $"Draft #{draftId}: placed {placed}, unassigned {unassigned}, mutual pairs {mutual}, one-way joins {oneWay}, skipped {skipped}.");

    private static AutoMatchSummary EmptySummary(string headline) =>
        new(0, 0, 0, 0, 0, headline);

    private static AutoMatchResult Fail(string code, string message) =>
        new(false, null, 0, 0, 0, 0,
            EmptySummary(message),
            Array.Empty<AutoMatchPlacement>(),
            Array.Empty<AutoMatchSkip>(),
            Array.Empty<string>(),
            [new ValidationIssueDto(code, "error", message, null, null, null, null, null, null)],
            Error: message);
}
