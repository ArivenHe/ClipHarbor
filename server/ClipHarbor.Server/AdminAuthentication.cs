using System.Security.Cryptography;
using System.Text;
using ClipHarbor.Sync;

namespace ClipHarbor.Server;

public sealed record AdminLogin(string Username, string Password);
public sealed record AdminSession(Guid Id, Guid AccountId, string Username, string CsrfToken, DateTime ExpiresAt);

public sealed class AdminAuthentication(Database database, Authentication authentication)
{
    private const string CookieName = "ClipHarbor.Admin";
    private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static string Hash(string value) => Protocol.Hash(Encoding.UTF8.GetBytes(value));
    private static CookieOptions Cookie(HttpContext context) => new()
    {
        HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict,
        Path = context.Request.PathBase + "/api/v1/admin", IsEssential = true
    };
    public async Task<AdminSession> Login(HttpContext context, AdminLogin request)
    {
        CheckOrigin(context);
        var account = await authentication.VerifyCredentials(request.Username, request.Password, administrator: true);
        var token = Token(); var csrf = Token(); var id = Guid.NewGuid(); var expires = DateTime.UtcNow.AddHours(8);
        await using var connection = await database.Open();
        await using var transaction = await connection.BeginTransactionAsync();
        // Recheck under the same account lock used by password resets and account changes.
        var hash = await Database.Scalar(connection, transaction, "SELECT password_hash FROM accounts WHERE id=$1 AND enabled AND is_admin FOR UPDATE", account.Id);
        if ((string?)hash != account.PasswordHash) throw new ApiException(401, "LOGIN_FAILED", "管理员账号已变更，请重新登录。");
        if (context.Request.Cookies.TryGetValue(CookieName, out var previous))
            await Database.Execute(connection, transaction, "UPDATE admin_sessions SET revoked=true WHERE token_hash=$1", Hash(previous));
        await Database.Execute(connection, transaction, "DELETE FROM admin_sessions WHERE expires_at<now() OR revoked");
        await Database.Execute(connection, transaction, "INSERT INTO admin_sessions(id,account_id,token_hash,csrf_token,expires_at) VALUES($1,$2,$3,$4,$5)", id, account.Id, Hash(token), csrf, expires);
        await transaction.CommitAsync();
        context.Response.Cookies.Append(CookieName, token, Cookie(context));
        return new(id, account.Id, account.Username, csrf, expires);
    }
    public async Task<AdminSession> Require(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var token) || token.Length != 64)
            throw new ApiException(401, "ADMIN_SESSION_EXPIRED", "请登录管理后台。");
        await using var connection = await database.Open();
        await using var command = Database.Command(connection, null, "SELECT s.id,s.account_id,a.username,s.csrf_token,s.expires_at FROM admin_sessions s JOIN accounts a ON a.id=s.account_id WHERE s.token_hash=$1 AND NOT s.revoked AND s.expires_at>now() AND a.enabled AND a.is_admin", Hash(token));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new ApiException(401, "ADMIN_SESSION_EXPIRED", "管理员会话已失效，请重新登录。");
        var session = new AdminSession(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetDateTime(4));
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            CheckOrigin(context);
            var csrf = context.Request.Headers["X-CSRF-Token"].ToString();
            if (csrf.Length != 64 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(csrf), Encoding.UTF8.GetBytes(session.CsrfToken)))
                throw new ApiException(403, "CSRF_INVALID", "页面已过期，请刷新后重试。");
        }
        return session;
    }
    private static void CheckOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        var expected = $"{context.Request.Scheme}://{context.Request.Host}";
        if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site" ||
            (origin.Length > 0 && !string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase)))
            throw new ApiException(403, "ORIGIN_INVALID", "请在同步服务器的管理页面操作。");
    }
    public async Task Logout(HttpContext context, AdminSession session)
    {
        await using var connection = await database.Open();
        await Database.Execute(connection, null, "UPDATE admin_sessions SET revoked=true WHERE id=$1", session.Id);
        context.Response.Cookies.Delete(CookieName, Cookie(context));
    }
}
