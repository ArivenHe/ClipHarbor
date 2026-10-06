using System.Security.Cryptography;
using System.Text;
using ClipHarbor.Sync;
using Konscious.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace ClipHarbor.Server;

public static class Passwords
{
    public static async Task<string> Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return $"v1${Convert.ToBase64String(salt)}${Convert.ToBase64String(await Derive(password, salt))}";
    }
    public static async Task<bool> Verify(string password, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 3 || parts[0] != "v1") return false;
        var actual = await Derive(password, Convert.FromBase64String(parts[1]));
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(parts[2]));
    }
    private static async Task<byte[]> Derive(string password, byte[] salt)
    {
        if (Encoding.UTF8.GetByteCount(password) is < 1 or > 1024) throw new ApiException(400, "PASSWORD_SIZE", "密码长度无效。");
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password)) { Salt = salt, MemorySize = 19456, Iterations = 2, DegreeOfParallelism = 1 };
        return await argon.GetBytesAsync(32);
    }
}

public sealed record AuthSession(Guid AccountId, Guid DeviceId, Guid SessionId, string Username, string Epoch, DateTime AccessExpires);
public sealed record AccountCredentials(Guid Id, string Username, string PasswordHash);

public sealed class Authentication(Database database, IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("ClipHarbor.RefreshRetry.v1");
    private string? _dummyHash;
    private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string TokenHash(string value) => Protocol.Hash(Encoding.UTF8.GetBytes(value));
    public async Task<AccountCredentials> VerifyCredentials(string? username, string? password, bool administrator = false)
    {
        if (username is null || password is null || username.Trim().Length is < 1 or > 100)
            throw new ApiException(400, "LOGIN_INVALID", "账号格式无效。");
        await using var connection = await database.Open();
        AccountCredentials? account = null;
        await using (var command = Database.Command(connection, null, "SELECT id,username,password_hash FROM accounts WHERE normalized_username=$1 AND enabled AND (NOT $2 OR is_admin)", username.Trim().ToUpperInvariant(), administrator))
        await using (var reader = await command.ExecuteReaderAsync())
            if (await reader.ReadAsync()) account = new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2));
        _dummyHash ??= await Passwords.Hash("ClipHarbor authentication timing placeholder");
        var valid = await Passwords.Verify(password, account?.PasswordHash ?? _dummyHash);
        if (account is null || !valid) throw new ApiException(401, "LOGIN_FAILED", administrator ? "管理员账号或密码错误。" : "账号或密码错误。");
        return account;
    }
    public async Task<SessionTokens> Login(LoginRequest request)
    {
        if (request.Username is null || request.Password is null || request.DeviceName is null || request.Username.Trim().Length is < 1 or > 100 || request.DeviceName.Length is < 1 or > 100 || !Guid.TryParse(request.DeviceId, out var device)) throw new ApiException(400, "LOGIN_INVALID", "账号或设备名称格式无效。");
        await using var connection = await database.Open();
        var credentials = await VerifyCredentials(request.Username, request.Password);
        var account = credentials.Id;
        await using var transaction = await connection.BeginTransactionAsync();
        var state = await Database.LockAccount(connection, transaction, account);
        if ((string?)await Database.Scalar(connection, transaction, "SELECT password_hash FROM accounts WHERE id=$1", account) != credentials.PasswordHash)
            throw new ApiException(401, "LOGIN_FAILED", "密码已变更，请重新登录。");
        var tokens = NewTokens(account, credentials.Username, device, Guid.NewGuid(), state.Epoch, DateTime.UtcNow.AddDays(30));
        await Database.Execute(connection, transaction, "INSERT INTO devices(account_id,id,name) VALUES($1,$2,$3) ON CONFLICT(account_id,id) DO UPDATE SET name=$3,last_seen=now()", account, device, request.DeviceName);
        await Database.Execute(connection, transaction, "UPDATE sessions SET revoked=true WHERE account_id=$1 AND device_id=$2", account, device);
        await Database.Execute(connection, transaction, "INSERT INTO sessions(id,account_id,device_id,access_hash,refresh_hash,revoke_hash,access_expires,refresh_expires) VALUES($1,$2,$3,$4,$5,$6,$7,$8)", Guid.Parse(tokens.SessionId), account, device, TokenHash(tokens.AccessToken), TokenHash(tokens.RefreshToken), TokenHash(tokens.RevokeToken), DateTime.Parse(tokens.AccessExpiresAt).ToUniversalTime(), DateTime.Parse(tokens.RefreshExpiresAt).ToUniversalTime());
        await transaction.CommitAsync();
        return tokens;
    }
    private static SessionTokens NewTokens(Guid account, string username, Guid device, Guid session, string epoch, DateTime refreshExpires) =>
        new(account.ToString(), username, device.ToString(), session.ToString(), Token(), Token(), DateTime.UtcNow.AddMinutes(15).ToString("O"), refreshExpires.ToString("O"), Token(), epoch);

    public async Task<AuthSession> Require(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length > 128) throw new ApiException(401, "SESSION_EXPIRED", "请重新登录。");
        await using var connection = await database.Open();
        AuthSession session;
        await using (var command = Database.Command(connection, null, "SELECT s.account_id,s.device_id,s.id,a.username,a.sync_epoch::text,s.access_expires FROM sessions s JOIN accounts a ON a.id=s.account_id WHERE s.access_hash=$1 AND NOT s.revoked AND a.enabled AND s.access_expires>now() AND s.refresh_expires>now()", TokenHash(header[7..])))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new ApiException(401, "SESSION_EXPIRED", "请重新登录。");
            session = new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.GetDateTime(5));
        }
        await Database.Execute(connection, null, "UPDATE devices SET last_seen=now() WHERE account_id=$1 AND id=$2 AND last_seen<now()-interval '1 minute'", session.AccountId, session.DeviceId);
        return session;
    }
    public async Task<bool> Valid(AuthSession session)
    {
        await using var connection = await database.Open();
        return (bool?)await Database.Scalar(connection, null, "SELECT EXISTS(SELECT 1 FROM sessions s JOIN accounts a ON a.id=s.account_id WHERE s.id=$1 AND NOT s.revoked AND a.enabled AND s.access_expires>now() AND s.refresh_expires>now())", session.SessionId) == true;
    }
    public async Task<SessionTokens> Refresh(string refreshToken)
    {
        if (refreshToken.Length > 128) throw new ApiException(401, "SESSION_EXPIRED", "请重新登录。");
        var hash = TokenHash(refreshToken);
        await using var connection = await database.Open();
        await using var transaction = await connection.BeginTransactionAsync();
        SessionTokens tokens; Guid id; string currentHash, revokeHash; string? cached = null;
        await using (var command = Database.Command(connection, transaction, "SELECT s.id,s.account_id,s.device_id,a.username,a.sync_epoch::text,s.refresh_expires,s.refresh_hash,s.revoke_hash,CASE WHEN s.previous_refresh_hash=$1 AND s.previous_refresh_until>now() THEN s.previous_response END FROM sessions s JOIN accounts a ON a.id=s.account_id WHERE (s.refresh_hash=$1 OR s.previous_refresh_hash=$1) AND NOT s.revoked AND a.enabled AND s.refresh_expires>now() FOR UPDATE OF s", hash))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new ApiException(401, "SESSION_EXPIRED", "请重新登录。");
            id = reader.GetGuid(0); currentHash = reader.GetString(6); revokeHash = reader.GetString(7);
            if (!reader.IsDBNull(8)) cached = reader.GetString(8);
            tokens = NewTokens(reader.GetGuid(1), reader.GetString(3), reader.GetGuid(2), id, reader.GetString(4), reader.GetDateTime(5));
        }
        if (cached is not null) return Protocol.Decode<SessionTokens>(_protector.Unprotect(cached));
        if (currentHash != hash) throw new ApiException(401, "SESSION_EXPIRED", "请重新登录。");
        // Revoke credential rotates with refresh; the client persists the entire response atomically.
        await Database.Execute(connection, transaction, "UPDATE sessions SET access_hash=$2,refresh_hash=$3,revoke_hash=$4,access_expires=$5,previous_refresh_hash=$6,previous_refresh_until=now()+interval '30 seconds',previous_response=$7 WHERE id=$1", id, TokenHash(tokens.AccessToken), TokenHash(tokens.RefreshToken), TokenHash(tokens.RevokeToken), DateTime.Parse(tokens.AccessExpiresAt).ToUniversalTime(), currentHash, _protector.Protect(Protocol.Encode(tokens)));
        await transaction.CommitAsync();
        return tokens;
    }
    public async Task Logout(string revokeToken)
    {
        await using var connection = await database.Open();
        await Database.Execute(connection, null, "UPDATE sessions SET revoked=true WHERE revoke_hash=$1", TokenHash(revokeToken));
    }
    public async Task SetAccount(string username, string password, bool reset = false, bool administrator = false)
    {
        if (username.Trim().Length is < 1 or > 100) throw new InvalidDataException("账号名称长度无效。");
        await using var connection = await database.Open();
        await using var transaction = await connection.BeginTransactionAsync();
        var hash = await Passwords.Hash(password);
        if (reset)
        {
            var id = await Database.Scalar(connection, transaction, "UPDATE accounts SET password_hash=$2 WHERE normalized_username=$1 RETURNING id", username.Trim().ToUpperInvariant(), hash);
            if (id is null) throw new InvalidDataException("账号不存在。");
            await Database.Execute(connection, transaction, "UPDATE sessions SET revoked=true WHERE account_id=$1", id);
            await Database.Execute(connection, transaction, "UPDATE admin_sessions SET revoked=true WHERE account_id=$1", id);
        }
        else await Database.Execute(connection, transaction, "INSERT INTO accounts(id,username,normalized_username,password_hash,is_admin) VALUES($1,$2,$3,$4,$5)", Guid.NewGuid(), username.Trim(), username.Trim().ToUpperInvariant(), hash, administrator);
        await transaction.CommitAsync();
    }
}
