using ClipHarbor.Server;
using ClipHarbor.Sync;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Net.Http.Headers;
using Npgsql;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = Protocol.PartBytes + 65536);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var dataDirectory = Environment.GetEnvironmentVariable("CLIPHARBOR_DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDirectory);
string? Secret(string name)
{
    var path = Environment.GetEnvironmentVariable(name + "_FILE");
    return string.IsNullOrEmpty(path) ? Environment.GetEnvironmentVariable(name) : File.ReadAllText(path).TrimEnd('\r', '\n');
}
var connectionString = Environment.GetEnvironmentVariable("CLIPHARBOR_DATABASE");
if (string.IsNullOrEmpty(connectionString)) connectionString = new NpgsqlConnectionStringBuilder
{
    Host = Environment.GetEnvironmentVariable("CLIPHARBOR_DB_HOST") ?? "localhost",
    Port = int.Parse(Environment.GetEnvironmentVariable("CLIPHARBOR_DB_PORT") ?? "5432"),
    Database = Environment.GetEnvironmentVariable("CLIPHARBOR_DB_NAME") ?? "clipharbor",
    Username = Environment.GetEnvironmentVariable("CLIPHARBOR_DB_USER") ?? "clipharbor",
    Password = Secret("CLIPHARBOR_DB_PASSWORD") ?? throw new InvalidOperationException("需要配置数据库密码。")
}.ConnectionString;
builder.Services.AddSingleton(new Database(connectionString));
builder.Services.AddDataProtection().SetApplicationName("ClipHarbor.Sync").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
builder.Services.AddSingleton<Authentication>(); builder.Services.AddSingleton<SyncStore>(); builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton(provider => new Attachments(provider.GetRequiredService<Database>(), dataDirectory));
builder.Services.AddHostedService(provider => new Maintenance(provider.GetRequiredService<Database>(), provider.GetRequiredService<EventHub>(), dataDirectory, provider.GetRequiredService<ILogger<Maintenance>>()));
builder.Services.ConfigureHttpJsonOptions(options => { options.SerializerOptions.PropertyNamingPolicy = Protocol.Json.PropertyNamingPolicy; options.SerializerOptions.DefaultIgnoreCondition = Protocol.Json.DefaultIgnoreCondition; });
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.RejectionStatusCode = 429;
});
var app = builder.Build();
// Compose publishes only Caddy; the application stays on its private network.
if (Environment.GetEnvironmentVariable("CLIPHARBOR_TRUST_PROXY") == "true")
{
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, ForwardLimit = 1 };
    forwarded.KnownIPNetworks.Clear(); forwarded.KnownProxies.Clear(); app.UseForwardedHeaders(forwarded);
}
var database = app.Services.GetRequiredService<Database>(); await database.Initialize();
var auth = app.Services.GetRequiredService<Authentication>();
var accountCommand = Array.FindIndex(args, a => a is "--create-account" or "--reset-password" or "--disable-account" or "--reset-epoch");
if (accountCommand >= 0)
{
    if (accountCommand + 1 >= args.Length) throw new InvalidDataException("需要账号名称。");
    var username = args[accountCommand + 1];
    if (args[accountCommand] is "--disable-account" or "--reset-epoch")
    {
        await using var connection = await database.Open();
        if (args[accountCommand] == "--disable-account") await Database.Execute(connection, null, "UPDATE accounts SET enabled=false WHERE normalized_username=$1", username.Trim().ToUpperInvariant());
        else await Database.Execute(connection, null, "UPDATE accounts SET sync_epoch=gen_random_uuid() WHERE normalized_username=$1", username.Trim().ToUpperInvariant());
    }
    else
    {
        var passwordIndex = Array.IndexOf(args, "--password-file");
        if (passwordIndex < 0 || passwordIndex + 1 >= args.Length) throw new InvalidDataException("需要 --password-file；不接受命令行明文密码。");
        await auth.SetAccount(username, (await File.ReadAllTextAsync(args[passwordIndex + 1])).TrimEnd('\r', '\n'), args[accountCommand] == "--reset-password");
    }
    Console.WriteLine("账号操作已完成。"); return;
}
var admin = Environment.GetEnvironmentVariable("CLIPHARBOR_ADMIN_USERNAME");
var adminPassword = Secret("CLIPHARBOR_ADMIN_PASSWORD");
if (!string.IsNullOrEmpty(admin) && !string.IsNullOrEmpty(adminPassword))
{
    await using var connection = await database.Open();
    if ((long)(await Database.Scalar(connection, null, "SELECT count(*) FROM accounts"))! == 0) await auth.SetAccount(admin, adminPassword);
}
var instanceId = await database.InstanceId();
var pathBase = Environment.GetEnvironmentVariable("CLIPHARBOR_PATH_BASE");
if (!string.IsNullOrEmpty(pathBase)) app.UsePathBase(pathBase.TrimEnd('/'));
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers.CacheControl = "no-store";
    try { await next(context); }
    catch (Exception error) when (!context.Response.HasStarted)
    {
        var api = error as ApiException;
        context.Response.StatusCode = api?.Status ?? (error is InvalidDataException or FormatException or BadHttpRequestException ? 400 : 500);
        if (context.Response.StatusCode == 500) app.Logger.LogError("Request failed: {ErrorType}; request {RequestId}", error.GetType().Name, context.TraceIdentifier);
        await context.Response.WriteAsJsonAsync(new ApiError(api?.Code ?? "REQUEST_FAILED", api?.Message ?? (context.Response.StatusCode == 500 ? "服务器暂时无法处理请求。" : "请求格式无效。"), context.TraceIdentifier));
    }
});
app.UseRateLimiter(); app.UseWebSockets(new() { KeepAliveInterval = TimeSpan.FromSeconds(15) });
app.MapGet("/healthz", async () => { await using var connection = await database.Open(); await Database.Scalar(connection, null, "SELECT 1"); return Results.Ok(new { status = "ok", instanceId, commit = Environment.GetEnvironmentVariable("CLIPHARBOR_BUILD_COMMIT") ?? "development" }); });
app.MapGet("/api/v1/meta", () => new ServerMeta("ClipHarbor.Sync", instanceId, Protocol.Version, Protocol.PartBytes, Protocol.MaxFileBytes, Protocol.MaxImageBytes, Protocol.MaxBatchBytes, Protocol.QuotaBytes));
app.MapPost("/api/v1/auth/login", async (LoginRequest request) => await auth.Login(request)).RequireRateLimiting("login");
app.MapPost("/api/v1/auth/refresh", async (RefreshRequest request) => await auth.Refresh(request.RefreshToken)).RequireRateLimiting("login");
app.MapPost("/api/v1/auth/logout", async (LogoutRequest request, HttpContext context) =>
{
    if (!string.IsNullOrEmpty(request.RevokeToken)) await auth.Logout(request.RevokeToken);
    else { var session = await auth.Require(context); await using var connection = await database.Open(); await Database.Execute(connection, null, "UPDATE sessions SET revoked=true WHERE id=$1", session.SessionId); }
    return Results.NoContent();
});
app.MapGet("/api/v1/account", async (HttpContext context) =>
{
    var session = await auth.Require(context); await using var connection = await database.Open();
    var used = Convert.ToInt64(await Database.Scalar(connection, null, "SELECT coalesce(sum(byte_length),0)::bigint FROM uploads WHERE account_id=$1", session.AccountId));
    return Results.Ok(new { accountId = session.AccountId, username = session.Username, syncEpoch = session.Epoch, quotaBytes = Protocol.QuotaBytes, usedBytes = used });
});
app.MapGet("/api/v1/devices", async (HttpContext context) =>
{
    var session = await auth.Require(context); await using var connection = await database.Open(); List<object> devices = [];
    await using var command = Database.Command(connection, null, "SELECT id::text,name,last_seen FROM devices WHERE account_id=$1 ORDER BY last_seen DESC", session.AccountId);
    await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) devices.Add(new { deviceId = reader.GetString(0), name = reader.GetString(1), lastSeen = reader.GetDateTime(2) });
    return Results.Ok(devices);
});
app.MapDelete("/api/v1/devices/{id:guid}", async (Guid id, HttpContext context) => { var session = await auth.Require(context); await using var connection = await database.Open(); await Database.Execute(connection, null, "UPDATE sessions SET revoked=true WHERE account_id=$1 AND device_id=$2", session.AccountId, id); return Results.NoContent(); });
app.MapPost("/api/v1/sync/operations", async (OperationsRequest request, HttpContext context, SyncStore store, EventHub hub) => { var session = await auth.Require(context); var result = await store.Apply(session, request); hub.Notify(session.AccountId, new("history", "0", session.Epoch)); return result; });
app.MapGet("/api/v1/sync/changes", async (HttpContext context, SyncStore store, string? cursor, int? limit) => await store.Changes(await auth.Require(context), cursor ?? "0", limit ?? 100));
app.MapGet("/api/v1/sync/snapshot", async (HttpContext context, SyncStore store, string? pageToken) => await store.Snapshot(await auth.Require(context), pageToken));
app.MapPost("/api/v1/sync/clear", async (ClearRequest request, HttpContext context, SyncStore store, EventHub hub) => { var session = await auth.Require(context); await store.Clear(session, request.SyncEpoch, request.OperationId, request.BeforeRevision, request.KeepFavorites); hub.Notify(session.AccountId, new("history", "0", session.Epoch)); return Results.NoContent(); });
app.MapPost("/api/v1/clipboard/events", async (ClipboardPublish request, HttpContext context, SyncStore store, EventHub hub) => { var session = await auth.Require(context); var latest = await store.Publish(session, request); hub.Notify(session.AccountId, new("clipboard", latest.ClipboardSequence, latest.SyncEpoch)); return latest; });
app.MapGet("/api/v1/clipboard/latest", async (HttpContext context, SyncStore store) => await store.Latest(await auth.Require(context)));
app.MapPost("/api/v1/uploads", async (UploadRequest request, HttpContext context, Attachments files) => await files.Create(await auth.Require(context), request));
app.MapGet("/api/v1/uploads/{id:guid}", async (Guid id, HttpContext context, Attachments files) => await files.Status(await auth.Require(context), id));
app.MapPut("/api/v1/uploads/{id:guid}/parts/{index:int}", async (Guid id, int index, HttpContext context, Attachments files) => { await files.Put(await auth.Require(context), id, index, context.Request.Body, context.RequestAborted); return Results.NoContent(); });
app.MapPost("/api/v1/uploads/{id:guid}/complete", async (Guid id, HttpContext context, Attachments files) => await files.Complete(await auth.Require(context), id, context.RequestAborted));
app.MapDelete("/api/v1/uploads/{id:guid}", async (Guid id, HttpContext context, Attachments files) => { await files.Cancel(await auth.Require(context), id); return Results.NoContent(); });
app.MapGet("/api/v1/blobs/{id:guid}", async (Guid id, HttpContext context, Attachments files) => { var file = await files.Download(await auth.Require(context), id); return Results.File(file.Path, "application/octet-stream", enableRangeProcessing: true, entityTag: new EntityTagHeaderValue('"' + file.Hash + '"')); });
app.Map("/api/v1/events", async (HttpContext context, EventHub hub) => await hub.Connect(context, await auth.Require(context)));
await app.RunAsync();

public sealed record ClearRequest(string OperationId, string SyncEpoch, long BeforeRevision, bool KeepFavorites);
public partial class Program { }
