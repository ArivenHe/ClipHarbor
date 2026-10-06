namespace ClipHarbor.Server;

public static class AdminEndpoints
{
    public static void MapAdmin(this WebApplication app)
    {
        app.MapPost("/api/v1/admin/login", async (AdminLogin request, HttpContext context, AdminAuthentication auth) => View(await auth.Login(context, request))).RequireRateLimiting("login");
        var admin = app.MapGroup("/api/v1/admin");
        admin.AddEndpointFilter(async (context, next) =>
        {
            var auth = context.HttpContext.RequestServices.GetRequiredService<AdminAuthentication>();
            context.HttpContext.Items["admin"] = await auth.Require(context.HttpContext);
            return await next(context);
        });
        static AdminSession Actor(HttpContext context) => (AdminSession)context.Items["admin"]!;
        admin.MapGet("/me", (HttpContext context) => View(Actor(context)));
        admin.MapPost("/logout", async (HttpContext context, AdminAuthentication auth) => { await auth.Logout(context, Actor(context)); return Results.NoContent(); });
        admin.MapGet("/overview", async (AdminStore store) => await store.Overview());
        admin.MapGet("/users", async (AdminStore store, string? search, string? status, int? page) => await store.Users(search, status, page ?? 1));
        admin.MapGet("/users/{id:guid}", async (Guid id, AdminStore store) => await store.User(id));
        admin.MapPost("/users", async (CreateUser request, HttpContext context, AdminStore store) =>
        {
            var id = await store.Create(Actor(context), request);
            return Results.Created(context.Request.PathBase + "/api/v1/admin/users/" + id, await store.User(id));
        });
        admin.MapPatch("/users/{id:guid}", async (Guid id, UpdateUser request, HttpContext context, AdminStore store) => { await store.Update(Actor(context), id, request); return await store.User(id); });
        admin.MapPost("/users/{id:guid}/password", async (Guid id, ResetPassword request, HttpContext context, AdminStore store) => { await store.Password(Actor(context), id, request.Password); return Results.NoContent(); });
        admin.MapGet("/users/{id:guid}/devices", async (Guid id, AdminStore store) => await store.Devices(id));
        admin.MapDelete("/users/{id:guid}/sessions", async (Guid id, HttpContext context, AdminStore store) => { await store.RevokeSessions(Actor(context), id); return Results.NoContent(); });
        admin.MapDelete("/users/{id:guid}/devices/{device:guid}/session", async (Guid id, Guid device, HttpContext context, AdminStore store) => { await store.RevokeSessions(Actor(context), id, device); return Results.NoContent(); });
        admin.MapGet("/audit", async (AdminStore store) => await store.AuditLog());
        app.MapGet("/admin", (HttpContext context) => context.Request.Path.Value!.EndsWith('/')
            ? Results.File(Path.Combine(app.Environment.WebRootPath, "admin", "index.html"), "text/html; charset=utf-8")
            : Results.Redirect(context.Request.PathBase + "/admin/"));
    }
    private static object View(AdminSession session) => new { accountId = session.AccountId, username = session.Username, csrfToken = session.CsrfToken, expiresAt = session.ExpiresAt };
}
