using Lodge.Core.Services;
using Lodge.Core.Validation;

namespace Lodge.Tests;

public class AutoMatchPlannerTests
{
    private static DraftState EmptyPlanState()
    {
        var state = new DraftState
        {
            DraftId = 1,
            TermId = 2,
            Status = "draft",
            DraftName = "test",
        };

        state.Contracts[1] = new ContractState(1, 10, "FAKE-A", "Ada", "Alpha", 2, 1, "W-STD-2", "new");
        state.Contracts[2] = new ContractState(2, 20, "FAKE-B", "Bea", "Beta", 2, 1, "W-STD-2", "new");
        state.Contracts[3] = new ContractState(3, 30, "FAKE-C", "Cara", "Chi", 2, 1, "W-STD-2", "new");
        state.Apartments[100] = new ApartmentSnapshot(100, "1001", 1, "W-STD-2", "Women's", 1, 2, true);
        state.Apartments[101] = new ApartmentSnapshot(101, "1002", 1, "W-STD-2", "Women's", 1, 2, true);
        return state;
    }

    [Fact]
    public void MutualResolved_PlacedTogether_BeforeGreedy()
    {
        var state = EmptyPlanState();
        state.RoommateRequests.Add(new RoommateRequestState(
            1, 1, "FAKE-A", "Ada Alpha", "Bea Beta", "resolved", null, ResolvedTenantId: 20));
        state.RoommateRequests.Add(new RoommateRequestState(
            2, 2, "FAKE-B", "Bea Beta", "Ada Alpha", "resolved", null, ResolvedTenantId: 10));
        // Pending / missing should be ignored
        state.RoommateRequests.Add(new RoommateRequestState(
            3, 3, "FAKE-C", "Cara Chi", "Nobody Here", "pending", null));

        var plan = AutoMatchPlanner.Build(state);
        Assert.Equal(1, plan.MutualPairsPlaced); // one mutual pair
        Assert.Equal(0, plan.OneWayJoinsPlaced);
        Assert.Equal(3, plan.Steps.Count);

        var aptA = plan.Steps.First(s => s.ContractId == 1).ApartmentId;
        var aptB = plan.Steps.First(s => s.ContractId == 2).ApartmentId;
        Assert.Equal(aptA, aptB);
    }

    [Fact]
    public void OneWayResolved_JoinsTarget_WhenPossible()
    {
        var state = EmptyPlanState();
        // A → B only (not mutual)
        state.RoommateRequests.Add(new RoommateRequestState(
            1, 1, "FAKE-A", "Ada Alpha", "Bea Beta", "resolved", null, ResolvedTenantId: 20));

        var plan = AutoMatchPlanner.Build(state);
        Assert.Equal(0, plan.MutualPairsPlaced);
        Assert.True(plan.OneWayJoinsPlaced >= 1);
        var aptA = plan.Steps.First(s => s.ContractId == 1).ApartmentId;
        var aptB = plan.Steps.First(s => s.ContractId == 2).ApartmentId;
        Assert.Equal(aptA, aptB);
    }

    [Fact]
    public void Unresolved_DoesNotInventIdentity_OrForcePair()
    {
        var state = EmptyPlanState();
        state.RoommateRequests.Add(new RoommateRequestState(
            1, 1, "FAKE-A", "Ada Alpha", "Ghost Person", "unresolved", "not in list", null));

        var pairs = AutoMatchPlanner.FindMutualResolvedPairs(
            state, state.Contracts.Values.ToDictionary(c => c.TenantId, c => c.ContractId));
        Assert.Empty(pairs);

        var plan = AutoMatchPlanner.Build(state);
        Assert.Equal(0, plan.MutualPairsPlaced);
        Assert.Equal(0, plan.OneWayJoinsPlaced);
        // Still places greedily into capacity
        Assert.Equal(3, plan.Steps.Count);
    }

    [Fact]
    public void Capacity_Respected_LeavesUnplaced()
    {
        var state = EmptyPlanState();
        // Only one apt capacity 2 for three people
        state.Apartments.Remove(101);

        var plan = AutoMatchPlanner.Build(state);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Contains(plan.Skips, s => s.Reason.Contains("No free capacity"));
    }

    [Fact]
    public void LockedAssignment_NotMoved_PartnerMayJoin()
    {
        var state = EmptyPlanState();
        state.AssignmentsByContract[1] = new AssignmentState(
            1, 1, 100, true, "Op", DateTime.UtcNow, "manual");
        state.RoommateRequests.Add(new RoommateRequestState(
            1, 1, "FAKE-A", "Ada Alpha", "Bea Beta", "resolved", null, 20));
        state.RoommateRequests.Add(new RoommateRequestState(
            2, 2, "FAKE-B", "Bea Beta", "Ada Alpha", "resolved", null, 10));

        var plan = AutoMatchPlanner.Build(state);
        Assert.DoesNotContain(plan.Steps, s => s.ContractId == 1);
        var join = plan.Steps.FirstOrDefault(s => s.ContractId == 2);
        Assert.NotNull(join);
        Assert.Equal(100u, join!.ApartmentId);
    }
}
