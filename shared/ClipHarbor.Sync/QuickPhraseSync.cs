namespace ClipHarbor.Sync;

public sealed class QuickPhraseSync : IAsyncDisposable
{
    private readonly SyncApi api;
    private readonly SyncConfig config;
    private readonly string instance;
    private readonly CancellationTokenSource life=new();
    private readonly SemaphoreSlim gate=new(1,1), noticeGate=new(1,1);
    private Task? run;
    private bool blocked;
    private string lastNotice = "";
    public QuickPhraseJournal Journal { get; }
    public bool Supported { get; private set; }
    public string Status { get; private set; }="个人短语待同步";
    public Func<PhraseLibrary,Task> Changed { get; set; }=_=>Task.CompletedTask;
    public QuickPhraseSync(SyncApi api,SyncConfig config,string directory,string instance)
    {
        this.api=api;this.config=config;this.instance=instance;Journal=new(directory);
        Supported=api.Metadata?.Features?.QuickPhrases==1 || Journal.State("supported")=="1";
        if (!config.Enabled || !config.SyncPhrases) Status="短语同步已暂停";
    }
    public PhraseLibrary Library()=>Journal.Library() with{Status=Status,Supported=Supported&&!blocked};
    public void Start(){run??=Run();}
    public async Task Save(PhraseEntity entity,PhraseEntity? newGroup=null,bool delete=false)
    {
        if(!Supported||blocked)throw new InvalidDataException(blocked?"账号已失效或数据空间变化，请重新登录；草稿已保留。":"服务器暂不支持快捷短语，请升级服务器。");
        Journal.Save(entity,newGroup,delete);Status="个人短语待同步";await Notify();
    }
    public async Task Resolve(string failureId,string choice)
    {
        var failure=Journal.Library().Failures.SingleOrDefault(f=>f.Id==failureId)??throw new InvalidDataException("草稿已不存在。");
        if(choice=="server"){Journal.DismissFailure(failureId);await Notify();return;}
        var entity=failure.Operation.Entity.Copy();
        if(choice=="copy")
        {
            if(entity.Kind!="phrase")throw new InvalidDataException("仅正文可以另存为个人短语。");
            entity.Id=Guid.NewGuid().ToString();entity.Revision="0";entity.OriginPresetId=null;entity.OriginPresetVersion=null;entity.Alias=null;entity.DeletedAt=null;entity.Title=string.Concat(entity.Title.EnumerateRunes().Take(77))+" 副本";
        }
        else if(choice=="mine")
        {
            if(failure.Server is not {DeletedAt:null} server||server.Id!=entity.Id)throw new InvalidDataException("服务端已删除或存在另一个个人版本，请另存副本。");
            var latest=Journal.Library().Entities.FirstOrDefault(e=>e.Kind==server.Kind&&e.Id==server.Id)??server;
            entity.Revision=latest.Revision;
        }
        else throw new InvalidDataException("草稿处理方式无效。");
        var newGroup = failure.Operation.NewGroup;
        if (choice == "copy" && entity.GroupId is { } groupId && !Journal.Library().Entities.Any(e => e.Kind == "group" && e.Id == groupId)) { entity.GroupId = null; newGroup = null; }
        await Save(entity,newGroup);Journal.DismissFailure(failureId);await Notify();
    }
    private async Task Notify()
    {
        await noticeGate.WaitAsync();
        try
        {
        var library = Library();
        var notice = Protocol.Hash(System.Text.Encoding.UTF8.GetBytes(Protocol.Encode(library)));
        if (notice == lastNotice) return;
        await Changed(library); lastNotice = notice;
        }
        finally { noticeGate.Release(); }
    }
    private async Task Run()
    {
        var first = true;
        while(!life.IsCancellationRequested)
        {
            if((first || config.Enabled&&config.SyncPhrases)&&!blocked)
            {
                first = false;
                try{await SyncNow(life.Token);}
                catch(Exception error)when(error is not OperationCanceledException)
                {
                    if(error is SyncHttpException {Status:401 or 403}){blocked=true;Status="登录已失效，请重新登录";}
                    else Status="短语等待同步："+error.Message;
                    await Notify();
                }
            }
            await Task.Delay(1000,life.Token);
        }
    }
    public async Task SyncNow(CancellationToken token=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,life.Token);token=linked.Token;
        await gate.WaitAsync(token);
        try
        {
            if(blocked)return;
            var metadata=api.Metadata??await api.Test(token);
            if(metadata.InstanceId!=instance){blocked=true;Status="服务器实例变化，请重新登录；草稿已保留。";await Notify();return;}
            Supported=metadata.Features?.QuickPhrases==1;Journal.SetState("supported",Supported?"1":"0");
            if(!Supported){Status="服务器暂不支持快捷短语，请升级服务器。";await Notify();return;}
            if(!config.Enabled||!config.SyncPhrases){Status="短语同步已暂停";await Notify();return;}
            await Pull(token);
            foreach(var pending in Journal.Pending())
            {
                token.ThrowIfCancellationRequested();
                if(!config.Enabled||!config.SyncPhrases)break;
                // Refresh after prior acknowledgements have rebased dependent edits.
                var op=Journal.Pending().FirstOrDefault(p=>p.OperationId==pending.OperationId);if(op is null)continue;
                Journal.MarkSent(op.OperationId);
                var response=await api.Post<PhraseOperationsResponse>("phrases/operations",new PhraseOperationsRequest(api.Session!.SyncEpoch,[op]),token);
                var result=response.Results.SingleOrDefault(r=>r.OperationId==op.OperationId)??throw new InvalidDataException("短语操作响应无效。");
                Journal.Acknowledge(op,result);
            }
            await Pull(token);
            var library=Journal.Library();Status=library.Failures.Count>0?$"{library.Failures.Count} 份短语草稿需要处理":library.PendingIds.Count>0?"个人短语待同步":"个人短语已同步";
            await Notify();
        }
        catch(SyncHttpException error)when(error.Code=="EPOCH_CHANGED")
        {blocked=true;Status="账号空间变化，请重新登录；原草稿已保留。";await Notify();}
        finally{gate.Release();}
    }
    private void Epoch(string epoch)
    {
        if(epoch!=api.Session!.SyncEpoch){blocked=true;throw new SyncHttpException(409,"EPOCH_CHANGED","账号空间已变化。");}
        var previous=Journal.State("epoch");if(previous!=""&&previous!=epoch){blocked=true;throw new SyncHttpException(409,"EPOCH_CHANGED","本机空间已变化。");}
    }
    private async Task Pull(CancellationToken token)
    {
        while(true)
        {
            PhraseChanges changes;
            try{changes=await api.Get<PhraseChanges>($"phrases/changes?after={Journal.State("cursor","0")}",token);}
            catch(SyncHttpException error)when(error.Code=="CURSOR_EXPIRED")
            {
                var snapshot=await api.Post<PhraseSnapshot>("phrases/snapshots",new{},token);Epoch(snapshot.SyncEpoch);
                List<PhraseEntity> entities=[];int page=0;
                while(true)
                {
                    var part=await api.Get<PhraseSnapshotPage>($"phrases/snapshots/{snapshot.Token}?page={page++}",token);Epoch(part.SyncEpoch);
                    if(part.Cursor!=snapshot.Cursor)throw new InvalidDataException("快照游标变化。");entities.AddRange(part.Entities);if(!part.HasMore)break;
                }
                Journal.Apply(entities,snapshot.Cursor,snapshot.SyncEpoch,true);continue;
            }
            Epoch(changes.SyncEpoch);Journal.Apply(changes.Entities,changes.Cursor,changes.SyncEpoch);
            if(!changes.HasMore)break;
        }
    }
    public async ValueTask DisposeAsync()
    {
        life.Cancel();if(run is not null)try{await run;}catch(OperationCanceledException){}
        await gate.WaitAsync();gate.Release();gate.Dispose();life.Dispose();
    }
}
