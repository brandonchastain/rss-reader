using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RssApp.Contracts;
using RssReader.Server.Services;

namespace RssApp.Data;

public class SQLiteItemRepository : IItemRepository, IDisposable
{
    private readonly string writeConnectionString;
    private readonly string readConnectionString;
    private readonly ILogger<SQLiteItemRepository> logger;
    private readonly IFeedRepository feedStore;
    private readonly IUserRepository userStore;
    private readonly FeedThumbnailRetriever feedThumbnailRetriever;

    // Serialize writes — SQLite allows only one writer at a time.
    private readonly SemaphoreSlim writeSemaphore = new SemaphoreSlim(1, 1);

    public SQLiteItemRepository(
        string writeConnectionString,
        string readConnectionString,
        ILogger<SQLiteItemRepository> logger,
        IFeedRepository feedStore,
        IUserRepository userStore,
        FeedThumbnailRetriever feedThumbnailRetriever)
    {
        this.writeConnectionString = writeConnectionString;
        this.readConnectionString = readConnectionString;
        this.logger = logger;
        this.feedStore = feedStore;
        this.userStore = userStore;
        this.feedThumbnailRetriever = feedThumbnailRetriever ?? throw new ArgumentNullException(nameof(feedThumbnailRetriever));
        this.InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using (var connection = new SqliteConnection(this.writeConnectionString))
        {
            connection.OpenWithWritePragmas();

            // WAL mode is persistent — only needs to be set once per database file.
            var walCmd = connection.CreateCommand();
            walCmd.CommandText = "PRAGMA journal_mode=WAL;";
            walCmd.ExecuteNonQuery();

            var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Items (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FeedUrl TEXT NOT NULL,
                    Href TEXT,
                    CommentsHref TEXT,
                    Title TEXT,
                    PublishDateOrder INTEGER NOT NULL DEFAULT 0,
                    PublishDate TEXT,
                    Content TEXT,
                    IsRead BOOLEAN DEFAULT 0,
                    UserId INTEGER NOT NULL,
                    ThumbnailUrl TEXT,
                    IsSaved BOOLEAN DEFAULT 0,
                    Tags TEXT DEFAULT '',
                    FOREIGN KEY (UserId) REFERENCES Users(Id),
                    UNIQUE(FeedUrl, UserId, Href)
                )";
            command.ExecuteNonQuery();

            command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS ItemContent (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FeedUrl TEXT NOT NULL,
                    Href TEXT,
                    Title TEXT,
                    PublishDateOrder INTEGER NOT NULL DEFAULT 0,
                    PublishDate TEXT,
                    Content TEXT,
                    UserId INTEGER NOT NULL,
                    UNIQUE (FeedUrl, UserId, Href),
                    FOREIGN KEY (UserId) REFERENCES Users(Id)
                )";
            command.ExecuteNonQuery();

            // FTS5 virtual table for full-text search
            command = connection.CreateCommand();
            command.CommandText = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS Items_fts
                USING fts5(
                    Title,
                    Content,
                    Href,
                    FeedUrl,
                    UserId UNINDEXED,
                    content='ItemContent',
                    content_rowid='Id'
                );";
            command.ExecuteNonQuery();

            // Trigger to keep the FTS table in sync with the main table
            command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TRIGGER IF NOT EXISTS Items_after_insert
                AFTER INSERT ON ItemContent
                BEGIN
                    INSERT INTO Items_fts (rowid, Title, Content, Href, FeedUrl, UserId)
                    VALUES (new.Id, new.Title, new.Content, new.Href, new.FeedUrl, new.UserId);
                END;";
            command.ExecuteNonQuery();

            // Update trigger
            command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TRIGGER IF NOT EXISTS Items_au AFTER UPDATE ON ItemContent
                BEGIN
                    INSERT INTO Items_fts(Items_fts, rowid, Title, Content, Href, FeedUrl, UserId)
                    VALUES('delete', old.Id, old.Title, old.Content, old.Href, old.FeedUrl, old.UserId);
                    INSERT INTO Items_fts(rowid, Title, Content, Href, FeedUrl, UserId)
                    VALUES (new.Id, new.Title, new.Content, new.Href, new.FeedUrl, new.UserId);
                END;";
            command.ExecuteNonQuery();

            command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TRIGGER IF NOT EXISTS Items_after_delete
                AFTER DELETE ON ItemContent
                BEGIN
                    DELETE FROM Items_fts WHERE rowid = old.Id;
                END;";
            command.ExecuteNonQuery();

            // Rebuild the FTS table (run if db gets out of sync with fts5)
            // logger.LogWarning("Rebuilding FTS table...");
            // command = connection.CreateCommand();
            // command.CommandText = @"INSERT INTO Items_fts(Items_fts) VALUES('rebuild');";
            // command.ExecuteNonQuery();
            // logger.LogWarning("Done rebuilding FTS table...");
        }
    }

    public async Task<IEnumerable<NewsFeedItem>> SearchItemsAsync(string query, RssUser user, int page, int pageSize)
    {
        var set = new HashSet<NewsFeedItem>();

        using (var connection = new SqliteConnection(this.readConnectionString))
        {
            await connection.OpenWithReadPragmasAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    i.Id,
                    i.FeedUrl,
                    i.Href,
                    i.CommentsHref,
                    i.Title,
                    i.PublishDateOrder,
                    i.PublishDate,
                    i.Content,
                    i.IsRead,
                    i.UserId,
                    i.ThumbnailUrl,
                    i.IsSaved,
                    i.Tags
                FROM Items i
                LEFT JOIN Feeds f
                    ON i.FeedUrl = f.Url
                WHERE (i.Id IN (
                    SELECT rowid FROM Items_fts
                    WHERE Items_fts MATCH @query)
                    OR i.Title LIKE @plainQuery)
                AND i.UserId = @userId
                ORDER BY i.PublishDateOrder DESC, i.PublishDate DESC
                LIMIT @pageSize OFFSET @offset
            """;
            
            command.Parameters.AddWithValue("@query", $"\"{query}\"");
            command.Parameters.AddWithValue("@plainQuery", $"%{query}%");
            command.Parameters.AddWithValue("@userId", user.Id);
            command.Parameters.AddWithValue("@pageSize", pageSize);
            command.Parameters.AddWithValue("@offset", page * pageSize);

            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var item = this.ReadItemFromResults(reader);

                    if (!set.Contains(item))
                    {
                        set.Add(item);
                    }

                    set.TryGetValue(item, out var storedItem);
                    storedItem.FeedTags = storedItem.FeedTags.Union(item.FeedTags).ToList();
                }
            }
        }

        return set.ToList();
    }

    public async Task<IEnumerable<NewsFeedItem>> GetItemsAsync(
        NewsFeed feed,
        bool isFilterUnread,
        bool isFilterSaved,
        string filterTag,
        int? page = null,
        int? pageSize = null,
        long? lastId = null,
        string lastPublishDate = null)
    {
        var items = new List<NewsFeedItem>();
        var user = this.userStore.GetUserById(feed.UserId);

        using (var connection = new SqliteConnection(this.readConnectionString))
        {
            await connection.OpenWithReadPragmasAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    i.Id,
                    i.FeedUrl,
                    i.Href,
                    i.CommentsHref,
                    i.Title,
                    i.PublishDateOrder,
                    i.PublishDate,
                    i.Content,
                    i.IsRead,
                    i.UserId,
                    i.ThumbnailUrl,
                    i.IsSaved,
                    i.Tags
                FROM Items i
            """;

            command.CommandText += " WHERE UserId=@userId";
            command.Parameters.AddWithValue("@userId", user.Id);

            if (feed.Href != "%")
            {
                command.CommandText += " AND i.FeedUrl LIKE @feedUrl";
                command.Parameters.AddWithValue("@feedUrl", feed.Href);
            }

            if (isFilterUnread)
            {
                command.CommandText += " AND i.IsRead = 0";
            }

            if (isFilterSaved)
            {
                command.CommandText += " AND i.IsSaved = 1";
            }

            if (!string.IsNullOrWhiteSpace(filterTag))
            {
                command.CommandText += " AND i.Tags LIKE @tagName";
                command.Parameters.AddWithValue("@tagName", $"%{filterTag}%");
            }

            command.CommandText += " ORDER BY i.PublishDateOrder DESC, i.PublishDate DESC /* USING INDEX idx_items_timeline */";
            pageSize ??= 20; // Default page size if not provided
            page ??= 0;
            command.CommandText += " LIMIT @pageSize OFFSET @offset";
            command.Parameters.AddWithValue("@pageSize", pageSize);
            command.Parameters.AddWithValue("@offset", page * pageSize);

            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var item = this.ReadItemFromResults(reader);

                    if (item.FeedTags == null || !item.FeedTags.Any())
                    {
                        item.FeedTags = feed.Tags;
                    }
                    items.Add(item);
                }
            }
        }

        return items;
    }

    private NewsFeedItem ReadItemFromResults(DbDataReader reader)
    {
        var id = reader.IsDBNull(reader.GetOrdinal("Id")) ? "" : reader.GetString(reader.GetOrdinal("Id"));
        var userId = reader.IsDBNull(reader.GetOrdinal("UserId")) ? 1 : reader.GetInt32(reader.GetOrdinal("UserId"));
        var href = reader.IsDBNull(reader.GetOrdinal("Href")) ? "" : reader.GetString(reader.GetOrdinal("Href"));
        var commentsHref = reader.IsDBNull(reader.GetOrdinal("CommentsHref")) ? "" : reader.GetString(reader.GetOrdinal("CommentsHref"));
        var title = reader.IsDBNull(reader.GetOrdinal("Title")) ? "" : reader.GetString(reader.GetOrdinal("Title"));
        var publishDateOrder = reader.IsDBNull(reader.GetOrdinal("PublishDateOrder")) ? 0 : reader.GetInt64(reader.GetOrdinal("PublishDateOrder"));
        var publishDate = reader.IsDBNull(reader.GetOrdinal("PublishDate")) ? "" : reader.GetString(reader.GetOrdinal("PublishDate"));
        string content = reader.IsDBNull(reader.GetOrdinal("Content")) ? null : reader.GetString(reader.GetOrdinal("Content"));
        var url = reader.IsDBNull(reader.GetOrdinal("FeedUrl")) ? "" : reader.GetString(reader.GetOrdinal("FeedUrl"));
        var isRead = reader.IsDBNull(reader.GetOrdinal("IsRead")) ? false : reader.GetBoolean(reader.GetOrdinal("IsRead"));
        var tags = reader.IsDBNull(reader.GetOrdinal("Tags")) ? "" : reader.GetString(reader.GetOrdinal("Tags"));
        var thumbnailUrl = reader.IsDBNull(reader.GetOrdinal("ThumbnailUrl")) ? "" : reader.GetString(reader.GetOrdinal("ThumbnailUrl"));
        //var isPaywalled = reader.IsDBNull(reader.GetOrdinal("IsPaywalled")) ? false : reader.GetBoolean(reader.GetOrdinal("IsPaywalled"));
        var isSaved = reader.IsDBNull(reader.GetOrdinal("IsSaved")) ? false : reader.GetBoolean(reader.GetOrdinal("IsSaved"));
        
        var item = new NewsFeedItem(id, userId, title, href, commentsHref, publishDate, content, thumbnailUrl)
        {
            FeedUrl = url,
            IsRead = isRead,
            FeedTags = string.IsNullOrWhiteSpace(tags) ? [] : tags.Split(","),
            IsSaved = isSaved,
            PublishDateOrder = publishDateOrder
        };

        // logger.LogInformation("DEBUG: isread: {IsRead}", isRead);

        return item;
    }

    public NewsFeedItem GetItem(RssUser user, string href)
    {
        using (var connection = new SqliteConnection(this.readConnectionString))
        {
            connection.OpenWithReadPragmas();
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT * FROM Items
                LEFT JOIN Feeds ON Items.FeedUrl = Feeds.Url
                WHERE Items.Href = @href AND Items.UserId = @userId
            """;
            command.Parameters.AddWithValue("@href", href);
            command.Parameters.AddWithValue("@userId", user.Id);

            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }

                var item = this.ReadItemFromResults(reader);
                return item;
            }
        }
    }

    public NewsFeedItem GetItem(RssUser user, int itemId)
    {
        using (var connection = new SqliteConnection(this.readConnectionString))
        {
            connection.OpenWithReadPragmas();
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT * FROM Items
                LEFT JOIN Feeds ON Items.FeedUrl = Feeds.Url
                WHERE Items.Id = @id AND Items.UserId = @userId
            """;
            command.Parameters.AddWithValue("@id", itemId);
            command.Parameters.AddWithValue("@userId", user.Id);

            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }

                var item = this.ReadItemFromResults(reader);
                return item;
            }
        }
    }

    public string GetItemContent(NewsFeedItem item)
    {
        using (var connection = new SqliteConnection(this.readConnectionString))
        {
            connection.OpenWithReadPragmas();
            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Content 
                FROM ItemContent 
                WHERE FeedUrl = @feedUrl AND Href = @href AND UserId = @userId";
            command.Parameters.AddWithValue("@feedUrl", item.FeedUrl);
            command.Parameters.AddWithValue("@href", item.Href);
            command.Parameters.AddWithValue("@userId", item.UserId);

            var content = command.ExecuteScalar() as string;
            //logger.LogError("content: {Content}", content);
            return content ?? string.Empty;
        }
    }

    private const int InsertBatchSize = 25;

    public async Task AddItemsAsync(IEnumerable<NewsFeedItem> items)
    {
        await this.writeSemaphore.WaitAsync();

        try
        {
            var itemList = items.ToList();
            if (itemList.Count == 0) return;

            // Defensive check: AddItemsAsync expects all items from the same user.
            var userId = itemList[0].UserId;
            if (itemList.Any(i => i.UserId != userId))
            {
                this.logger.LogError("AddItemsAsync received items for multiple users — this is not supported");
                throw new InvalidOperationException("AddItemsAsync must receive items for a single user only.");
            }

            // Pre-fetch existing hrefs using the read connection (non-blocking).
            var existingHrefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usersByFeed = new Dictionary<string, (RssUser User, NewsFeed Feed)>(StringComparer.OrdinalIgnoreCase);
            var feedTags = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in itemList)
            {
                if (!usersByFeed.ContainsKey(item.FeedUrl))
                {
                    var user = this.userStore.GetUserById(item.UserId);
                    var feed = this.feedStore.GetFeed(user, item.FeedUrl);
                    usersByFeed[item.FeedUrl] = (user, feed);
                    feedTags[item.FeedUrl] = feed.Tags ?? [];
                }
            }

            // Single query to fetch all existing hrefs for this user, avoiding per-item GetItem() calls.
            var sampleUserId = itemList[0].UserId;
            var feedUrls = usersByFeed.Keys.ToList();
            using (var readConn = new SqliteConnection(this.readConnectionString))
            {
                await readConn.OpenWithReadPragmasAsync();
                var cmd = readConn.CreateCommand();
                cmd.CommandText = "SELECT Href FROM Items WHERE UserId = @userId AND FeedUrl IN (" +
                    string.Join(",", feedUrls.Select((_, i) => $"@f{i}")) + ")";
                cmd.Parameters.AddWithValue("@userId", sampleUserId);
                for (int i = 0; i < feedUrls.Count; i++)
                    cmd.Parameters.AddWithValue($"@f{i}", feedUrls[i]);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    existingHrefs.Add(reader.GetString(0));
            }

            // Prepare new items (skip duplicates, resolve thumbnails).
            var newItems = new List<NewsFeedItem>();
            foreach (var item in itemList)
            {
                if (existingHrefs.Contains(item.Href))
                    continue;

                // Mark as seen to avoid duplicates within this batch.
                existingHrefs.Add(item.Href);

                item.ThumbnailUrl = item.GetThumbnailUrl();
                if (string.IsNullOrWhiteSpace(item.ThumbnailUrl))
                {
                    try
                    {
                        var (_, feed) = usersByFeed[item.FeedUrl];
                        item.ThumbnailUrl = await this.feedThumbnailRetriever.RetrieveThumbnailUrlAsync(feed);
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogWarning(ex, "Thumbnail retrieval failed for {FeedUrl}, skipping", item.FeedUrl);
                    }
                }

                newItems.Add(item);
            }

            if (newItems.Count == 0) return;

            // Insert in batches, releasing the write lock briefly between batches.
            for (int batchStart = 0; batchStart < newItems.Count; batchStart += InsertBatchSize)
            {
                var batch = newItems.Skip(batchStart).Take(InsertBatchSize);

                using (var connection = new SqliteConnection(this.writeConnectionString))
                {
                    await connection.OpenWithWritePragmasAsync();
                    using var transaction = connection.BeginTransaction();
                    try
                    {
                        foreach (var item in batch)
                        {
                            try
                            {
                                var command = connection.CreateCommand();
                                command.CommandText = @"
                                    INSERT INTO Items (
                                        FeedUrl, Href, CommentsHref, Title,
                                        PublishDateOrder, PublishDate, UserId,
                                        ThumbnailUrl, Tags
                                    ) 
                                    VALUES (@feedUrl, @href, @commentsHref, @title,
                                            @publishDateOrder, @publishDate, @userId,
                                            @thumbnailUrl, @tags)";
                                command.Parameters.AddWithValue("@feedUrl", item.FeedUrl ?? "");
                                command.Parameters.AddWithValue("@href", item.Href ?? "");
                                command.Parameters.AddWithValue("@commentsHref", (object)item.CommentsHref ?? DBNull.Value);
                                command.Parameters.AddWithValue("@title", item.Title ?? "");
                                command.Parameters.AddWithValue("@publishDateOrder", item.PublishDateOrder);
                                command.Parameters.AddWithValue("@publishDate", item.PublishDate ?? "");
                                command.Parameters.AddWithValue("@userId", item.UserId);
                                command.Parameters.AddWithValue("@thumbnailUrl", (object)item.ThumbnailUrl ?? DBNull.Value);
                                command.Parameters.AddWithValue("@tags", string.Join(",", feedTags[item.FeedUrl]));
                                await command.ExecuteNonQueryAsync();

                                command = connection.CreateCommand();
                                command.CommandText = @"
                                    INSERT INTO ItemContent (
                                        FeedUrl, Href, Title,
                                        PublishDateOrder, PublishDate, Content, UserId
                                    ) 
                                    VALUES (@feedUrl, @href, @title,
                                            @publishDateOrder, @publishDate, @content, @userId)";
                                command.Parameters.AddWithValue("@feedUrl", item.FeedUrl ?? "");
                                command.Parameters.AddWithValue("@href", item.Href ?? "");
                                command.Parameters.AddWithValue("@title", item.Title ?? "");
                                command.Parameters.AddWithValue("@publishDateOrder", item.PublishDateOrder);
                                command.Parameters.AddWithValue("@publishDate", item.PublishDate ?? "");
                                command.Parameters.AddWithValue("@content", item.Content ?? "");
                                command.Parameters.AddWithValue("@userId", item.UserId);
                                await command.ExecuteNonQueryAsync();
                            }
                            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && ex.Message.Contains("UNIQUE"))
                            {
                                this.logger.LogWarning(ex, "Unique constraint violation while adding items. Skipping duplicate.");
                            }
                        }

                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }

                // Yield between batches so read requests can acquire the write lock if needed.
                if (batchStart + InsertBatchSize < newItems.Count)
                    await Task.Yield();
            }
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Error adding items to SQLite database");
        }
        finally
        {
            this.writeSemaphore.Release();
        }
    }

    public void UpdateTags(NewsFeedItem item, string tags)
    {
        using (var connection = new SqliteConnection(this.writeConnectionString))
        {
            connection.OpenWithWritePragmas();
            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE Items
                SET Tags = @tags
                WHERE FeedUrl LIKE @feedUrl AND Href LIKE @href AND UserId = @userId";
            command.Parameters.AddWithValue("@feedUrl", item.FeedUrl);
            command.Parameters.AddWithValue("@href", item.Href);
            command.Parameters.AddWithValue("@tags", tags);
            command.Parameters.AddWithValue("@userId", item.UserId);
            command.ExecuteNonQuery();
        }
    }

    public void MarkAsRead(NewsFeedItem item, bool isRead, RssUser user)
    {
        using (var connection = new SqliteConnection(this.writeConnectionString))
        {
            connection.OpenWithWritePragmas();
            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE Items
                SET IsRead = @isRead
                WHERE FeedUrl LIKE @feedUrl AND Href LIKE @href AND UserId = @userId";
            command.Parameters.AddWithValue("@feedUrl", item.FeedUrl);
            command.Parameters.AddWithValue("@href", item.Href);
            command.Parameters.AddWithValue("@isRead", isRead ? 1 : 0);
            command.Parameters.AddWithValue("@userId", user.Id);
            command.ExecuteNonQuery();
        }
    }

    public void SavePost(NewsFeedItem item, RssUser user)
    {
        using (var connection = new SqliteConnection(this.writeConnectionString))
        {
            connection.OpenWithWritePragmas();
            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE Items
                SET IsSaved = 1
                WHERE UserId = @userId AND FeedUrl LIKE @feedUrl AND Href LIKE @href";
            command.Parameters.AddWithValue("@userId", user.Id);
            command.Parameters.AddWithValue("@feedUrl", item.FeedUrl);
            command.Parameters.AddWithValue("@href", item.Href);
            command.ExecuteNonQuery();
        }
    }

    public void UnsavePost(NewsFeedItem item, RssUser user)
    {
        using (var connection = new SqliteConnection(this.writeConnectionString))
        {
            connection.OpenWithWritePragmas();
            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE Items
                SET IsSaved = 0
                WHERE UserId = @userId AND FeedUrl = @feedUrl AND Href = @href";
            command.Parameters.AddWithValue("@userId", user.Id);
            command.Parameters.AddWithValue("@feedUrl", item.FeedUrl);
            command.Parameters.AddWithValue("@href", item.Href);
            command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        this.writeSemaphore.Dispose();
    }
}