using RssApp.Contracts;

namespace WasmApp.Services;

/// <summary>
/// A read/saved flag change that has not reached the server yet. Both kinds are
/// idempotent -- they set an absolute value rather than toggling -- so replaying
/// one is always safe.
///
/// FeedUrl/Href/UserId are carried because the save endpoints match rows on
/// (UserId, FeedUrl, Href) rather than on the item id, and by replay time the
/// originating item may no longer be in the cached timeline.
/// </summary>
public record PendingWrite(
    string ItemId,
    string Kind,
    bool Value,
    long Ts,
    string FeedUrl = null,
    string Href = null,
    int UserId = 0)
{
    public const string ReadKind = "read";
    public const string SavedKind = "saved";

    public static PendingWrite For(NewsFeedItem item, string kind, bool value, long nowMs)
        => new(item.Id, kind, value, nowMs, item.FeedUrl, item.Href, item.UserId);

    /// <summary>Rebuilds the payload the save/unsave endpoints expect.</summary>
    public NewsFeedItem ToItem() => new()
    {
        Id = this.ItemId,
        FeedUrl = this.FeedUrl,
        Href = this.Href,
        UserId = this.UserId,
    };
}

/// <summary>
/// Queue arithmetic for the write-behind outbox, kept pure so it can be tested
/// without a browser. Storage lives in <see cref="PostCache"/>.
/// </summary>
public static class PendingWrites
{
    // A runaway queue would be a symptom of a backend that has been unreachable
    // for a very long time; past this we drop the oldest rather than grow without
    // bound in a storage area shared with the cached posts themselves.
    public const int MaxEntries = 500;

    // Replaying a very old flag could clobber a deliberate later change made on
    // another device, so entries expire rather than waiting forever.
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    /// <summary>
    /// Adds a write to the queue, replacing any earlier write for the same item
    /// and kind. Toggling a post read/unread/read repeatedly therefore costs one
    /// entry and replays once, with the value the user last chose.
    /// </summary>
    public static List<PendingWrite> Collapse(IEnumerable<PendingWrite> existing, PendingWrite incoming, long nowMs)
    {
        var result = Prune(existing, nowMs);
        result.RemoveAll(w => SameTarget(w, incoming));
        result.Add(incoming);

        if (result.Count > MaxEntries)
        {
            result = result
                .OrderByDescending(w => w.Ts)
                .Take(MaxEntries)
                .OrderBy(w => w.Ts)
                .ToList();
        }

        return result;
    }

    /// <summary>Drops expired entries, preserving order.</summary>
    public static List<PendingWrite> Prune(IEnumerable<PendingWrite> writes, long nowMs)
    {
        var cutoff = nowMs - (long)MaxAge.TotalMilliseconds;
        return (writes ?? Enumerable.Empty<PendingWrite>())
            .Where(w => w != null && !string.IsNullOrEmpty(w.ItemId) && w.Ts >= cutoff)
            .ToList();
    }

    /// <summary>
    /// Overlays queued writes onto freshly fetched items. Without this, a page
    /// loaded before the queue drains would show the server's older value and
    /// visibly revert what the user just did.
    /// </summary>
    public static void Apply(IEnumerable<PendingWrite> pending, IEnumerable<NewsFeedItem> items)
    {
        if (pending == null || items == null)
        {
            return;
        }

        var byItem = pending
            .Where(w => w != null && !string.IsNullOrEmpty(w.ItemId))
            .GroupBy(w => w.ItemId)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (byItem.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item?.Id == null || !byItem.TryGetValue(item.Id, out var writes))
            {
                continue;
            }

            foreach (var write in writes)
            {
                if (write.Kind == PendingWrite.ReadKind)
                {
                    item.IsRead = write.Value;
                }
                else if (write.Kind == PendingWrite.SavedKind)
                {
                    item.IsSaved = write.Value;
                }
            }
        }
    }

    private static bool SameTarget(PendingWrite a, PendingWrite b)
        => a.ItemId == b.ItemId && a.Kind == b.Kind;
}
