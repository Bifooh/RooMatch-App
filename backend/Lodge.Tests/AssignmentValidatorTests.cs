using Lodge.Core.Validation;

namespace Lodge.Tests;

public class AssignmentValidatorTests
{
    private readonly AssignmentValidator _validator = new();

    [Fact]
    public void Move_Rejects_WrongFloorPlan()
    {
        var state = SampleState();
        var issues = _validator.ValidateMove(state, contractId: 1, apartmentId: 20); // M plan apt
        Assert.Contains(issues, i => i.Code == ValidationIssue.Codes.FloorPlanMismatch && i.IsBlocking);
    }

    [Fact]
    public void Move_Rejects_OverCapacity()
    {
        var state = SampleState();
        // Apt 10: capacity 2, already has contracts 2 and 3
        state.Apartments[10] = state.Apartments[10] with { EffectiveCapacity = 2 };
        state.AssignmentsByContract[2] = new AssignmentState(2, 2, 10, false, null, null, "manual");
        state.AssignmentsByContract[3] = new AssignmentState(3, 3, 10, false, null, null, "manual");
        var issues = _validator.ValidateMove(state, contractId: 1, apartmentId: 10);
        Assert.Contains(issues, i => i.Code == ValidationIssue.Codes.CapacityExceeded && i.IsBlocking);
    }

    [Fact]
    public void Move_Allows_WithinCapacity_SamePlan()
    {
        var state = SampleState();
        var issues = _validator.ValidateMove(state, contractId: 1, apartmentId: 10);
        Assert.DoesNotContain(issues, i => i.IsBlocking);
    }

    [Fact]
    public void Move_Rejects_LockedAssignment()
    {
        var state = SampleState();
        state.AssignmentsByContract[1] = new AssignmentState(1, 1, 10, true, "Fictional Manager", DateTime.UtcNow, "manual");
        var issues = _validator.ValidateMove(state, contractId: 1, apartmentId: 11);
        Assert.Contains(issues, i => i.Code == ValidationIssue.Codes.AssignmentLocked && i.IsBlocking);
    }

    [Fact]
    public void Move_Rejects_RequiredFloorViolation()
    {
        var state = SampleState();
        state.Restrictions.Add(new RestrictionState(1, 1, "required_floor", null, null, "1", "ground floor"));
        // Apt 11 is floor 2
        var issues = _validator.ValidateMove(state, contractId: 1, apartmentId: 11);
        Assert.Contains(issues, i => i.Code == ValidationIssue.Codes.RestrictionRequiredFloor && i.IsBlocking);
    }

    [Fact]
    public void Move_Rejects_MustNotShare()
    {
        var state = SampleState();
        state.AssignmentsByContract[2] = new AssignmentState(2, 2, 10, false, null, null, "manual");
        state.Restrictions.Add(new RestrictionState(1, 1, "must_not_share_with", null, 200, null, "no share"));
        var issues = _validator.ValidateMove(state, contractId: 1, apartmentId: 10);
        Assert.Contains(issues, i => i.Code == ValidationIssue.Codes.RestrictionMustNotShare && i.IsBlocking);
    }

    [Fact]
    public void Move_Warns_ButDoesNotBlock_UnconfirmedAllergy()
    {
        var state = SampleState();
        state.Preferences.Add(new PreferenceState(1, HasAnimal: true, AnimalAllergyRestriction: false));
        state.Preferences.Add(new PreferenceState(2, HasAnimal: false, AnimalAllergyRestriction: true));
        state.AssignmentsByContract[2] = new AssignmentState(2, 2, 10, false, null, null, "manual");
        var issues = _validator.ValidateMove(state, contractId: 1, apartmentId: 10);
        Assert.Contains(issues, i => i.Code == ValidationIssue.Codes.UnconfirmedAllergyColocation);
        Assert.DoesNotContain(issues, i => i.IsBlocking);
    }

    [Fact]
    public void Move_Rejects_FinalDraft()
    {
        var state = SampleState();
        var final = new DraftState
        {
            DraftId = state.DraftId,
            TermId = state.TermId,
            Status = "final",
            DraftName = state.DraftName,
        };
        foreach (var (k, v) in state.Contracts) final.Contracts[k] = v;
        foreach (var (k, v) in state.Apartments) final.Apartments[k] = v;

        var issues = _validator.ValidateMove(final, 1, 10);
        Assert.Contains(issues, i => i.Code == ValidationIssue.Codes.DraftNotEditable);
    }

    [Fact]
    public void Unassign_Rejects_WhenLocked()
    {
        var state = SampleState();
        state.AssignmentsByContract[1] = new AssignmentState(1, 1, 10, true, "Fictional Manager", DateTime.UtcNow, "manual");
        var issues = _validator.ValidateMove(state, 1, null);
        Assert.Contains(issues, i => i.Code == ValidationIssue.Codes.AssignmentLocked);
    }

    [Fact]
    public void Swap_Succeeds_WhenBothAssignedCompatible()
    {
        var state = SampleState();
        state.AssignmentsByContract[1] = new AssignmentState(1, 1, 10, false, null, null, "manual");
        state.AssignmentsByContract[2] = new AssignmentState(2, 2, 11, false, null, null, "manual");
        var issues = _validator.ValidateSwap(state, 1, 2);
        Assert.DoesNotContain(issues, i => i.IsBlocking);
    }

    [Fact]
    public void Swap_Rejects_WhenOneLocked()
    {
        var state = SampleState();
        state.AssignmentsByContract[1] = new AssignmentState(1, 1, 10, true, "Mgr", DateTime.UtcNow, "manual");
        state.AssignmentsByContract[2] = new AssignmentState(2, 2, 11, false, null, null, "manual");
        var issues = _validator.ValidateSwap(state, 1, 2);
        Assert.Contains(issues, i => i.Code == ValidationIssue.Codes.AssignmentLocked);
    }

    [Fact]
    public void Swap_Rejects_FloorPlanMismatchOnResult()
    {
        var state = SampleState();
        // Contract 1 is W plan; apt 20 is M plan. Put contract 4 (M) in apt 10 (W) illegally in source
        // Better: swap contract 1 (W) with contract 4 (M) — resulting placements mismatch plans.
        state.AssignmentsByContract[1] = new AssignmentState(1, 1, 10, false, null, null, "manual");
        state.AssignmentsByContract[4] = new AssignmentState(4, 4, 20, false, null, null, "manual");
        var issues = _validator.ValidateSwap(state, 1, 4);
        Assert.Contains(issues, i => i.Code == ValidationIssue.Codes.FloorPlanMismatch && i.IsBlocking);
    }

    [Fact]
    public void Swap_Rejects_MustNotShareOnResult()
    {
        var state = SampleState();
        // Contract 3 already in apt 11. Swap puts contract 1 into apt 11 beside tenant 300.
        state.AssignmentsByContract[1] = new AssignmentState(1, 1, 10, false, null, null, "manual");
        state.AssignmentsByContract[2] = new AssignmentState(2, 2, 11, false, null, null, "manual");
        state.AssignmentsByContract[3] = new AssignmentState(3, 3, 11, false, null, null, "manual");
        state.Restrictions.Add(new RestrictionState(1, 1, "must_not_share_with", null, 300, null, "no share"));
        var issues = _validator.ValidateSwap(state, 1, 2);
        Assert.Contains(issues, i => i.Code == ValidationIssue.Codes.RestrictionMustNotShare);
    }

    [Fact]
    public void Swap_AtomicCapacity_BothApartmentsFull_StillOk()
    {
        var state = SampleState();
        // Both apartments capacity 1, each full — swap must still succeed (net occupancy unchanged).
        state.Apartments[10] = state.Apartments[10] with { EffectiveCapacity = 1 };
        state.Apartments[11] = state.Apartments[11] with { EffectiveCapacity = 1 };
        state.AssignmentsByContract[1] = new AssignmentState(1, 1, 10, false, null, null, "manual");
        state.AssignmentsByContract[2] = new AssignmentState(2, 2, 11, false, null, null, "manual");
        var issues = _validator.ValidateSwap(state, 1, 2);
        Assert.DoesNotContain(issues, i => i.IsBlocking);
    }

    [Fact]
    public void Lock_RequiresOperatorIdentity()
    {
        var state = SampleState();
        state.AssignmentsByContract[1] = new AssignmentState(1, 1, 10, false, null, null, "manual");
        var issues = _validator.ValidateLock(state, 1, locked: true, lockedBy: "  ");
        Assert.Contains(issues, i => i.Code == ValidationIssue.Codes.LockOwnerRequired);
    }

    [Fact]
    public void ValidateDraft_SurfacesPendingRoommateRequestsAsInfo()
    {
        var state = SampleState();
        state.RoommateRequests.Add(new RoommateRequestState(
            1, 1, "FAKE-00001", "Arlo Pebblecrumb", "Jordan", "pending", null));
        var issues = _validator.ValidateDraft(state);
        Assert.Contains(issues, i => i.Code == ValidationIssue.Codes.UnresolvedRoommateRequest
                                     && i.Severity == ValidationIssue.Severities.Info);
    }

    private static DraftState SampleState()
    {
        var state = new DraftState
        {
            DraftId = 1,
            TermId = 2,
            Status = "draft",
            DraftName = "Test",
        };

        state.Contracts[1] = new ContractState(1, 100, "FAKE-00001", "Arlo", "Pebblecrumb", 2, 1, "W-STD-2", "new");
        state.Contracts[2] = new ContractState(2, 200, "FAKE-00002", "Harper", "Fizzfizzle", 2, 1, "W-STD-2", "new");
        state.Contracts[3] = new ContractState(3, 300, "FAKE-00003", "Willow", "Wobbleberry", 2, 1, "W-STD-2", "new");
        state.Contracts[4] = new ContractState(4, 400, "FAKE-00017", "Lark", "Jellynugget", 2, 7, "M-STD-3", "new");

        state.Apartments[10] = new ApartmentSnapshot(10, "3102", 1, "W-STD-2", "Women's", 1, 4, true);
        state.Apartments[11] = new ApartmentSnapshot(11, "3227", 1, "W-STD-2", "Women's", 2, 4, true);
        state.Apartments[20] = new ApartmentSnapshot(20, "2421", 7, "M-STD-3", "Men's", 1, 6, true);

        return state;
    }
}
