using Lodge.Core.Models;
using Lodge.Core.Services;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);

// Untracked local overrides (connection string with any dev password). See appsettings.Local.example.json.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(); // env vars (e.g. ConnectionStrings__Lodge) still win over the local file

var connectionString = builder.Configuration.GetConnectionString("Lodge")
    ?? throw new InvalidOperationException("ConnectionStrings:Lodge is not configured. Copy appsettings.Local.example.json to appsettings.Local.json.");
builder.Services.AddMySqlDataSource(connectionString);
builder.Services.AddSingleton<AssignmentService>();
builder.Services.AddSingleton<AutoMatchService>();

var app = builder.Build();

app.MapGet("/api/health", async (MySqlDataSource db) =>
{
    try
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var cmd = new MySqlCommand("SELECT DATABASE(), VERSION()", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return Results.Ok(new { status = "ok", database = r.GetString(0), serverVersion = r.GetString(1) });
    }
    catch (Exception ex)
    {
        // Do not echo connection strings or row data.
        return Results.Json(new { status = "unhealthy", error = ex.GetType().Name }, statusCode: 503);
    }
});

app.MapGet("/api/summary", async (MySqlDataSource db) =>
{
    await using var conn = await db.OpenConnectionAsync();
    await using var cmd = new MySqlCommand(
        "SELECT (SELECT COUNT(*) FROM tenants), (SELECT COUNT(*) FROM apartments), (SELECT COUNT(*) FROM floor_plans)", conn);
    await using var r = await cmd.ExecuteReaderAsync();
    await r.ReadAsync();
    return Results.Ok(new { tenants = r.GetInt64(0), apartments = r.GetInt64(1), floorPlans = r.GetInt64(2) });
});

app.MapGet("/api/terms", async (AssignmentService svc, CancellationToken ct) =>
    Results.Ok(await svc.ListTermsAsync(ct)));

app.MapGet("/api/terms/{termId:int}/drafts", async (uint termId, AssignmentService svc, CancellationToken ct) =>
    Results.Ok(await svc.ListDraftsAsync(termId, ct)));

app.MapPost("/api/terms/{termId:int}/drafts", async (uint termId, CreateDraftRequest body, AssignmentService svc, CancellationToken ct) =>
{
    var (draft, issues) = await svc.CreateDraftAsync(termId, body, ct);
    if (draft is null)
        return Results.Json(new { ok = false, issues }, statusCode: 400);
    return Results.Ok(new { ok = true, draft, issues });
});

app.MapGet("/api/drafts/{draftId:int}/workspace", async (uint draftId, AssignmentService svc, CancellationToken ct) =>
{
    var workspace = await svc.GetWorkspaceAsync(draftId, ct);
    return workspace is null ? Results.NotFound(new { error = "Draft not found." }) : Results.Ok(workspace);
});

app.MapPost("/api/drafts/{draftId:int}/move", async (uint draftId, MoveRequest body, AssignmentService svc, CancellationToken ct) =>
{
    try
    {
        var result = await svc.MoveAsync(draftId, body, ct);
        return result.Ok
            ? Results.Ok(result)
            : Results.Json(result, statusCode: 409);
    }
    catch (Exception)
    {
        return Results.Json(new { ok = false, error = "Move failed due to a server or database error." }, statusCode: 500);
    }
});

app.MapPost("/api/drafts/{draftId:int}/swap", async (uint draftId, SwapRequest body, AssignmentService svc, CancellationToken ct) =>
{
    try
    {
        var result = await svc.SwapAsync(draftId, body, ct);
        return result.Ok
            ? Results.Ok(result)
            : Results.Json(result, statusCode: 409);
    }
    catch (Exception)
    {
        return Results.Json(new { ok = false, error = "Swap failed due to a server or database error." }, statusCode: 500);
    }
});

app.MapPost("/api/drafts/{draftId:int}/lock", async (uint draftId, LockRequest body, AssignmentService svc, CancellationToken ct) =>
{
    try
    {
        var result = await svc.SetLockAsync(draftId, body, ct);
        return result.Ok
            ? Results.Ok(result)
            : Results.Json(result, statusCode: 409);
    }
    catch (Exception)
    {
        return Results.Json(new { ok = false, error = "Lock update failed due to a server or database error." }, statusCode: 500);
    }
});

app.MapGet("/api/drafts/{draftId:int}/history", async (uint draftId, int? limit, AssignmentService svc, CancellationToken ct) =>
{
    var items = await svc.ListHistoryAsync(draftId, limit ?? 50, ct);
    return Results.Ok(items);
});

app.MapPost("/api/drafts/{draftId:int}/undo", async (uint draftId, UndoRequest body, AssignmentService svc, CancellationToken ct) =>
{
    try
    {
        var result = await svc.UndoAsync(draftId, body, ct);
        return result.Ok
            ? Results.Ok(result)
            : Results.Json(result, statusCode: 409);
    }
    catch (Exception)
    {
        return Results.Json(new { ok = false, error = "Undo failed due to a server or database error." }, statusCode: 500);
    }
});

app.MapPost("/api/terms/{termId:int}/match",
    async (uint termId, AutoMatchRequest? body, AutoMatchService svc, CancellationToken ct) =>
    {
        try
        {
            var result = await svc.RunAsync(termId, body, ct);
            if (result.Ok) return Results.Ok(result);
            // 400 for bad input; 409 for draft/validation conflicts
            var code = result.Issues.FirstOrDefault()?.Code ?? "";
            var status = code is "BODY_REQUIRED" or "OPERATOR_REQUIRED" or "DRAFT_NAME_TOO_LONG" or "TERM_NOT_FOUND"
                or "DRAFT_NAME_REQUIRED"
                ? 400
                : 409;
            return Results.Json(result, statusCode: status);
        }
        catch (Exception)
        {
            return Results.Json(new
            {
                ok = false,
                error = "Auto-match failed due to a server or database error. Check MySQL and that migration 001 is applied.",
                summary = new { headline = "Auto-match failed unexpectedly." },
            }, statusCode: 500);
        }
    });

app.Run();

public partial class Program { }
