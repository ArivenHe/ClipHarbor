using ClipHarbor.Sync;
using Npgsql;

namespace ClipHarbor.Server;

public sealed record CreateUser(string Username, string Password, bool IsAdmin = false);
public sealed record UpdateUser(bool Enabled, bool IsAdmin);
public sealed record ResetPassword(string Password);
public sealed record ManagedUser(Guid Id, string Username, bool Enabled, bool IsAdmin, DateTime CreatedAt,
    long RecordCount, long DeviceCount, long ActiveSessions, long UsedBytes, long QuotaBytes, DateTime? LastSeen);
public sealed record ManagedDevice(Guid Id, string Name, DateTime LastSeen, long ActiveSessions);

public sealed class AdminStore(Database database, EventHub hub)
{
    private const long ManagementLock = 572194137;
    private const string UserSelect = """
        SELECT a.id,a.username,a.enabled,a.is_admin,a.created_at,
          (SELECT count(*) FROM records r WHERE r.account_id=a.id AND NOT r.deleted),
          (SELECT count(*) FROM devices d WHERE d.account_id=a.id),
          (SELECT count(*) FROM sessions s WHERE s.account_id=a.id AND NOT s.revoked AND s.refresh_expires>now()),
          ((SELECT coalesce(sum(byte_length),0) FROM uploads u WHERE u.account_id=a.id)+
           (SELECT coalesce(sum(octet_length(data::text)),0) FROM records r WHERE r.account_id=a.id AND NOT r.deleted))::bigint,
          (SELECT max(last_seen) FROM devices d WHERE d.account_id=a.id)
        FROM accounts a
        """;
    private static ManagedUser ReadUser(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetDateTime(4),
        reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), Protocol.QuotaBytes, reader.IsDBNull(9) ? null : reader.GetDateTime(9));

    public async Task EnsureAdministrator(string username, string? password)
    {
        await using var connection = await database.Open();
        await using var transaction = await connection.BeginTransactionAsync();
        await Database.Execute(connection, transaction, "SELECT pg_advisory_xact_lock($1)", ManagementLock);
        if ((long)(await Database.Scalar(connection, transaction, "SELECT count(*) FROM accounts WHERE is_admin"))! > 0) return;
        var id = await Database.Scalar(connection, transaction, "SELECT id FROM accounts WHERE normalized_username=$1", username.Trim().ToUpperInvariant());
        if (id is not null) await Database.Execute(connection, transaction, "UPDATE accounts SET is_admin=true WHERE id=$1", id);
        else if ((long)(await Database.Scalar(connection, transaction, "SELECT count(*) FROM accounts"))! == 0 && !string.IsNullOrEmpty(password))
            await Database.Execute(connection, transaction, "INSERT INTO accounts(id,username,normalized_username,password_hash,is_admin) VALUES($1,$2,$3,$4,true)", Guid.NewGuid(), username.Trim(), username.Trim().ToUpperInvariant(), await Passwords.Hash(password));
        else throw new InvalidOperationException("初始管理员不存在；请配置已有账号名称或使用 --create-admin 创建管理员。");
        await transaction.CommitAsync();
    }
    public async Task<object> Overview()
    {
        await using var connection = await database.Open();
        await using var command = Database.Command(connection, null, """
            SELECT (SELECT count(*) FROM accounts),(SELECT count(*) FROM accounts WHERE enabled),
              (SELECT count(*) FROM devices),(SELECT count(*) FROM records WHERE NOT deleted),
              ((SELECT coalesce(sum(byte_length),0) FROM uploads)+(SELECT coalesce(sum(octet_length(data::text)),0) FROM records WHERE NOT deleted))::bigint
            """);
        await using var reader = await command.ExecuteReaderAsync(); await reader.ReadAsync();
        return new { users = reader.GetInt64(0), enabledUsers = reader.GetInt64(1), devices = reader.GetInt64(2), records = reader.GetInt64(3), usedBytes = reader.GetInt64(4) };
    }
    public async Task<object> Users(string? search, string? status, int page)
    {
        if (page is < 1 or > 100000 || search?.Length > 100 || status is not (null or "all" or "enabled" or "disabled"))
            throw new ApiException(400, "FILTER_INVALID", "筛选条件无效。");
        await using var connection = await database.Open();
        var term = (search ?? "").Trim(); var filter = status ?? "all";
        const string where = " WHERE strpos(lower(a.username),lower($1))>0 AND ($2='all' OR a.enabled=($2='enabled'))";
        var total = (long)(await Database.Scalar(connection, null, "SELECT count(*) FROM accounts a" + where, term, filter))!;
        List<ManagedUser> users = [];
        await using var command = Database.Command(connection, null, UserSelect + where + " ORDER BY a.created_at DESC,a.id LIMIT 25 OFFSET $3", term, filter, (page - 1) * 25);
        await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) users.Add(ReadUser(reader));
        return new { items = users, total, page, pageSize = 25 };
    }
    public async Task<ManagedUser> User(Guid id)
    {
        await using var connection = await database.Open();
        await using var command = Database.Command(connection, null, UserSelect + " WHERE a.id=$1", id);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw Missing();
        return ReadUser(reader);
    }
    public async Task<List<ManagedDevice>> Devices(Guid id)
    {
        _ = await User(id);
        await using var connection = await database.Open(); List<ManagedDevice> devices = [];
        await using var command = Database.Command(connection, null, "SELECT d.id,d.name,d.last_seen,(SELECT count(*) FROM sessions s WHERE s.account_id=d.account_id AND s.device_id=d.id AND NOT s.revoked AND s.refresh_expires>now()) FROM devices d WHERE d.account_id=$1 ORDER BY last_seen DESC", id);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) devices.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetDateTime(2), reader.GetInt64(3)));
        return devices;
    }
    public async Task<Guid> Create(AdminSession actor, CreateUser request)
    {
        var username = request.Username?.Trim();
        if (string.IsNullOrEmpty(username) || username.Length > 100 || username.Any(char.IsControl)) throw new ApiException(400, "USERNAME_INVALID", "用户名需为 1–100 个字符，不能包含控制字符。");
        var hash = await PasswordHash(request.Password);
        await using var connection = await database.Open(); await using var transaction = await connection.BeginTransactionAsync();
        await Authorize(connection, transaction, actor);
        if (await Database.Scalar(connection, transaction, "SELECT id FROM accounts WHERE normalized_username=$1", username.ToUpperInvariant()) is not null)
            throw new ApiException(409, "USERNAME_EXISTS", "该用户名已存在。");
        var id = Guid.NewGuid();
        await Database.Execute(connection, transaction, "INSERT INTO accounts(id,username,normalized_username,password_hash,is_admin) VALUES($1,$2,$3,$4,$5)", id, username, username.ToUpperInvariant(), hash, request.IsAdmin);
        await Audit(connection, transaction, actor.AccountId, id, request.IsAdmin ? "create_admin" : "create_user");
        await transaction.CommitAsync(); return id;
    }
    public async Task Update(AdminSession actor, Guid id, UpdateUser request)
    {
        await using var connection = await database.Open(); await using var transaction = await connection.BeginTransactionAsync();
        await Authorize(connection, transaction, actor); await LockUser(connection, transaction, id);
        if (id == actor.AccountId && (!request.Enabled || !request.IsAdmin)) throw new ApiException(409, "SELF_LOCKOUT", "不能禁用自己或取消自己的管理员权限。");
        if (!request.Enabled || !request.IsAdmin)
        {
            var remaining = (long)(await Database.Scalar(connection, transaction, "SELECT count(*) FROM accounts WHERE enabled AND is_admin AND id<>$1", id))!;
            if (remaining == 0) throw new ApiException(409, "LAST_ADMIN", "至少需要保留一位启用的管理员。");
        }
        await Database.Execute(connection, transaction, "UPDATE accounts SET enabled=$2,is_admin=$3 WHERE id=$1", id, request.Enabled, request.IsAdmin);
        if (!request.Enabled) await Revoke(connection, transaction, id);
        else if (!request.IsAdmin) await Database.Execute(connection, transaction, "UPDATE admin_sessions SET revoked=true WHERE account_id=$1", id);
        await Audit(connection, transaction, actor.AccountId, id, request.Enabled ? (request.IsAdmin ? "enable_admin" : "enable_user") : "disable_user");
        await transaction.CommitAsync(); Wake(id);
    }
    public async Task Password(AdminSession actor, Guid id, string password)
    {
        var hash = await PasswordHash(password);
        await using var connection = await database.Open(); await using var transaction = await connection.BeginTransactionAsync();
        await Authorize(connection, transaction, actor); await LockUser(connection, transaction, id);
        await Database.Execute(connection, transaction, "UPDATE accounts SET password_hash=$2 WHERE id=$1", id, hash);
        await Revoke(connection, transaction, id); await Audit(connection, transaction, actor.AccountId, id, "reset_password");
        await transaction.CommitAsync(); Wake(id);
    }
    public async Task RevokeSessions(AdminSession actor, Guid id, Guid? device = null)
    {
        await using var connection = await database.Open(); await using var transaction = await connection.BeginTransactionAsync();
        await Authorize(connection, transaction, actor); await LockUser(connection, transaction, id);
        if (device is { } deviceId)
        {
            if (await Database.Scalar(connection, transaction, "SELECT 1 FROM devices WHERE account_id=$1 AND id=$2", id, deviceId) is null) throw new ApiException(404, "DEVICE_NOT_FOUND", "该用户下没有此设备。");
            await Database.Execute(connection, transaction, "UPDATE sessions SET revoked=true WHERE account_id=$1 AND device_id=$2", id, deviceId);
        }
        else await Database.Execute(connection, transaction, "UPDATE sessions SET revoked=true WHERE account_id=$1", id);
        await Audit(connection, transaction, actor.AccountId, id, device is null ? "revoke_all_devices" : "revoke_device");
        await transaction.CommitAsync(); Wake(id);
    }
    public async Task<object> AuditLog()
    {
        await using var connection = await database.Open(); List<object> entries = [];
        await using var command = Database.Command(connection, null, "SELECT l.id,a.username,t.username,l.action,l.created_at FROM admin_audit l JOIN accounts a ON a.id=l.actor_id JOIN accounts t ON t.id=l.target_id ORDER BY l.id DESC LIMIT 100");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) entries.Add(new { id = reader.GetInt64(0), actor = reader.GetString(1), target = reader.GetString(2), action = reader.GetString(3), createdAt = reader.GetDateTime(4) });
        return entries;
    }
    private static async Task Authorize(NpgsqlConnection connection, NpgsqlTransaction transaction, AdminSession actor)
    {
        await Database.Execute(connection, transaction, "SELECT pg_advisory_xact_lock($1)", ManagementLock);
        if ((bool?)await Database.Scalar(connection, transaction, "SELECT EXISTS(SELECT 1 FROM accounts a JOIN admin_sessions s ON s.account_id=a.id WHERE a.id=$1 AND a.enabled AND a.is_admin AND s.id=$2 AND NOT s.revoked AND s.expires_at>now())", actor.AccountId, actor.Id) != true)
            throw new ApiException(401, "ADMIN_SESSION_EXPIRED", "管理员会话已失效，请重新登录。");
    }
    private static async Task LockUser(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id)
    {
        if (await Database.Scalar(connection, transaction, "SELECT id FROM accounts WHERE id=$1 FOR UPDATE", id) is null) throw Missing();
    }
    private static ApiException Missing() => new(404, "USER_NOT_FOUND", "用户不存在。");
    private static Task Audit(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid actor, Guid target, string action) => Database.Execute(connection, transaction, "INSERT INTO admin_audit(actor_id,target_id,action) VALUES($1,$2,$3)", actor, target, action);
    private static async Task<string> PasswordHash(string? password)
    {
        if (password is null || password.Length < 8) throw new ApiException(400, "PASSWORD_SIZE", "密码至少需要 8 个字符。");
        return await Passwords.Hash(password);
    }
    private static async Task Revoke(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id)
    {
        await Database.Execute(connection, transaction, "UPDATE sessions SET revoked=true WHERE account_id=$1", id);
        await Database.Execute(connection, transaction, "UPDATE admin_sessions SET revoked=true WHERE account_id=$1", id);
    }
    private void Wake(Guid id) => hub.Revalidate(id);
}
