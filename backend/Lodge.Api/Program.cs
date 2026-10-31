using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);

// Untracked local overrides (connection string with any dev password). See appsettings.Local.example.json.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(); // env vars (e.g. ConnectionStrings__Lodge) still win over the local file

var connectionString = builder.Configuration.GetConnectionString("Lodge")
    ?? throw new InvalidOperationException("ConnectionStrings:Lodge is not configured. Copy appsettings.Local.example.json to appsettings.Local.json.");
builder.Services.AddMySqlDataSource(connectionString);

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
        return Results.Json(new { status = "unhealthy", error = ex.Message }, statusCode: 503);
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

app.Run();

public partial class Program { }
