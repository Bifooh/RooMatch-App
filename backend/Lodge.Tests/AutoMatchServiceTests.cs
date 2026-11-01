using Lodge.Core.Models;
using Lodge.Core.Services;
using MySqlConnector;

namespace Lodge.Tests;

/// <summary>
/// Auto-match against lodge_small when LODGE_TEST_CONNECTION / ConnectionStrings__Lodge is set.
/// </summary>
public class AutoMatchServiceTests
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
            using var cmd = new MySqlCommand("SELECT COUNT(*) FROM contracts WHERE term_id = 2", conn);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        catch
        {
            return false;
        }
    }

    private static (AssignmentService Assignments, AutoMatchService Match) Create()
    {
        var ds = new MySqlDataSource(ConnectionString!);
        var assignments = new AssignmentService(ds);
        return (assignments, new AutoMatchService(assignments));
    }

    [Fact]
    public async Task Run_CreatesDraft_PlacesThroughMove_HistoryRecorded()
    {
        if (!CanConnect()) return;

        var (assignments, match) = Create();
        var result = await match.RunAsync(2, new AutoMatchRequest(
            $"Match {Guid.NewGuid():N}"[..18],
            null,
            false,
            "Fictional Matcher",
            "v1 test"));

        Assert.True(result.Ok, result.Error ?? result.Issues.FirstOrDefault()?.Message);
        Assert.NotNull(result.Draft);
        Assert.True(result.PlacedCount > 0);
        Assert.Equal(result.PlacedCount, result.Summary.Placed);
        Assert.Contains(result.Notes, n => n.Contains("V1 auto-match"));
        Assert.Contains($"Draft #{result.Draft!.DraftId}", result.Summary.Headline);

        var hist = await assignments.ListHistoryAsync(result.Draft!.DraftId);
        Assert.True(hist.Count >= result.PlacedCount);
        Assert.Contains(hist, h => h.ChangeType == "move" && h.PerformedBy == "Fictional Matcher");

        var ws = await assignments.GetWorkspaceAsync(result.Draft.DraftId);
        Assert.NotNull(ws);
        var assigned = ws!.Apartments.Sum(a => a.Occupants.Count);
        Assert.Equal(result.PlacedCount, assigned);
        // All assigned occupants must match contracted plan (validator already enforced)
        foreach (var apt in ws.Apartments)
        {
            foreach (var occ in apt.Occupants)
            {
                var u = ws.Unassigned.FirstOrDefault(x => x.ContractId == occ.ContractId);
                Assert.Null(u);
            }
        }
    }

    [Fact]
    public async Task Run_WithMutualResolved_PlacesPairTogether()
    {
        if (!CanConnect()) return;

        await using var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync();

        // Use E05-ish pair FAKE-00011 / FAKE-00012 if present; otherwise first two same-plan contracts.
        uint contractA, contractB, tenantA, tenantB;
        await using (var cmd = new MySqlCommand(
            """
            SELECT c1.contract_id, c1.tenant_id, c2.contract_id, c2.tenant_id
            FROM contracts c1
            JOIN contracts c2 ON c2.term_id = c1.term_id AND c2.floor_plan_id = c1.floor_plan_id
                             AND c2.contract_id > c1.contract_id
            WHERE c1.term_id = 2
            ORDER BY c1.contract_id
            LIMIT 1
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await r.ReadAsync());
            contractA = r.GetUInt32(0);
            tenantA = r.GetUInt32(1);
            contractB = r.GetUInt32(2);
            tenantB = r.GetUInt32(3);
        }

        // Ensure mutual resolved requests (insert or update)
        await EnsureResolvedAsync(conn, contractA, tenantB);
        await EnsureResolvedAsync(conn, contractB, tenantA);

        var (_, match) = Create();
        var result = await match.RunAsync(2, new AutoMatchRequest(
            $"Pair {Guid.NewGuid():N}"[..18],
            null,
            false,
            "Fictional Matcher",
            null));

        Assert.True(result.Ok);
        Assert.True(result.MutualPairsPlaced >= 1);

        var placeA = result.Placements.FirstOrDefault(p => p.ContractId == contractA);
        var placeB = result.Placements.FirstOrDefault(p => p.ContractId == contractB);
        Assert.NotNull(placeA);
        Assert.NotNull(placeB);
        Assert.Equal(placeA!.ApartmentId, placeB!.ApartmentId);
    }

    private static async Task EnsureResolvedAsync(MySqlConnection conn, uint contractId, uint resolvedTenantId)
    {
        await using var find = new MySqlCommand(
            "SELECT request_id FROM roommate_requests WHERE contract_id = @c LIMIT 1", conn);
        find.Parameters.AddWithValue("@c", contractId);
        var idObj = await find.ExecuteScalarAsync();
        if (idObj is null || idObj is DBNull)
        {
            await using var ins = new MySqlCommand(
                """
                INSERT INTO roommate_requests (contract_id, raw_text, resolved_tenant_id, status, exclusion_reason)
                VALUES (@c, 'test mutual', @t, 'resolved', NULL)
                """, conn);
            ins.Parameters.AddWithValue("@c", contractId);
            ins.Parameters.AddWithValue("@t", resolvedTenantId);
            await ins.ExecuteNonQueryAsync();
        }
        else
        {
            await using var upd = new MySqlCommand(
                """
                UPDATE roommate_requests
                SET resolved_tenant_id = @t, status = 'resolved', exclusion_reason = NULL
                WHERE request_id = @id
                """, conn);
            upd.Parameters.AddWithValue("@t", resolvedTenantId);
            upd.Parameters.AddWithValue("@id", Convert.ToUInt32(idObj));
            await upd.ExecuteNonQueryAsync();
        }
    }
}
