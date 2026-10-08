using ClipHarbor.Sync;

static class QuickPhraseIntegration
{
    public static async Task Run(SyncApi a,SyncApi b,SyncApi outsider,Action<bool,string> check,Func<Func<Task>,int,Task> expect)
    {
        async Task<PhraseResult> Apply(SyncApi api,PhraseOperation op)=>(await api.Post<PhraseOperationsResponse>("phrases/operations",new PhraseOperationsRequest(api.Session!.SyncEpoch,[op]))).Results.Single();
        PhraseOperation Op(PhraseEntity e,string type="create",string revision="0",PhraseEntity? group=null)=>new(Guid.NewGuid().ToString(),type,e,revision,group);
        var metadata=await a.Test();check(metadata.Features?.QuickPhrases==1,"phrase capability advertised");
        var group=new PhraseEntity{Kind="group",Title="工作 "+Guid.NewGuid()};
        var personal=new PhraseEntity{Title="确认收到",Body="  我的回复\n    代码\n\n",Alias="ack_"+Guid.NewGuid().ToString("N")[..8],OriginPresetId="communication.received",OriginPresetVersion=1};
        var create=Op(personal,group:group);var created=await Apply(a,create);check(created.Status=="accepted"&&created.Entity!.GroupId==group.Id,"custom phrase and category atomic creation");
        check((await Apply(a,create)).Entity!.Revision==created.Entity!.Revision,"phrase retry idempotent");
        check((await b.Get<PhraseChanges>("phrases/changes")).Entities.Any(e=>e.Id==personal.Id&&e.Body==personal.Body),"same account receives full formatting");
        check((await outsider.Get<PhraseChanges>("phrases/changes")).Entities.Count==0,"private phrases and groups isolated");
        var foreign=personal.Copy();foreign.Body="stolen";check((await Apply(outsider,Op(foreign,"update",created.Entity.Revision))).Code=="NOT_FOUND","foreign update cannot access owner phrase");
        var snapshot=await a.Post<PhraseSnapshot>("phrases/snapshots",new{});await expect(()=>outsider.Get<PhraseSnapshotPage>($"phrases/snapshots/{snapshot.Token}"),404);
        var duplicate=personal.Copy();duplicate.Id=Guid.NewGuid().ToString();duplicate.Alias=null;
        var duplicateSource=await Apply(b,Op(duplicate));check(duplicateSource.Code=="PRESET_ALREADY_CUSTOMIZED"&&duplicateSource.Entity?.Id==personal.Id,"concurrent customization returns existing personal version");
        duplicate.OriginPresetId=null;duplicate.OriginPresetVersion=null;check((await Apply(a,Op(duplicate))).Status=="accepted","identical body remains independent");
        duplicate.Id=Guid.NewGuid().ToString();duplicate.Alias=personal.Alias;check((await Apply(a,Op(duplicate))).Code=="ALIAS_CONFLICT","personal alias unique");
        var edited=created.Entity.Copy();edited.Body="A 自定义";var update=await Apply(a,Op(edited,"update",edited.Revision));check(update.Status=="accepted","editable personal body");
        var stale=await Apply(b,Op(edited,"update",edited.Revision));check(stale.Status=="conflict"&&stale.Entity!.Body=="A 自定义","stale edit conflict preserves server version");
        var snapPage=await a.Get<PhraseSnapshotPage>($"phrases/snapshots/{snapshot.Token}");check(snapPage.Entities.Single(e=>e.Id==personal.Id).Body==personal.Body,"fixed snapshot unaffected by later edits");
        var pref=new PhraseEntity{Kind="preference",Id="communication.received",Hidden=true,Pinned=true};
        var hidden=await Apply(a,Op(pref));check(hidden.Status=="accepted","account preset preferences saved");
        check((await outsider.Get<PhraseChanges>("phrases/changes")).Entities.Count==0,"preferences do not leak to B");
        pref=hidden.Entity!.Copy();pref.Hidden=pref.Pinned=false;check((await Apply(a,Op(pref,"update",pref.Revision))).Status=="accepted","restore default preference is versioned");
        var deletedGroup=await Apply(a,Op(group,"delete"));check(deletedGroup.Related!.All(e=>e.GroupId is null)&&deletedGroup.Related!.Count>=1,"group deletion moves members without deleting text");
        var deleted=await Apply(a,Op(personal,"delete"));check(deleted.Entity!.DeletedAt is not null,"personal deletion tombstone");
        check((await Apply(b,Op(edited,"update",update.Entity!.Revision))).Status=="deleted","offline edit cannot resurrect deleted phrase");
        check((await Apply(a,Op(personal))).Status=="deleted","new operation cannot reuse deleted ID");
        var replacement=personal.Copy();replacement.Id=Guid.NewGuid().ToString();replacement.GroupId=null;check((await Apply(a,Op(replacement))).Status=="accepted","deleted customization may be recreated with a new identity");
        var oversized=new PhraseEntity{Title="large",Body=new string('x',65537)};check((await Apply(a,Op(oversized))).Code=="INVALID_PHRASE","body limit rejected");
        var obsolete=new PhraseEntity{Title="old group",Body="draft",GroupId=group.Id};check((await Apply(a,Op(obsolete))).Code=="GROUP_DELETED","offline deleted group rejected");
        var batch=await a.Post<PhraseOperationsResponse>("phrases/operations",new PhraseOperationsRequest(a.Session!.SyncEpoch,[Op(oversized),Op(new PhraseEntity{Title="valid batch",Body="kept"})]));check(batch.Results[0].Status=="rejected"&&batch.Results[1].Status=="accepted","batch savepoint preserves independent valid operation");
        await expect(()=>a.Get<PhraseChanges>("phrases/changes?after=9223372036854775807"),409);
        var before=(await a.Get<ClipboardLatest>("clipboard/latest")).ClipboardSequence;
        var root=Path.Combine(Path.GetTempPath(),"phrase-engine-"+Guid.NewGuid());
        using var ca=new SyncApi(a.BaseUri,a.Session);using var cb=new SyncApi(b.BaseUri,b.Session);
        await using var sa=new QuickPhraseSync(ca,new SyncConfig{Enabled=true},Path.Combine(root,"a"),metadata.InstanceId);
        await using var sb=new QuickPhraseSync(cb,new SyncConfig{Enabled=true},Path.Combine(root,"b"),metadata.InstanceId);
        await sa.SyncNow();await sb.SyncNow();
        var queued=new PhraseEntity{Title="two engines",Body="offline creation"};await sa.Save(queued);check(sa.Journal.Pending().Count==1,"durable local operation before network");
        await sa.SyncNow();await sb.SyncNow();check(sb.Library().Entities.Any(e=>e.Id==queued.Id&&e.Body==queued.Body),"two phrase engines synchronize personal content");
        check((await a.Get<ClipboardLatest>("clipboard/latest")).ClipboardSequence==before,"phrase CRUD and hydration never publish clipboard events");
        var ae=sa.Library().Entities.Single(e=>e.Id==queued.Id).Copy();var be=sb.Library().Entities.Single(e=>e.Id==queued.Id).Copy();ae.Body="A edit";be.Body="B draft";
        await sa.Save(ae);await sb.Save(be);await sa.SyncNow();await sb.SyncNow();check(sb.Library().Failures.Any(f=>f.Operation.Entity.Body=="B draft"),"real engine conflict preserves local draft");
        var failed=sb.Library().Failures.Single(f=>f.Operation.Entity.Id==queued.Id);await sb.Resolve(failed.Id,"copy");await sb.SyncNow();
        check(sb.Library().Entities.Any(e=>e.Body=="B draft"&&e.Id!=queued.Id)&&sb.Library().Failures.Count==0,"conflicting text can become independent personal copy");
        Console.WriteLine("PASS quick phrases: catalog capability, account isolation, customization, snapshot, conflicts, durable engines, clipboard independence");
    }
}
