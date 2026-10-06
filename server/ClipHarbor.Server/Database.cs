using Npgsql;
using NpgsqlTypes;

namespace ClipHarbor.Server;

public sealed class Database(string connectionString)
{
    private readonly NpgsqlDataSource _source = NpgsqlDataSource.Create(connectionString);
    public async Task<NpgsqlConnection> Open() => await _source.OpenConnectionAsync();
    public static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object?[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
            command.Parameters.Add(value is null ? new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = DBNull.Value } : new NpgsqlParameter { Value = value });
        return command;
    }
    public static async Task<int> Execute(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object?[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        return await command.ExecuteNonQueryAsync();
    }
    public static async Task<object?> Scalar(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object?[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }
    public async Task Initialize()
    {
        await using var connection = await Open();
        // Serialize schema setup across simultaneous processes.
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, transaction, "SELECT pg_advisory_xact_lock(572194136)");
        await Execute(connection, transaction, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "schema.sql")));
        var version = Convert.ToInt32(await Scalar(connection, transaction, "SELECT schema_version FROM instance WHERE singleton"));
        if (version != 2) throw new InvalidOperationException("数据库版本不兼容。");
        await transaction.CommitAsync();
    }
    public async Task<string> InstanceId()
    {
        await using var connection = await Open();
        return (await Scalar(connection, null, "SELECT id::text FROM instance WHERE singleton"))!.ToString()!;
    }
    public static async Task<(long History, long Clipboard, string Epoch)> LockAccount(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid account)
    {
        await using var command = Command(connection, transaction, "SELECT history_sequence,clipboard_sequence,sync_epoch::text FROM accounts WHERE id=$1 AND enabled FOR UPDATE", account);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new ApiException(403, "ACCOUNT_DISABLED", "账号不可用。");
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2));
    }
}

public sealed class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
