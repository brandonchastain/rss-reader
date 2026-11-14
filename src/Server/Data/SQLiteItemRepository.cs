using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RssApp.Contracts;

namespace RssApp.Data;

/// <summary>
/// Posts are stored once per feed; what a user sees is the join of their
/// Subscriptions, the shared Posts and their sparse UserPostState rows. The
/// NewsFeedItem the repository returns is that view: UserId is the requesting
/// user, FeedUrl comes from Feeds.Url and Id is Posts.Id (shared, opaque).
/// </summary>
public class SQLiteItemRepository : IItemRepository, IDisposable
{
    private readonly IDbConnections connections;
    private readonly ILogger<SQLiteItemRepository> logger;
    private readonly IFeedRepository feedStore;
    private readonly IUserRepository userStore;

    // Serialize writes  SQLite allows only one writer at a time.
    private readonly SemaphoreSlim writeSemaphore = new SemaphoreSlim(1, 1);

    public SQLiteItemRepository(
        IDbConnections connections,
        ILogger<SQLiteItemRepository> logger,
        IFeedRepository feedStore,
        IUserRepository userStore,
        bool rebuildFtsOnStartup = false)
    {
        this.connections = connections;
        this.logger = logger;
        this.feedStore = feedStore;
        this.userStore = userStore;
        this.InitializeDatabase(rebuildFtsOnStartup);
    }

    private void InitializeDatabase(bool rebuildFtsOnStartup)
    {
        using var connection = this.connections.OpenWrite();

        // A table that is created right here is empty by definition and needs
        // no rebuild. Checked against sqlite_master rather than by counting the
        // FTS rows, which walks the whole index on every cold start.
        var ftsTableExisted = DatabaseSchema.TableExists(connection, "Posts_fts");
        DatabaseSchema.EnsureCreated(connection);

        if (ftsTableExisted && !rebuildFtsOnStartup)
        {
            logger.LogInformation("FTS5 index already present — skipping rebuild");
            return;
        }

        // Only when the table was just created (fresh DB) or explicitly
        // requested via config (RssAppConfig__RebuildFtsOnStartup=true).
        logger.LogInformation("FTS5 rebuild starting (existed={Existed}, forced={Forced})", ftsTableExisted, rebuildFtsOnStartup);
        try
        {
            DatabaseSchema.Execute(connection, "INSERT INTO Posts_fts(Posts_fts) VALUES('rebuild');");
            logger.LogInformation("FTS5 rebuild completed successfully");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 11) // SQLITE_CORRUPT
        {
            logger.LogWarning(ex, "FTS5 index corrupt  dropping and recreating");
            DatabaseSchema.Execute(connection, "DROP TABLE IF EXISTS Posts_fts;");
            DatabaseSchema.Execute(connection, DatabaseSchema.FtsTable);
            DatabaseSchema.Execute(connection, "INSERT INTO Posts_fts(Posts_fts) VALUES('rebuild');");
            logger.LogInformation("FTS5 recreated and rebuilt from scratch");
        }
    }

    // -------------------------------------------------------------------------
    // Shared SELECT shape
    // -------------------------------------------------------------------------

    private static string SelectColumns(bool includeContent, bool walkTimelineIndex = false) => $"""
        SELECT
            p.Id,
            f.Url AS FeedUrl,
            p.Href,
            p.CommentsHref,
            p.Title,
            p.FormerTitle,
            p.PublishDateOrder,
            p.PublishDate,
            {(includeContent ? "pc.Content" : "NULL AS Content")},
            COALESCE(s.IsRead, 0) AS IsRead,
            COALESCE(s.IsSaved, 0) AS IsSaved,
            p.ThumbnailUrl,
            (SELECT group_concat(t.Name, ',') FROM SubscriptionTags st
             JOIN Tags t ON t.Id = st.TagId
             WHERE st.UserId = @userId AND st.FeedId = p.FeedId) AS Tags
        FROM Posts p{(walkTimelineIndex ? " INDEXED BY IX_Posts_Timeline" : "")}
        JOIN Feeds f ON f.Id = p.FeedId
        {(includeContent ? "LEFT JOIN PostContent pc ON pc.PostId = p.Id" : "")}
        LEFT JOIN UserPostState s ON s.PostId = p.Id AND s.UserId = @userId
        """;

    // The subscription join and the per-subscription watermark: a post is
    // visible when the user subscribes to its feed and it is newer than the
    // "clear posts" watermark (NULL = show all).
    private const string SubscriptionJoin = """

        JOIN Subscriptions sub ON sub.FeedId = p.FeedId AND sub.UserId = @userId

        """;

    private const string VisibleWhere = """
        (sub.HideBeforeOrder IS NULL OR p.PublishDateOrder > sub.HideBeforeOrder)
        """;

    // The saved view is the user's own collection: a post stays in it after
    // the feed is unsubscribed or cleared, as it did when items were per-user
    // rows. Every other view is scoped by the subscription and its watermark.
    private static string ScopeFor(bool isFilterSaved) => isFilterSaved
        ? """

          LEFT JOIN Subscriptions sub ON sub.FeedId = p.FeedId AND sub.UserId = @userId
          WHERE s.IsSaved = 1
          """
        : SubscriptionJoin + " WHERE " + VisibleWhere;

    private const string HiddenTagExclusion = """
        NOT EXISTS (
            SELECT 1 FROM SubscriptionTags st JOIN Tags t ON t.Id = st.TagId
            WHERE st.UserId = @userId AND st.FeedId = p.FeedId AND t.IsHidden = 1)
        """;

    // -------------------------------------------------------------------------
    // Search
    // -------------------------------------------------------------------------

    public async Task<IEnumerable<NewsFeedItem>> SearchItemsAsync(string query, RssUser user, int page, int pageSize)
    {
        try
        {
            return await SearchItemsCoreAsync(query, user, page, pageSize, useFts: true);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 11) // SQLITE_CORRUPT
        {
            logger.LogWarning(ex, "FTS5 index corrupt, falling back to LIKE-only search for query: {Query}", query);
            return await SearchItemsCoreAsync(query, user, page, pageSize, useFts: false);
        }
    }

    /// <summary>
    /// The search SELECT. With the full-text index the query is a phrase whose last word may be
    /// cut short ("cloudfl" finds "cloudflare"), which the index answers directly (12 to 35 ms on
    /// 150,000 posts). It used to be OR-ed with a title LIKE '%word%', which cannot use an index,
    /// so every search scanned every title (about 200 ms). Words that begin mid-word are no longer
    /// found; the LIKE scan remains only as the fallback when the index is unusable.
    ///
    /// With the index, the newest page of matching ids is chosen first and only then are the wide
    /// columns (content, tags, state) read, for those few rows: a common word matches tens of
    /// thousands of posts, and building the whole row for each of them before sorting cost 97 ms.
    /// </summary>
    internal static SqliteCommand BuildSearchCommand(SqliteConnection connection, string query, RssUser user, int page, int pageSize, bool useFts)
    {
        var command = connection.CreateCommand();
        if (useFts)
        {
            command.CommandText = SelectColumns(includeContent: true) + SubscriptionJoin + """
                WHERE p.Id IN (
                    SELECT p2.Id FROM Posts p2
                    JOIN Subscriptions sub2 ON sub2.FeedId = p2.FeedId AND sub2.UserId = @userId
                    WHERE p2.Id IN (SELECT rowid FROM Posts_fts WHERE Posts_fts MATCH @query)
                    ORDER BY p2.PublishDateOrder DESC, p2.Id DESC
                    LIMIT @pageSize OFFSET @offset)
                ORDER BY p.PublishDateOrder DESC, p.Id DESC
                """;
        }
        else
        {
            command.CommandText = SelectColumns(includeContent: true) + SubscriptionJoin + """
                WHERE (p.Title LIKE @plainQuery OR pc.Content LIKE @plainQuery)
                ORDER BY p.PublishDateOrder DESC, p.Id DESC
                LIMIT @pageSize OFFSET @offset
                """;
        }
        if (useFts) command.Parameters.AddWithValue("@query", $"\"{query.Replace("\"", "\"\"")}\"*");
        else command.Parameters.AddWithValue("@plainQuery", $"%{query}%");
        command.Parameters.AddWithValue("@userId", user.Id);
        command.Parameters.AddWithValue("@pageSize", pageSize);
        command.Parameters.AddWithValue("@offset", page * pageSize);
        return command;
    }

    private async Task<IEnumerable<NewsFeedItem>> SearchItemsCoreAsync(string query, RssUser user, int page, int pageSize, bool useFts)
    {
        var items = new List<NewsFeedItem>();
        using var connection = await this.connections.OpenReadAsync();
        using var command = BuildSearchCommand(connection, query, user, page, pageSize, useFts);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(ReadItemFromResults(reader, user.Id));
        }

        return items;
    }

    // -------------------------------------------------------------------------
    // Timeline
    // -------------------------------------------------------------------------

    public async Task<IEnumerable<NewsFeedItem>> GetItemsAsync(
        NewsFeed feed,
        bool isFilterUnread,
        bool isFilterSaved,
        string filterTag,
        int? page = null,
        int? pageSize = null,
        long? lastId = null,
        long? lastPublishDateOrder = null,
        IEnumerable<string> excludeFeedUrls = null,
        bool includeContent = true)
    {
        var items = new List<NewsFeedItem>();
        using var connection = await this.connections.OpenReadAsync();
        using var command = BuildTimelineCommand(
            connection, feed, isFilterUnread, isFilterSaved, filterTag, page, pageSize, lastId, lastPublishDateOrder, excludeFeedUrls, includeContent);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(ReadItemFromResults(reader, feed.UserId));
        }

        return items;
    }

    /// <summary>
    /// The timeline SELECT with its filters, cursor and paging. For the whole-timeline view the
    /// newest-first index is pinned: left to itself the planner starts from the user's
    /// subscriptions and sorts every visible post into a temporary tree for each page (83 ms
    /// on 150,000 posts, growing with the table), where walking the index and stopping at the
    /// page size takes about 1 ms.
    /// </summary>
    internal static SqliteCommand BuildTimelineCommand(
        SqliteConnection connection,
        NewsFeed feed,
        bool isFilterUnread,
        bool isFilterSaved,
        string filterTag,
        int? page,
        int? pageSize,
        long? lastId,
        long? lastPublishDateOrder,
        IEnumerable<string> excludeFeedUrls,
        bool includeContent)
    {
        var command = connection.CreateCommand();
        var wholeTimeline = feed.Href == "%" && !isFilterSaved;
        command.CommandText = SelectColumns(includeContent, walkTimelineIndex: wholeTimeline) + ScopeFor(isFilterSaved);
        command.Parameters.AddWithValue("@userId", feed.UserId);

        AppendFilters(command, feed, isFilterUnread, isFilterSaved, filterTag, excludeFeedUrls);

        // Cursor-based pagination: use (PublishDateOrder, Id) as seek position
        bool useCursor = lastPublishDateOrder.HasValue && lastId.HasValue;
        if (useCursor)
        {
            command.CommandText += " AND (p.PublishDateOrder, p.Id) < (@lastOrder, @lastId)";
            command.Parameters.AddWithValue("@lastOrder", lastPublishDateOrder.Value);
            command.Parameters.AddWithValue("@lastId", lastId.Value);
        }

        command.CommandText += " ORDER BY p.PublishDateOrder DESC, p.Id DESC";
        pageSize ??= 20;
        command.CommandText += " LIMIT @pageSize";
        command.Parameters.AddWithValue("@pageSize", pageSize);

        if (!useCursor)
        {
            page ??= 0;
            command.CommandText += " OFFSET @offset";
            command.Parameters.AddWithValue("@offset", page * pageSize);
        }

        return command;
    }

    public async Task<int> GetNewItemCountAsync(
        NewsFeed feed,
        bool isFilterUnread,
        bool isFilterSaved,
        string filterTag,
        long cursorPublishDateOrder,
        long cursorId,
        IEnumerable<string> excludeFeedUrls = null)
    {
        using var connection = await this.connections.OpenReadAsync();
        var command = connection.CreateCommand();

        // Same WHERE as the timeline with the cursor comparison flipped. COUNT
        // DISTINCT Href mirrors the controller's post-query DistinctBy(Href) so
        // the hint matches what a top-of-list reload actually surfaces.
        command.CommandText = """
            SELECT COUNT(DISTINCT p.Href) FROM Posts p
            JOIN Feeds f ON f.Id = p.FeedId
            LEFT JOIN UserPostState s ON s.PostId = p.Id AND s.UserId = @userId
            """ + ScopeFor(isFilterSaved);
        command.Parameters.AddWithValue("@userId", feed.UserId);

        AppendFilters(command, feed, isFilterUnread, isFilterSaved, filterTag, excludeFeedUrls);

        command.CommandText += " AND (p.PublishDateOrder, p.Id) > (@cursorOrder, @cursorId)";
        command.Parameters.AddWithValue("@cursorOrder", cursorPublishDateOrder);
        command.Parameters.AddWithValue("@cursorId", cursorId);

        var count = await command.ExecuteScalarAsync();
        return Convert.ToInt32(count);
    }

    private static void AppendFilters(
        SqliteCommand command,
        NewsFeed feed,
        bool isFilterUnread,
        bool isFilterSaved,
        string filterTag,
        IEnumerable<string> excludeFeedUrls)
    {
        if (feed.Href != "%")
        {
            command.CommandText += " AND f.Url = @feedUrl";
            command.Parameters.AddWithValue("@feedUrl", feed.Href);
        }

        if (isFilterUnread)
        {
            command.CommandText += " AND COALESCE(s.IsRead, 0) = 0";
        }

        if (isFilterSaved)
        {
            command.CommandText += " AND s.IsSaved = 1";
        }

        if (!string.IsNullOrWhiteSpace(filterTag))
        {
            command.CommandText += """
                 AND EXISTS (SELECT 1 FROM SubscriptionTags st JOIN Tags t ON t.Id = st.TagId
                             WHERE st.UserId = @userId AND st.FeedId = p.FeedId AND t.Name = @tagName)
                """;
            command.Parameters.AddWithValue("@tagName", filterTag);
        }

        // The caller passes a (possibly empty) exclusion list exactly when the
        // view should hide feeds carrying a hidden tag: the plain timeline. The
        // saved and explicit-tag views pass null and see everything. The hidden
        // flag is read live from Tags; any URLs the caller listed are excluded
        // as well.
        if (excludeFeedUrls != null)
        {
            command.CommandText += " AND " + HiddenTagExclusion;
            var excludeUrls = excludeFeedUrls.ToList();
            if (excludeUrls.Count > 0)
            {
                var excludeParams = new List<string>();
                for (int idx = 0; idx < excludeUrls.Count; idx++)
                {
                    var paramName = $"@excludeUrl{idx}";
                    excludeParams.Add(paramName);
                    command.Parameters.AddWithValue(paramName, excludeUrls[idx]);
                }
                command.CommandText += $" AND f.Url NOT IN ({string.Join(",", excludeParams)})";
            }
        }
    }

    private static NewsFeedItem ReadItemFromResults(DbDataReader reader, int userId)
    {
        var id = reader.GetInt64(reader.GetOrdinal("Id")).ToString(CultureInfo.InvariantCulture);
        var href = reader.IsDBNull(reader.GetOrdinal("Href")) ? "" : reader.GetString(reader.GetOrdinal("Href"));
        var commentsHref = reader.IsDBNull(reader.GetOrdinal("CommentsHref")) ? "" : reader.GetString(reader.GetOrdinal("CommentsHref"));
        var title = reader.IsDBNull(reader.GetOrdinal("Title")) ? "" : reader.GetString(reader.GetOrdinal("Title"));
        var publishDateOrder = reader.IsDBNull(reader.GetOrdinal("PublishDateOrder")) ? 0 : reader.GetInt64(reader.GetOrdinal("PublishDateOrder"));
        var publishDate = reader.IsDBNull(reader.GetOrdinal("PublishDate")) ? "" : reader.GetString(reader.GetOrdinal("PublishDate"));
        string content = reader.IsDBNull(reader.GetOrdinal("Content")) ? null : reader.GetString(reader.GetOrdinal("Content"));
        var url = reader.IsDBNull(reader.GetOrdinal("FeedUrl")) ? "" : reader.GetString(reader.GetOrdinal("FeedUrl"));
        var isRead = !reader.IsDBNull(reader.GetOrdinal("IsRead")) && reader.GetInt64(reader.GetOrdinal("IsRead")) != 0;
        var isSaved = !reader.IsDBNull(reader.GetOrdinal("IsSaved")) && reader.GetInt64(reader.GetOrdinal("IsSaved")) != 0;
        var tags = reader.IsDBNull(reader.GetOrdinal("Tags")) ? "" : reader.GetString(reader.GetOrdinal("Tags"));
        var thumbnailUrl = reader.IsDBNull(reader.GetOrdinal("ThumbnailUrl")) ? "" : reader.GetString(reader.GetOrdinal("ThumbnailUrl"));
        var formerTitle = reader.IsDBNull(reader.GetOrdinal("FormerTitle")) ? null : reader.GetString(reader.GetOrdinal("FormerTitle"));

        return new NewsFeedItem(id, userId, title, href, commentsHref, publishDate, content, thumbnailUrl)
        {
            FeedUrl = url,
            IsRead = isRead,
            FeedTags = string.IsNullOrWhiteSpace(tags) ? [] : tags.Split(","),
            IsSaved = isSaved,
            PublishDateOrder = publishDateOrder,
            FormerTitle = formerTitle
        };
    }

    // -------------------------------------------------------------------------
    // Single items
    // -------------------------------------------------------------------------

    // A post is the user's when they subscribe to its feed or already hold
    // state on it (a saved post of a feed they since left stays reachable).
    private const string OwnershipWhere = """
        (EXISTS (SELECT 1 FROM Subscriptions sub WHERE sub.FeedId = p.FeedId AND sub.UserId = @userId)
         OR s.PostId IS NOT NULL)
        """;

    public NewsFeedItem GetItem(RssUser user, string href)
    {
        using var connection = this.connections.OpenRead();
        var command = connection.CreateCommand();
        command.CommandText = SelectColumns(includeContent: false)
            + " WHERE p.Href = @href AND " + OwnershipWhere + " ORDER BY p.Id LIMIT 1";
        command.Parameters.AddWithValue("@href", href);
        command.Parameters.AddWithValue("@userId", user.Id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItemFromResults(reader, user.Id) : null;
    }

    public NewsFeedItem GetItem(RssUser user, int itemId)
    {
        using var connection = this.connections.OpenRead();
        var command = connection.CreateCommand();
        command.CommandText = SelectColumns(includeContent: false)
            + " WHERE p.Id = @id AND " + OwnershipWhere;
        command.Parameters.AddWithValue("@id", itemId);
        command.Parameters.AddWithValue("@userId", user.Id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItemFromResults(reader, user.Id) : null;
    }

    public string GetItemContent(NewsFeedItem item)
    {
        using var connection = this.connections.OpenRead();
        var postId = ResolvePostId(connection, item);
        if (postId == null) return string.Empty;

        var command = connection.CreateCommand();
        command.CommandText = "SELECT Content FROM PostContent WHERE PostId = @postId";
        command.Parameters.AddWithValue("@postId", postId.Value);
        return command.ExecuteScalar() as string ?? string.Empty;
    }

    /// <summary>
    /// The shared post an item refers to. (FeedUrl, Href) is preferred because
    /// it is stable across the id change of the migration; the numeric Id is
    /// the fallback for callers that only have that.
    /// </summary>
    // ResolvePostId's restriction to posts the user can see: a feed they subscribe
    // to, or a post they already have read/saved state on. Same rule as
    // OwnershipWhere, written against Posts p without the UserPostState join.
    private const string ResolveOwnedWhere = """
         AND (EXISTS (SELECT 1 FROM Subscriptions sub WHERE sub.FeedId = p.FeedId AND sub.UserId = @userId)
              OR EXISTS (SELECT 1 FROM UserPostState ups WHERE ups.PostId = p.Id AND ups.UserId = @userId))
        """;

    /// <summary>
    /// Finds the Posts row an item names, by (feed URL, href) or by id. With a
    /// userId, only a post that user can see resolves; anything else is null,
    /// so a crafted id or href can never reach another user's feed.
    /// </summary>
    private static long? ResolvePostId(SqliteConnection connection, NewsFeedItem item, int? userId = null)
    {
        var owned = userId.HasValue ? ResolveOwnedWhere : string.Empty;
        if (!string.IsNullOrEmpty(item.FeedUrl) && !string.IsNullOrEmpty(item.Href))
        {
            var byKey = connection.CreateCommand();
            byKey.CommandText = "SELECT p.Id FROM Posts p JOIN Feeds f ON f.Id = p.FeedId WHERE f.Url = @url AND p.Href = @href" + owned;
            byKey.Parameters.AddWithValue("@url", item.FeedUrl);
            byKey.Parameters.AddWithValue("@href", item.Href);
            if (userId.HasValue) byKey.Parameters.AddWithValue("@userId", userId.Value);
            var found = byKey.ExecuteScalar();
            if (found is long id) return id;
        }

        if (long.TryParse(item.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            var byId = connection.CreateCommand();
            byId.CommandText = "SELECT p.Id FROM Posts p WHERE p.Id = @id" + owned;
            byId.Parameters.AddWithValue("@id", parsed);
            if (userId.HasValue) byId.Parameters.AddWithValue("@userId", userId.Value);
            if (byId.ExecuteScalar() is long id) return id;
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Writes
    // -------------------------------------------------------------------------

    public async Task<int> AddItemsAsync(IEnumerable<NewsFeedItem> items)
        => (await this.UpsertItemsAsync(items)).Inserted;

    /// <summary>
    /// Stores a fetch's items once per feed. A post is the same post when its
    /// link matches, or failing that when its guid matches: some publishers (The
    /// Register) put the headline in the URL, so fixing a typo in a title moves
    /// the link while the guid stays put. The link is checked first and the guid
    /// is only the fallback, so rows stored before guids were kept still match by
    /// link and nothing is re-inserted or backfilled. The fallback also needs the
    /// same publish date: a renamed article keeps its date, while a feed that
    /// reuses guids (numbering items by position, say) gives a different article
    /// a different date, and overwriting that post would lose it. A matched post keeps its row, and with it
    /// every reader's read and saved state; a changed title is recorded, the
    /// first one kept in FormerTitle.
    /// </summary>
    public async Task<ItemWriteResult> UpsertItemsAsync(IEnumerable<NewsFeedItem> items)
    {
        var itemList = items.Where(i => !string.IsNullOrEmpty(i.FeedUrl)).ToList();
        if (itemList.Count == 0) return default;

        // A guid only identifies a post when it is unique within the fetch and
        // is not just the feed's own address; feeds that repeat one guid on every
        // item would otherwise fold distinct posts into one.
        var usableGuids = itemList
            .Where(i => !string.IsNullOrWhiteSpace(i.Id) && !string.Equals(i.Id.Trim(), i.FeedUrl, StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => (i.FeedUrl, Guid: i.Id.Trim()))
            .Where(g => g.Count() == 1)
            .Select(g => g.Key)
            .ToHashSet();

        var insertedCount = 0;
        var updatedCount = 0;
        await this.writeSemaphore.WaitAsync();
        try
        {
            using var connection = await this.connections.OpenWriteAsync();
            using var transaction = connection.BeginTransaction();

            // One Feeds lookup per distinct URL. A URL with no feed row has no
            // subscribers and nothing to attach its posts to; skip it.
            var feedIds = new Dictionary<string, long?>(StringComparer.Ordinal);
            foreach (var url in itemList.Select(i => i.FeedUrl).Distinct(StringComparer.Ordinal))
            {
                var lookup = connection.CreateCommand();
                lookup.Transaction = transaction;
                lookup.CommandText = "SELECT Id FROM Feeds WHERE Url = @url";
                lookup.Parameters.AddWithValue("@url", url);
                feedIds[url] = lookup.ExecuteScalar() as long?;
                if (feedIds[url] == null)
                {
                    this.logger.LogWarning("No feed row for {Url}; its {Count} items were not stored", url, itemList.Count(i => i.FeedUrl == url));
                }
            }

            var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            foreach (var item in itemList)
            {
                var feedId = feedIds[item.FeedUrl];
                if (feedId == null) continue;

                var href = item.Href ?? "";
                var title = item.Title ?? "";
                var guid = string.IsNullOrWhiteSpace(item.Id) ? null : item.Id.Trim();
                if (guid != null && !usableGuids.Contains((item.FeedUrl, guid))) guid = null;

                try
                {
                    var existing = FindPost(connection, transaction, feedId.Value, "Href", href)
                        ?? (guid == null ? null : FindPost(connection, transaction, feedId.Value, "Guid", guid, item.PublishDateOrder));

                    if (existing is { } post)
                    {
                        if (UpdatePost(connection, transaction, post, item, href, title))
                        {
                            updatedCount++;
                        }

                        continue;
                    }

                    var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT OR IGNORE INTO Posts (FeedId, Href, CommentsHref, Title, PublishDateOrder, PublishDate, ThumbnailUrl, FirstSeenUtc, Guid)
                        VALUES (@feedId, @href, @commentsHref, @title, @publishDateOrder, @publishDate, @thumbnailUrl, @now, @guid)
                        """;
                    command.Parameters.AddWithValue("@feedId", feedId.Value);
                    command.Parameters.AddWithValue("@href", href);
                    command.Parameters.AddWithValue("@commentsHref", (object)item.CommentsHref ?? DBNull.Value);
                    command.Parameters.AddWithValue("@title", title);
                    command.Parameters.AddWithValue("@publishDateOrder", item.PublishDateOrder);
                    command.Parameters.AddWithValue("@publishDate", item.PublishDate ?? "");
                    command.Parameters.AddWithValue("@thumbnailUrl", (object)item.ThumbnailUrl ?? DBNull.Value);
                    command.Parameters.AddWithValue("@now", now);
                    command.Parameters.AddWithValue("@guid", (object)guid ?? DBNull.Value);
                    var rowsAffected = await command.ExecuteNonQueryAsync();
                    if (rowsAffected == 0) continue;

                    insertedCount += rowsAffected;
                    var content = connection.CreateCommand();
                    content.Transaction = transaction;
                    content.CommandText = """
                        INSERT OR IGNORE INTO PostContent (PostId, Title, Content)
                        VALUES (last_insert_rowid(), @title, @content)
                        """;
                    content.Parameters.AddWithValue("@title", title);
                    content.Parameters.AddWithValue("@content", item.Content ?? "");
                    await content.ExecuteNonQueryAsync();
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    this.logger.LogWarning(ex, "Constraint violation for item {Href}, skipping", item.Href);
                }
            }

            transaction.Commit();
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Error adding items to SQLite database");
            return default;
        }
        finally
        {
            this.writeSemaphore.Release();
        }

        return new ItemWriteResult(insertedCount, updatedCount);
    }

    private readonly record struct StoredPost(long Id, string Href, string Title, string FormerTitle);

    // column is a fixed identifier ("Href" or "Guid"), never input.
    private static StoredPost? FindPost(SqliteConnection connection, SqliteTransaction transaction, long feedId, string column, string value, long? publishDateOrder = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SELECT Id, Href, Title, FormerTitle FROM Posts WHERE FeedId = @feedId AND {column} = @value"
            + (publishDateOrder.HasValue ? " AND PublishDateOrder = @publishDateOrder" : "")
            + " ORDER BY Id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@feedId", feedId);
        cmd.Parameters.AddWithValue("@value", value);
        if (publishDateOrder.HasValue) cmd.Parameters.AddWithValue("@publishDateOrder", publishDateOrder.Value);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new StoredPost(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? "" : reader.GetString(1),
            reader.IsDBNull(2) ? "" : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    /// <summary>
    /// Brings a stored post in line with the feed's current copy when its title
    /// or link changed, refreshing the searchable content with it. An unchanged
    /// post (nearly every item of every fetch) costs no write.
    /// </summary>
    private bool UpdatePost(SqliteConnection connection, SqliteTransaction transaction, StoredPost post, NewsFeedItem item, string href, string title)
    {
        var retitled = title.Length > 0 && !string.Equals(title, post.Title, StringComparison.Ordinal);
        var relinked = !string.Equals(href, post.Href, StringComparison.Ordinal);
        if (!retitled && !relinked) return false;

        // FormerTitle is the title readers first saw, kept across later renames;
        // a rename back to it leaves nothing to show.
        var formerTitle = post.FormerTitle;
        if (retitled)
        {
            formerTitle ??= string.IsNullOrEmpty(post.Title) ? null : post.Title;
            if (string.Equals(formerTitle, title, StringComparison.Ordinal)) formerTitle = null;
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE Posts
                SET Href = @href, Title = @title, FormerTitle = @formerTitle,
                    CommentsHref = COALESCE(@commentsHref, CommentsHref)
                WHERE Id = @id
                """;
            cmd.Parameters.AddWithValue("@id", post.Id);
            cmd.Parameters.AddWithValue("@href", href);
            cmd.Parameters.AddWithValue("@title", retitled ? title : post.Title);
            cmd.Parameters.AddWithValue("@formerTitle", (object)formerTitle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@commentsHref", relinked && !string.IsNullOrEmpty(item.CommentsHref) ? item.CommentsHref : DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "UPDATE PostContent SET Title = @title, Content = COALESCE(NULLIF(@content, ''), Content) WHERE PostId = @id";
            cmd.Parameters.AddWithValue("@id", post.Id);
            cmd.Parameters.AddWithValue("@title", retitled ? title : post.Title);
            cmd.Parameters.AddWithValue("@content", item.Content ?? "");
            cmd.ExecuteNonQuery();
        }

        this.logger.LogInformation(
            "Post {PostId} updated in place ({Change}): {OldTitle} -> {NewTitle}",
            post.Id, retitled && relinked ? "retitled, relinked" : retitled ? "retitled" : "relinked", post.Title, title);
        return true;
    }

    /// <summary>
    /// Tags are per-subscription (see IFeedRepository.AddTag) and the timeline
    /// reads them live, so there is nothing to stamp onto a post. Kept for the
    /// interface; a no-op.
    /// </summary>
    public void UpdateTags(NewsFeedItem item, string tags)
    {
    }

    public void MarkAsRead(NewsFeedItem item, bool isRead, RssUser user)
        => UpsertState(item, user, "IsRead", "ReadUtc", isRead);

    public void SavePost(NewsFeedItem item, RssUser user)
        => UpsertState(item, user, "IsSaved", "SavedUtc", true);

    public void UnsavePost(NewsFeedItem item, RssUser user)
        => UpsertState(item, user, "IsSaved", "SavedUtc", false);

    // Sparse state: a row exists only while something differs from "unread,
    // not saved", so clearing the last flag deletes the row.
    private void UpsertState(NewsFeedItem item, RssUser user, string flagColumn, string timeColumn, bool value)
    {
        using var connection = this.connections.OpenWrite();
        // Scoped to the user: saving a post is how it becomes visible to them
        // (OwnershipWhere admits any post with state), so an unscoped lookup
        // would let anyone read any feed's posts by saving them by id first.
        var postId = ResolvePostId(connection, item, user.Id);
        if (postId == null)
        {
            this.logger.LogWarning("Cannot set {Flag} for user {UserId}: post {Href} is unknown or not visible to them", flagColumn, user.Id, item.Href);
            return;
        }

        var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO UserPostState (UserId, PostId, {flagColumn}, {timeColumn})
            VALUES (@userId, @postId, @value, CASE WHEN @value THEN @now ELSE NULL END)
            ON CONFLICT(UserId, PostId) DO UPDATE SET
                {flagColumn} = excluded.{flagColumn},
                {timeColumn} = excluded.{timeColumn};
            DELETE FROM UserPostState WHERE UserId = @userId AND PostId = @postId AND IsRead = 0 AND IsSaved = 0;
            """;
        command.Parameters.AddWithValue("@userId", user.Id);
        command.Parameters.AddWithValue("@postId", postId.Value);
        command.Parameters.AddWithValue("@value", value ? 1 : 0);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// "Clear posts": moves every subscription's watermark to the feed's newest
    /// post so the timeline starts empty. Shared posts and read/saved state
    /// are untouched; the operation is reversible by clearing the watermark.
    /// </summary>
    public async Task DeleteAllItemsAsync(RssUser user)
    {
        using var connection = await this.connections.OpenWriteAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Subscriptions
            SET HideBeforeOrder = (SELECT MAX(p.PublishDateOrder) FROM Posts p WHERE p.FeedId = Subscriptions.FeedId)
            WHERE UserId = @userId
            """;
        command.Parameters.AddWithValue("@userId", user.Id);
        var updated = await command.ExecuteNonQueryAsync();
        this.logger.LogInformation("Set clear-posts watermark on {Count} subscriptions for user {UserId}", updated, user.Id);
    }

    public int GetItemCountForFeed(RssUser user, string feedUrl)
    {
        using var connection = this.connections.OpenRead();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM Posts p
            JOIN Feeds f ON f.Id = p.FeedId
            JOIN Subscriptions sub ON sub.FeedId = p.FeedId AND sub.UserId = @userId
            WHERE f.Url = @feedUrl AND (sub.HideBeforeOrder IS NULL OR p.PublishDateOrder > sub.HideBeforeOrder)
            """;
        command.Parameters.AddWithValue("@userId", user.Id);
        command.Parameters.AddWithValue("@feedUrl", feedUrl);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Dispose()
    {
        this.writeSemaphore.Dispose();
    }
}
