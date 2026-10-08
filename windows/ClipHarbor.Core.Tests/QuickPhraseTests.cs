using ClipHarbor.Sync;
using Xunit;

namespace ClipHarbor.Core.Tests;

public sealed class QuickPhraseTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"phrases-test-"+Guid.NewGuid());
    private QuickPhraseJournal Store()=>new(root);
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    [Fact] public void SharedCatalogHasStableUniqueItems()
    {var catalog=PresetCatalog.Load();Assert.Equal(15,catalog.Phrases.Count);Assert.Equal(15,catalog.Phrases.Select(p=>p.PresetId).Distinct().Count());Assert.DoesNotContain(catalog.Phrases,p=>string.IsNullOrWhiteSpace(p.Body));}
    [Fact] public void UnicodeAndBodyValidationPreserveFormatting()
    {
        var phrase=new PhraseEntity{Title=string.Concat(Enumerable.Repeat("😀",80)),Body="  中文\r\n    code\r\n\r\n",Alias="ACK"};phrase.Validate();
        Assert.Equal("  中文\n    code\n\n",phrase.Body);Assert.Equal("ack",phrase.Alias);
        phrase.Title+="😀";Assert.Throws<InvalidDataException>(phrase.Validate);
        phrase.Title="test";phrase.Body=new string('a',65537);Assert.Throws<InvalidDataException>(phrase.Validate);
    }
    [Fact] public void DurableOfflineEditsCoalesceAndDeleteUnsentCreates()
    {
        var store=Store();var entity=new PhraseEntity{Title="回复",Body="收到"};store.Save(entity);entity.Body="稍后处理";store.Save(entity);
        var restored=Store();Assert.Single(restored.Pending());Assert.Equal("create",restored.Pending()[0].Type);Assert.Equal("稍后处理",restored.Library().Entities.Single().Body);
        restored.Save(entity,delete:true);Assert.Empty(Store().Pending());Assert.Empty(Store().Library().Entities);
    }
    [Fact] public void InflightRetriesKeepIdsAndRebaseDependentEdits()
    {
        var store=Store();var e=new PhraseEntity{Title="reply",Body="one"};var first=store.Save(e);store.MarkSent(first.OperationId);e.Body="two";var second=store.Save(e);
        Assert.Equal(first.OperationId,Store().Pending()[0].OperationId);
        var accepted=first.Entity.Copy();accepted.Revision="7";store.Acknowledge(first,new(first.OperationId,"accepted",accepted));
        var pending=Store().Pending().Single();Assert.Equal(second.OperationId,pending.OperationId);Assert.Equal("update",pending.Type);Assert.Equal("7",pending.ExpectedRevision);Assert.Equal("two",pending.Entity.Body);
    }
    [Fact] public void SnapshotAndConflictNeverDiscardDrafts()
    {
        var store=Store();var server=new PhraseEntity{Title="reply",Body="official",Revision="4"};store.Apply([server],"4","epoch");
        var mine=server.Copy();mine.Body="personal draft";var pending=store.Save(mine);store.Apply([server],"4","epoch",true);Assert.Equal("personal draft",store.Library().Entities.Single().Body);
        server.Body="other device";server.Revision="5";store.Acknowledge(pending,new(pending.OperationId,"conflict",server,"REVISION_CONFLICT"));
        Assert.Equal("personal draft",Store().Library().Failures.Single().Operation.Entity.Body);Assert.Equal("other device",Store().Library().Entities.Single().Body);
    }
    [Fact] public void OldChangesCannotReplaceNewerMirror()
    {var store=Store();var e=new PhraseEntity{Title="a",Body="new",Revision="10"};store.Apply([e],"10","epoch");e.Body="old";e.Revision="9";store.Apply([e],"10","epoch");Assert.Equal("new",store.Library().Entities.Single().Body);}
}
