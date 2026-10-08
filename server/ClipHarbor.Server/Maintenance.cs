using ClipHarbor.Sync;

namespace ClipHarbor.Server;

public sealed class Maintenance(Database database, EventHub events, string root, ILogger<Maintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Sweep(stoppingToken); }
            catch (Exception error) when (error is not OperationCanceledException) { logger.LogWarning("Maintenance retry needed: {ErrorType}", error.GetType().Name); }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
    internal async Task Sweep(CancellationToken token)
    {
        await using var connection = await database.Open(); List<Guid> accounts = [];
        await using (var command = Database.Command(connection, null, "SELECT id FROM accounts WHERE enabled"))
        await using (var reader = await command.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token)) accounts.Add(reader.GetGuid(0));
        foreach (var account in accounts)
        {
            token.ThrowIfCancellationRequested();
            await using var transaction = await connection.BeginTransactionAsync(token);
            var state = await Database.LockAccount(connection, transaction, account); var sequence = state.History;
            List<WireRecord> expired = [];
            await using (var command = Database.Command(connection, transaction, "SELECT data::text FROM records WHERE account_id=$1 AND NOT deleted AND NOT (data->>'favorite')::boolean AND (data->>'lastCapturedAt')::timestamptz<now()-interval '30 days' LIMIT 500", account))
            await using (var reader = await command.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token)) expired.Add(Protocol.Decode<WireRecord>(reader.GetString(0)));
            foreach (var record in expired) { record.DeletedAt = Protocol.Now; await SyncStore.Write(connection, transaction, account, record, ++sequence); }
            await Database.Execute(connection, transaction, "UPDATE accounts SET history_sequence=$2 WHERE id=$1", account, sequence);
            List<Guid> abandoned = [];
            await using (var command = Database.Command(connection, transaction, "DELETE FROM uploads u WHERE account_id=$1 AND created_at<now()-interval '24 hours' AND NOT EXISTS(SELECT 1 FROM records r WHERE r.account_id=u.account_id AND NOT r.deleted AND (r.data->>'blobId'=u.id::text OR r.data->'files' @> jsonb_build_array(jsonb_build_object('blobId',u.id::text)))) RETURNING id", account))
            await using (var reader = await command.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token)) abandoned.Add(reader.GetGuid(0));
            await Database.Execute(connection, transaction, "DELETE FROM changes WHERE account_id=$1 AND created_at<now()-interval '90 days' AND sequence<$2", account, sequence);
            await Database.Execute(connection, transaction, "DELETE FROM clipboard_events WHERE account_id=$1 AND created_at<now()-interval '30 days' AND sequence<$2", account, state.Clipboard);
            await Database.Execute(connection, transaction, "DELETE FROM quick_phrase_changes WHERE account_id=$1 AND created_at<now()-interval '90 days' AND sequence<(SELECT phrase_sequence FROM accounts WHERE id=$1)", account);
            await transaction.CommitAsync(token);
            foreach (var id in abandoned) { var directory = Path.Combine(root, "attachments", account.ToString(), id.ToString()); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            if (expired.Count > 0) events.Notify(account, new("history", "0", state.Epoch));
        }
        await Database.Execute(connection, null, "DELETE FROM quick_phrase_snapshots WHERE expires_at<now(); DELETE FROM snapshots WHERE expires_at<now(); DELETE FROM sessions WHERE refresh_expires<now()-interval '30 days'");
    }
}
