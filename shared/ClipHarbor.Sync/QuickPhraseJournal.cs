using Microsoft.Data.Sqlite;

namespace ClipHarbor.Sync;

public sealed class QuickPhraseJournal
{
    private readonly object gate = new();
    private readonly string connectionString;
    public QuickPhraseJournal(string directory)
    {
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "phrases.sqlite") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
         PRAGMA journal_mode=WAL;
         CREATE TABLE IF NOT EXISTS phrase_mirror(kind TEXT NOT NULL,id TEXT NOT NULL,data TEXT NOT NULL,PRIMARY KEY(kind,id));
         CREATE TABLE IF NOT EXISTS phrase_outbox(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT UNIQUE NOT NULL,kind TEXT NOT NULL,entity_id TEXT NOT NULL,data TEXT NOT NULL,sent INTEGER NOT NULL DEFAULT 0);
         CREATE TABLE IF NOT EXISTS phrase_failures(id TEXT PRIMARY KEY,data TEXT NOT NULL);
         CREATE TABLE IF NOT EXISTS phrase_state(key TEXT PRIMARY KEY,value TEXT NOT NULL);
         """; cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction? t, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value); return cmd;
    }
    private static List<T> List<T>(SqliteConnection c, SqliteTransaction? t, string sql)
    {
        using var cmd = Cmd(c,t,sql); using var reader = cmd.ExecuteReader(); List<T> values=[];
        while (reader.Read()) values.Add(Protocol.Decode<T>(reader.GetString(0))); return values;
    }
    public string State(string key, string fallback = "")
    {
        lock(gate) { using var c=Open(); using var cmd=Cmd(c,null,"SELECT value FROM phrase_state WHERE key=$k",("$k",key)); return cmd.ExecuteScalar() as string ?? fallback; }
    }
    public void SetState(string key,string value)
    {
        lock(gate) { using var c=Open(); Set(c,null,key,value); }
    }
    private static void Set(SqliteConnection c, SqliteTransaction? t, string key,string value)
    { using var cmd=Cmd(c,t,"INSERT INTO phrase_state VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v",("$k",key),("$v",value));cmd.ExecuteNonQuery(); }
    public PhraseLibrary Library()
    {
        lock(gate)
        {
            using var c=Open(); var mirror=List<PhraseEntity>(c,null,"SELECT data FROM phrase_mirror").ToDictionary(e=>(e.Kind,e.Id));
            var pending=List<PhraseOperation>(c,null,"SELECT data FROM phrase_outbox ORDER BY seq");
            foreach(var op in pending)
            {
                if(op.NewGroup is {} group) mirror[(group.Kind,group.Id)]=group;
                var entity=op.Entity.Copy(); if(op.Type=="delete")entity.DeletedAt=Protocol.Now; mirror[(entity.Kind,entity.Id)]=entity;
                if(op.Type=="delete" && entity.Kind=="group") foreach(var member in mirror.Values.Where(e=>e.Kind=="phrase"&&e.GroupId==entity.Id)) member.GroupId=null;
            }
            return new(mirror.Values.Where(e=>e.DeletedAt is null).ToList(),List<PhraseFailure>(c,null,"SELECT data FROM phrase_failures"),pending.Select(op=>op.Entity.Id).Distinct().ToList());
        }
    }
    public List<PhraseOperation> Pending()
    { lock(gate) { using var c=Open(); return List<PhraseOperation>(c,null,"SELECT data FROM phrase_outbox ORDER BY seq"); } }
    public PhraseOperation Save(PhraseEntity entity, PhraseEntity? newGroup = null, bool delete=false)
    {
        lock(gate)
        {
            entity=entity.Copy(); if(!delete)entity.Validate();
            using var c=Open();using var t=c.BeginTransaction();
            using var get=Cmd(c,t,"SELECT data,sent FROM phrase_outbox WHERE kind=$k AND entity_id=$e ORDER BY seq DESC LIMIT 1",("$k",entity.Kind),("$e",entity.Id));
            PhraseOperation? previous=null;bool sent=false;
            using(var r=get.ExecuteReader())if(r.Read()){previous=Protocol.Decode<PhraseOperation>(r.GetString(0));sent=r.GetInt32(1)!=0;}
            using var mirror=Cmd(c,t,"SELECT data FROM phrase_mirror WHERE kind=$k AND id=$e",("$k",entity.Kind),("$e",entity.Id));
            var existing=mirror.ExecuteScalar() as string;
            var type=delete?"delete":existing is null&&previous is null?"create":"update";
            var expected=entity.Revision;
            if(previous is not null&&!sent)
            {
                using var remove=Cmd(c,t,"DELETE FROM phrase_outbox WHERE id=$id",("$id",previous.OperationId));remove.ExecuteNonQuery();
                expected=previous.ExpectedRevision; type=delete?"delete":previous.Type=="create"?"create":"update";
                newGroup ??=previous.NewGroup;
                if(delete&&previous.Type=="create") { t.Commit();return previous; }
            }
            var op=new PhraseOperation(Guid.NewGuid().ToString(),type,entity,expected,newGroup);
            using var put=Cmd(c,t,"INSERT INTO phrase_outbox(id,kind,entity_id,data) VALUES($id,$k,$e,$d)",("$id",op.OperationId),("$k",entity.Kind),("$e",entity.Id),("$d",Protocol.Encode(op)));put.ExecuteNonQuery();t.Commit();return op;
        }
    }
    public void MarkSent(string id)
    { lock(gate){using var c=Open();using var cmd=Cmd(c,null,"UPDATE phrase_outbox SET sent=1 WHERE id=$id",("$id",id));cmd.ExecuteNonQuery();} }
    private static void Upsert(SqliteConnection c,SqliteTransaction t,PhraseEntity entity)
    {
        using var cmd=Cmd(c,t,"INSERT INTO phrase_mirror VALUES($k,$id,$d) ON CONFLICT(kind,id) DO UPDATE SET data=$d WHERE CAST(json_extract(phrase_mirror.data,'$.revision') AS INTEGER)<=CAST(json_extract($d,'$.revision') AS INTEGER)",("$k",entity.Kind),("$id",entity.Id),("$d",Protocol.Encode(entity)));cmd.ExecuteNonQuery();
    }
    public void Apply(List<PhraseEntity> entities,string cursor,string epoch,bool replace=false)
    {
        lock(gate)
        {
            using var c=Open();using var t=c.BeginTransaction();
            if(replace){using var clear=Cmd(c,t,"DELETE FROM phrase_mirror");clear.ExecuteNonQuery();}
            foreach(var e in entities)Upsert(c,t,e);
            Set(c,t,"cursor",cursor);Set(c,t,"epoch",epoch);t.Commit();
        }
    }
    public void Acknowledge(PhraseOperation op,PhraseResult result)
    {
        lock(gate)
        {
            using var c=Open();using var t=c.BeginTransaction();
            if(result.Entity is {} entity)Upsert(c,t,entity);
            foreach(var related in result.Related??[])Upsert(c,t,related);
            if(result.Status!="accepted" && !(result.Status=="deleted"&&op.Type=="delete"))
            {
                var failure=new PhraseFailure(op.OperationId,op,result.Code??result.Status,result.Entity);
                using var put=Cmd(c,t,"INSERT OR REPLACE INTO phrase_failures VALUES($id,$d)",("$id",op.OperationId),("$d",Protocol.Encode(failure)));put.ExecuteNonQuery();
                // Dependent edits cannot silently overwrite a conflicting/deleted version.
                foreach(var dependent in List<PhraseOperation>(c,t,"SELECT data FROM phrase_outbox ORDER BY seq").Where(p=>p.OperationId!=op.OperationId&&p.Entity.Id==op.Entity.Id&&p.Entity.Kind==op.Entity.Kind))
                {
                    var draft=new PhraseFailure(dependent.OperationId,dependent,result.Code??result.Status,result.Entity);
                    using var keep=Cmd(c,t,"INSERT OR REPLACE INTO phrase_failures VALUES($id,$d); DELETE FROM phrase_outbox WHERE id=$id",("$id",draft.Id),("$d",Protocol.Encode(draft)));keep.ExecuteNonQuery();
                }
            }
            else if(result.Entity is {} accepted)
            {
                foreach(var next in List<PhraseOperation>(c,t,"SELECT data FROM phrase_outbox WHERE sent=0 ORDER BY seq").Where(p=>p.Entity.Id==op.Entity.Id&&p.Entity.Kind==op.Entity.Kind))
                {
                    var rebased=next with{ExpectedRevision=accepted.Revision,Type=next.Type=="create"?"update":next.Type};
                    using var update=Cmd(c,t,"UPDATE phrase_outbox SET data=$d WHERE id=$id",("$d",Protocol.Encode(rebased)),("$id",next.OperationId));update.ExecuteNonQuery();
                }
            }
            using var remove=Cmd(c,t,"DELETE FROM phrase_outbox WHERE id=$id",("$id",op.OperationId));remove.ExecuteNonQuery();t.Commit();
        }
    }
    public void DismissFailure(string id)
    {lock(gate){using var c=Open();using var cmd=Cmd(c,null,"DELETE FROM phrase_failures WHERE id=$id",("$id",id));cmd.ExecuteNonQuery();}}
}
