using Lodge.Core.Models;

namespace Lodge.Core.Services;

/// <summary>
/// Thin V1 auto-matcher: creates a working draft and places via <see cref="AssignmentService.MoveAsync"/>.
/// </summary>
public sealed class AutoMatchService
{
    private readonly AssignmentService _assignments;

    public AutoMatchService(AssignmentService assignments) => _assignments = assignments;

    public async Task<AutoMatchResult> RunAsync(uint termId, AutoMatchRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.PerformedBy))
        {
            return Fail("OPERATOR_REQUIRED", "Fictional operator identity is required.");
        }

        var name = string.IsNullOrWhiteSpace(request.DraftName)
            ? $"Auto match {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z"
            : request.DraftName.Trim();

        var (draft, createIssues) = await _assignments.CreateDraftAsync(
            termId,
            new CreateDraftRequest(name, request.CopyFromDraftId, request.CopyLocks, request.Notes),
            ct);

        if (draft is null)
        {
            return new AutoMatchResult(
                false, null, 0, 0, 0,
                Array.Empty<AutoMatchPlacement>(),
                Array.Empty<AutoMatchSkip>(),
                ["Draft creation failed."],
                createIssues);
        }

        var state = await _assignments.LoadStateAsync(draft.DraftId, ct);
        if (state is null)
        {
            return Fail("DRAFT_NOT_FOUND", $"Created draft {draft.DraftId} could not be loaded.");
        }

        var plan = AutoMatchPlanner.Build(state);
        var placements = new List<AutoMatchPlacement>();
        var skips = plan.Skips
            .Select(s => new AutoMatchSkip(s.ContractId, s.TenantCode, s.Reason))
            .ToList();
        var issues = new List<ValidationIssueDto>();

        foreach (var step in plan.Steps)
        {
            var move = await _assignments.MoveAsync(
                draft.DraftId,
                new MoveRequest(
                    step.ContractId,
                    step.ApartmentId,
                    request.PerformedBy.Trim(),
                    step.Reason,
                    Source: "generated"),
                ct);

            if (!move.Ok)
            {
                var code = state.Contracts.TryGetValue(step.ContractId, out var c) ? c.TenantCode : "?";
                skips.Add(new AutoMatchSkip(step.ContractId, code,
                    $"Move rejected: {move.Issues.FirstOrDefault()?.Code ?? "unknown"}"));
                issues.AddRange(move.Issues);
                continue;
            }

            // Refresh apartment number from reloaded state when possible
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

        return new AutoMatchResult(
            true,
            draft,
            placements.Count,
            stillUnassigned,
            plan.MutualPairsPlaced,
            placements,
            skips,
            plan.Notes,
            issues);
    }

    private static AutoMatchResult Fail(string code, string message) =>
        new(false, null, 0, 0, 0,
            Array.Empty<AutoMatchPlacement>(),
            Array.Empty<AutoMatchSkip>(),
            Array.Empty<string>(),
            [new ValidationIssueDto(code, "error", message, null, null, null, null, null, null)]);
}
