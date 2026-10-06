using ClipHarbor.Core;
using ClipHarbor.Sync;
using Xunit;

namespace ClipHarbor.Core.Tests;

public sealed class CloudSyncTests
{
    [Fact] public async Task RemoteImportPreservesLocalLearningAndClearOnlyHides()
    {
        var path = Path.Combine(Path.GetTempPath(), "clipharbor-test-" + Guid.NewGuid());
        try
        {
            var store = new HistoryStore(path); await store.LoadAsync(); store.ActiveSyncSpace = "one";
            var record = await store.AddAsync(new ClipRecord { Text = "shared", SyncSpace = "one", SyncRecordId = "id", SyncContentHash = "hash", CaptureCount = 5, UseCount = 3 });
            await store.ImportRemoteAsync(new ClipRecord { Text = "shared", SyncSpace = "one", SyncRecordId = "id", SyncContentHash = "hash", Favorite = true, CaptureCount = 0 });
            Assert.Single(store.Items); Assert.Equal(record.Id, store.Items[0].Id); Assert.Equal(5, store.Items[0].CaptureCount); Assert.Equal(3, store.Items[0].UseCount);
            var other = new ClipRecord { Text = "old account", SyncSpace = "two" }; store.Items.Add(other);
            var mutations = new List<string>(); store.SyncMutation += (_, type) => mutations.Add(type);
            await store.ClearAsync(false); Assert.Single(store.Items); Assert.Same(other, store.Items[0]); Assert.Equal(new[] { "hide" }, mutations);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    [Fact] public async Task CopiesDoNotMergeIntoAnotherAccount()
    {
        var path = Path.Combine(Path.GetTempPath(), "clipharbor-test-" + Guid.NewGuid());
        try
        {
            var store = new HistoryStore(path); await store.LoadAsync(); store.ActiveSyncSpace = "new";
            store.Items.Add(new ClipRecord { Text = "same", SyncSpace = "old", Favorite = true });
            var added = await store.AddAsync(new ClipRecord { Text = "same" });
            Assert.Equal(2, store.Items.Count); Assert.False(added.Favorite); Assert.Null(added.SyncSpace);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    [Fact] public void UnsafeAttachmentNamesAreRejected()
    {
        foreach (var name in new[] { "..", "/tmp/file", "a/b", "a\\b", "a\0b" }) Assert.Throws<InvalidDataException>(() => Protocol.SafeName(name, new HashSet<string>()));
    }
}
