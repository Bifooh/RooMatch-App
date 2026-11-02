using Lodge.Core.Models;
using Lodge.Core.Services;
using MySqlConnector;

namespace Lodge.Tests;

/// <summary>
/// Roommate request prep against MySQL when LODGE_TEST_CONNECTION / ConnectionStrings__Lodge is set.
/// </summary>
public class RoommateRequestPrepTests
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
            using var cmd = new MySqlCommand("SELECT COUNT(*) FROM roommate_requests", conn);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        catch
        {
            return false;
        }
    }

    private static RoommateRequestPrepService CreateService() =>
        new(new MySqlDataSource(ConnectionString!));

    [Fact]
    public async Task ListPrep_FlagsMissingDuplicateAndNear()
    {
        if (!CanConnect()) return;

        var svc = CreateService();
        var payload = await svc.ListPrepAsync(2);
        Assert.NotNull(payload);
        Assert.True(payload!.Summary.NeedsAttention > 0);

        var missing = payload.Items.FirstOrDefault(i =>
            i.RawText.Equals("Rowan Glimmerpatch", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(missing);
        Assert.Equal("missing", missing!.ConflictKind);
        Assert.Empty(missing.Candidates);

        var jordan = payload.Items.FirstOrDefault(i =>
            i.RawText.Equals("Jordan", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(jordan);
        Assert.Equal("duplicate", jordan!.ConflictKind);
        Assert.True(jordan.Candidates.Count >= 2);

        var misspelled = payload.Items.FirstOrDefault(i =>
            i.RawText.Equals("Quinn Jelyberry", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(misspelled);
        Assert.Equal("nearOnly", misspelled!.ConflictKind);
        Assert.DoesNotContain(misspelled.Candidates, c => c.MatchKind == "exact");
    }

    [Fact]
    public async Task Resolve_DuplicatePick_SetsResolved_PreservesRawText()
    {
        if (!CanConnect()) return;

        var svc = CreateService();
        var payload = await svc.ListPrepAsync(2);
        var jordan = payload!.Items.First(i =>
            i.RawText.Equals("Jordan", StringComparison.OrdinalIgnoreCase) && i.ConflictKind == "duplicate");
        var pick = jordan.Candidates.First();
        var original = jordan.RawText;

        var result = await svc.ResolveAsync(2, jordan.RequestId, new ResolveRoommateRequest(
            pick.TenantId, "Fictional Operator", "test pick"));
        Assert.True(result.Ok, result.Issues.FirstOrDefault()?.Message);
        Assert.NotNull(result.Item);
        Assert.Equal("resolved", result.Item!.Status);
        Assert.Equal(original, result.Item.RawText);

        // Reset so other tests / local UI stay useful
        await svc.MarkAsync(2, jordan.RequestId, new MarkRoommateRequest(
            "ambiguous", "Reset after test — still needs manager pick.", "Fictional Operator", null));
    }

    [Fact]
    public async Task Missing_MarkUnresolved_RequiresReason_AndDoesNotInventTenant()
    {
        if (!CanConnect()) return;

        var svc = CreateService();
        var payload = await svc.ListPrepAsync(2);
        var missing = payload!.Items.First(i =>
            i.RawText.Equals("Rowan Glimmerpatch", StringComparison.OrdinalIgnoreCase));

        var bad = await svc.MarkAsync(2, missing.RequestId, new MarkRoommateRequest(
            "unresolved", "  ", "Fictional Operator", null));
        Assert.False(bad.Ok);

        var invent = await svc.ResolveAsync(2, missing.RequestId, new ResolveRoommateRequest(
            999999, "Fictional Operator", null));
        Assert.False(invent.Ok);
        Assert.Contains(invent.Issues, i => i.Code == "TENANT_NOT_IN_TERM");

        var ok = await svc.MarkAsync(2, missing.RequestId, new MarkRoommateRequest(
            "unresolved", "Person not in tenant list for this term.", "Fictional Operator", null));
        Assert.True(ok.Ok);
        Assert.Equal("unresolved", ok.Item!.Status);
        Assert.Equal(missing.RawText, ok.Item.RawText);
        Assert.NotNull(ok.Item.ExclusionReason);

        // Leave as unresolved (realistic for E04); re-list still shows it under attention
        var again = await svc.ListPrepAsync(2);
        Assert.Contains(again!.Items, i => i.RequestId == missing.RequestId && i.Status == "unresolved");
    }
}
