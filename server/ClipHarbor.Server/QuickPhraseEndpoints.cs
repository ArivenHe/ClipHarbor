using ClipHarbor.Sync;

namespace ClipHarbor.Server;

public static class QuickPhraseEndpoints
{
    public static void MapQuickPhrases(this WebApplication app)
    {
        app.MapPost("/api/v1/phrases/operations", async (PhraseOperationsRequest request, HttpContext context, Authentication auth, QuickPhraseStore store, EventHub hub) =>
        {
            var session = await auth.Require(context); var result = await store.Apply(session, request);
            hub.Notify(session.AccountId, new("phrases", "0", session.Epoch)); return result;
        });
        app.MapGet("/api/v1/phrases/changes", async (long? after, int? limit, HttpContext context, Authentication auth, QuickPhraseStore store) => await store.Changes(await auth.Require(context), after ?? 0, limit ?? 200));
        app.MapPost("/api/v1/phrases/snapshots", async (HttpContext context, Authentication auth, QuickPhraseStore store) => await store.Snapshot(await auth.Require(context)));
        app.MapGet("/api/v1/phrases/snapshots/{id:guid}", async (Guid id, int? page, HttpContext context, Authentication auth, QuickPhraseStore store) => await store.SnapshotPage(await auth.Require(context), id, page ?? 0));
    }
}
