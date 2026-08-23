using RssApp.Contracts;
using WasmApp.Services;

namespace SerializerTests;

/// <summary>
/// The write-behind outbox: read/saved changes made while the API was
/// unreachable, replayed once it is back.
/// </summary>
[TestClass]
public class PendingWritesTests
{
    private const long Now = 1_700_000_000_000;

    private static NewsFeedItem Item(string id)
        => new() { Id = id, FeedUrl = "https://feed/" + id, Href = "https://post/" + id, UserId = 7 };

    private static PendingWrite Read(string id, bool value, long ts = Now)
        => PendingWrite.For(Item(id), PendingWrite.ReadKind, value, ts);

    private static PendingWrite Saved(string id, bool value, long ts = Now)
        => PendingWrite.For(Item(id), PendingWrite.SavedKind, value, ts);

    [TestMethod]
    public void Collapse_ReplacesEarlierWriteForTheSameItemAndKind()
    {
        var queue = new List<PendingWrite> { Read("1", true) };

        queue = PendingWrites.Collapse(queue, Read("1", false, Now + 10), Now + 10);

        // Toggling read/unread repeatedly must cost one entry, holding the value
        // the user landed on.
        Assert.AreEqual(1, queue.Count);
        Assert.IsFalse(queue[0].Value);
    }

    [TestMethod]
    public void Collapse_KeepsReadAndSavedSeparateForTheSameItem()
    {
        var queue = PendingWrites.Collapse(new List<PendingWrite>(), Read("1", true), Now);
        queue = PendingWrites.Collapse(queue, Saved("1", true, Now + 1), Now + 1);

        Assert.AreEqual(2, queue.Count, "read and saved are independent flags");
    }

    [TestMethod]
    public void Collapse_KeepsWritesForDifferentItems()
    {
        var queue = PendingWrites.Collapse(new List<PendingWrite>(), Read("1", true), Now);
        queue = PendingWrites.Collapse(queue, Read("2", true, Now + 1), Now + 1);

        Assert.AreEqual(2, queue.Count);
    }

    [TestMethod]
    public void Collapse_BoundsTheQueue()
    {
        var queue = new List<PendingWrite>();
        for (int i = 0; i < PendingWrites.MaxEntries + 25; i++)
        {
            queue = PendingWrites.Collapse(queue, Read(i.ToString(), true, Now + i), Now + i);
        }

        Assert.AreEqual(PendingWrites.MaxEntries, queue.Count);
        // The newest writes are the ones worth keeping.
        Assert.IsTrue(queue.Any(w => w.ItemId == (PendingWrites.MaxEntries + 24).ToString()));
        Assert.IsFalse(queue.Any(w => w.ItemId == "0"));
    }

    [TestMethod]
    public void Prune_DropsEntriesPastTheMaxAge()
    {
        var stale = Read("1", true, Now - (long)PendingWrites.MaxAge.TotalMilliseconds - 1);
        var fresh = Read("2", true, Now);

        var kept = PendingWrites.Prune(new[] { stale, fresh }, Now);

        // Replaying a very old flag could clobber a deliberate later change made
        // on another device.
        Assert.AreEqual(1, kept.Count);
        Assert.AreEqual("2", kept[0].ItemId);
    }

    [TestMethod]
    public void Apply_OverlaysQueuedFlagsOntoFetchedItems()
    {
        var fetched = new List<NewsFeedItem>
        {
            new() { Id = "1", IsRead = false, IsSaved = false },
            new() { Id = "2", IsRead = false, IsSaved = false },
        };

        PendingWrites.Apply(new[] { Read("1", true), Saved("2", true) }, fetched);

        Assert.IsTrue(fetched[0].IsRead, "a queued read must survive a server page that predates it");
        Assert.IsFalse(fetched[0].IsSaved);
        Assert.IsTrue(fetched[1].IsSaved);
    }

    [TestMethod]
    public void Apply_LeavesUnaffectedItemsAlone()
    {
        var fetched = new List<NewsFeedItem> { new() { Id = "99", IsRead = true, IsSaved = true } };

        PendingWrites.Apply(new[] { Read("1", false) }, fetched);

        Assert.IsTrue(fetched[0].IsRead);
        Assert.IsTrue(fetched[0].IsSaved);
    }

    [TestMethod]
    public void Apply_ToleratesEmptyAndNullInput()
    {
        var fetched = new List<NewsFeedItem> { new() { Id = "1", IsRead = false } };

        PendingWrites.Apply(null, fetched);
        PendingWrites.Apply(new List<PendingWrite>(), fetched);
        PendingWrites.Apply(new[] { Read("1", true) }, null);

        Assert.IsFalse(fetched[0].IsRead);
    }

    [TestMethod]
    public void ToItem_CarriesTheIdentityTheSaveEndpointMatchesOn()
    {
        // The save/unsave endpoints match rows on (UserId, FeedUrl, Href), not on
        // the item id, so a replay built only from an id would silently no-op.
        var replayed = Saved("42", true).ToItem();

        Assert.AreEqual("42", replayed.Id);
        Assert.AreEqual("https://feed/42", replayed.FeedUrl);
        Assert.AreEqual("https://post/42", replayed.Href);
        Assert.AreEqual(7, replayed.UserId);
    }
}
