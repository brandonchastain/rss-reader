using System.Data.Common;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RssApp.Contracts;

namespace RssApp.Data;

public class SQLiteItemRepository : IItemRepository, IDisposable
{
    private readonly string connectionString;
    private readonly ILogger<SQLiteItemRepository> logger;
    private readonly IFeedRepository feedStore;
    private readonly IUserRepository userStore;

    private SemaphoreSlim semaphore = new SemaphoreSlim(2, 2);

    public SQLiteItemRepository(
        string connectionString,
        ILogger<SQLiteItemRepository> logger,
        IFeedRepository feedStore,
        IUserRepository userStore)
    {
        this.connectionString = connectionString;
        this.logger = logger;
        this.feedStore = feedStore;
        this.userStore = userStore;
        this.InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.Open();
            // Set WAL journal mode for better concurrency
            var pragmaCommand = connection.CreateCommand();

            // Performance optimization settings
            pragmaCommand = connection.CreateCommand();
            pragmaCommand.CommandText = """
                PRAGMA journal_mode=WAL;    
                PRAGMA cache_size=-20000;  -- Use 20MB of memory for page cache
                PRAGMA temp_store=MEMORY; -- Store temp tables and indices in memory
                PRAGMA synchronous=NORMAL; -- Slightly faster than FULL, still safe
                PRAGMA busy_timeout=5000; -- Wait up to 5s on locks
                PRAGMA mmap_size=268435456; -- Use memory mapping up to 256MB
            """;
            pragmaCommand.ExecuteNonQuery();

            var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Items (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FeedUrl TEXT NOT NULL,
                    Href TEXT,
                    CommentsHref TEXT,
                    Title TEXT,
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
        await this.semaphore.WaitAsync();

        try
        {
            using (var connection = new SqliteConnection(this.connectionString))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT 
                        i.FeedUrl,
                        i.Href,
                        i.CommentsHref,
                        i.Title,
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
                    ORDER BY i.PublishDate DESC
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
        }
        finally
        {
            this.semaphore.Release();
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
        string? lastPublishDate = null)
    {
        var sw = Stopwatch.StartNew();
        await this.semaphore.WaitAsync();
        var lockWait = sw.ElapsedMilliseconds;

        try
        {
            var items = new List<NewsFeedItem>();
            var user = this.userStore.GetUserById(feed.UserId);

            using (var connection = new SqliteConnection(this.connectionString))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT
                        i.FeedUrl,
                        i.Href,
                        i.CommentsHref,
                        i.Title,
                        i.PublishDate,
                        i.Content,
                        i.IsRead,
                        i.UserId,
                        i.ThumbnailUrl,
                        i.IsSaved,
                        i.Tags
                    FROM Items i
                """;

                command.CommandText += " WHERE 1=1";
                command.Parameters.AddWithValue("@userId", user.Id);

                if (feed.Href != "%")
                {
                    command.CommandText += " AND i.FeedUrl LIKE @feedUrl";
                    command.Parameters.AddWithValue("@feedUrl", feed.Href);
                }

                if (isFilterUnread)
                {
                    command.CommandText += " AND i.IsRead = @isRead";
                    command.Parameters.AddWithValue("@isRead", false);
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

                command.CommandText += " ORDER BY i.PublishDate DESC /* USING INDEX idx_items_timeline */";
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
                        items.Add(item);
                    }
                }
            }

            // var feedTags = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
            // foreach (var item in items)
            // {
            //     if (!feedTags.ContainsKey(item.FeedUrl))
            //     {
            //         var feedItem = this.feedStore.GetFeed(user, item.FeedUrl);
            //         feedTags[item.FeedUrl] = feedItem.Tags;
            //     }

            //     item.FeedTags = feedTags[item.FeedUrl]?.ToList();
            // }            
            return items;
        }
        finally
        {
            this.semaphore.Release();
            this.logger.LogInformation("GetItems took {ElapsedMilliseconds} ms, lock wait: {LockWait} ms", 
                sw.ElapsedMilliseconds, lockWait);
        }
    }

    private NewsFeedItem ReadItemFromResults(DbDataReader reader)
    {
        //var id = reader.IsDBNull(reader.GetOrdinal("NewsFeedItemId")) ? "" : reader.GetString(reader.GetOrdinal("NewsFeedItemId"));
        var id = "1";
        var userId = reader.IsDBNull(reader.GetOrdinal("UserId")) ? 1 : reader.GetInt32(reader.GetOrdinal("UserId"));
        var href = reader.IsDBNull(reader.GetOrdinal("Href")) ? "" : reader.GetString(reader.GetOrdinal("Href"));
        var commentsHref = reader.IsDBNull(reader.GetOrdinal("CommentsHref")) ? "" : reader.GetString(reader.GetOrdinal("CommentsHref"));
        var title = reader.IsDBNull(reader.GetOrdinal("Title")) ? "" : reader.GetString(reader.GetOrdinal("Title"));
        var publishDate = reader.IsDBNull(reader.GetOrdinal("PublishDate")) ? "" : reader.GetString(reader.GetOrdinal("PublishDate"));
        var content = reader.IsDBNull(reader.GetOrdinal("Content")) ? "" : reader.GetString(reader.GetOrdinal("Content"));
        var url = reader.IsDBNull(reader.GetOrdinal("FeedUrl")) ? "" : reader.GetString(reader.GetOrdinal("FeedUrl"));
        var isRead = reader.IsDBNull(reader.GetOrdinal("IsRead")) ? false : reader.GetBoolean(reader.GetOrdinal("IsRead"));
        var tags = reader.IsDBNull(reader.GetOrdinal("Tags")) ? "" : reader.GetString(reader.GetOrdinal("Tags"));
        var thumbnailUrl = reader.IsDBNull(reader.GetOrdinal("ThumbnailUrl")) ? "" : reader.GetString(reader.GetOrdinal("ThumbnailUrl"));
        //var isPaywalled = reader.IsDBNull(reader.GetOrdinal("IsPaywalled")) ? false : reader.GetBoolean(reader.GetOrdinal("IsPaywalled"));
        var isSaved = reader.IsDBNull(reader.GetOrdinal("IsSaved"));;
        
        var item = new NewsFeedItem(id, userId, title, href, commentsHref, publishDate, content, thumbnailUrl)
        {
            Href = url,
            IsRead = isRead,
            FeedTags = string.IsNullOrWhiteSpace(tags) ? [] : tags.Split(","),
            IsSaved = isSaved
        };

        return item;
    }

    public NewsFeedItem? GetItem(RssUser user, string href)
    {
        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.Open();
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

    public string GetItemContent(NewsFeedItem item)
    {
        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.Open();
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

    public void AddItems(IEnumerable<NewsFeedItem> items)
    {
        this.semaphore.Wait();
        try
        {
            using (var connection = new SqliteConnection(this.connectionString))
            {
                connection.Open();

                // using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        var feedTags = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);

                        foreach (var item in items)
                        {
                            try
                            {
                                var user = this.userStore.GetUserById(item.UserId);

                                if (!feedTags.ContainsKey(item.FeedUrl))
                                {
                                    var feed = this.feedStore.GetFeed(user, item.FeedUrl);
                                    feedTags[item.FeedUrl] = feed.Tags ?? [];
                                }

                                var alreadyStored = this.GetItem(user, item.Href);
                                if (alreadyStored != null)
                                {
                                    this.logger.LogWarning($"Item already exists in the database: {item.Href}");
                                    continue;
                                }

                                item.SetThumbnailUrl(item.Content);

                                var command = connection.CreateCommand();
                                command.CommandText = @"
                                    INSERT INTO Items (
                                        FeedUrl,
                                        Href,
                                        CommentsHref,
                                        Title,
                                        PublishDate,
                                        UserId,
                                        ThumbnailUrl,
                                        Tags
                                    ) 
                                    VALUES (@feedUrl, @href, @commentsHref, @title, @publishDate, @userId, @thumbnailUrl, @tags)";
                                command.Parameters.AddWithValue("@feedUrl", item.FeedUrl ?? "");
                                command.Parameters.AddWithValue("@href", item.Href ?? "");
                                command.Parameters.AddWithValue("@commentsHref", (object?)item.CommentsHref ?? DBNull.Value);
                                command.Parameters.AddWithValue("@title", item.Title ?? "");
                                command.Parameters.AddWithValue("@publishDate", item.PublishDate ?? "");
                                command.Parameters.AddWithValue("@userId", item.UserId);
                                command.Parameters.AddWithValue("@thumbnailUrl", (object?)item.ThumbnailUrl ?? DBNull.Value);
                                command.Parameters.AddWithValue("@tags", string.Join(",", feedTags[item.FeedUrl]));
                                command.ExecuteNonQuery();

                                command = connection.CreateCommand();
                                command.CommandText = @"
                                    INSERT INTO ItemContent (
                                        FeedUrl,
                                        Href,
                                        Title,
                                        PublishDate,
                                        Content,
                                        UserId
                                    ) 
                                    VALUES (@feedUrl, @href, @title, @publishDate, @content, @userId)";
                                command.Parameters.AddWithValue("@feedUrl", item.FeedUrl ?? "");
                                command.Parameters.AddWithValue("@href", item.Href ?? "");
                                command.Parameters.AddWithValue("@title", item.Title ?? "");
                                command.Parameters.AddWithValue("@publishDate", item.PublishDate ?? "");
                                command.Parameters.AddWithValue("@content", item.Content ?? "");
                                command.Parameters.AddWithValue("@userId", item.UserId);
                                command.ExecuteNonQuery();
                            }
                            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && ex.Message.Contains("UNIQUE"))
                            {
                                // A duplicate entry was found, just skip it.
                                this.logger.LogWarning(ex, "Unique constraint violation while adding items to SQLite database");
                            }
                        }

                        // transaction.Commit();
                    }
                    catch
                    {
                        // transaction.Rollback();
                        throw;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Error adding items to SQLite database");
        }
        finally
        {
            this.semaphore.Release();
        }
    }

    public void UpdateTags(NewsFeedItem item, string tags)
    {
        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE Items
                SET Tags = @tags
                WHERE FeedUrl = @feedUrl AND Href = @href AND UserId = @userId";
            command.Parameters.AddWithValue("@feedUrl", item.FeedUrl);
            command.Parameters.AddWithValue("@href", item.Href);
            command.Parameters.AddWithValue("@tags", tags);
            command.Parameters.AddWithValue("@userId", item.UserId);
            command.ExecuteNonQuery();
        }
    }

    public void MarkAsRead(NewsFeedItem item, bool isRead)
    {
        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE Items
                SET IsRead = @isRead
                WHERE FeedUrl = @feedUrl AND Href = @href AND UserId = @userId";
            command.Parameters.AddWithValue("@feedUrl", item.FeedUrl);
            command.Parameters.AddWithValue("@href", item.Href);
            command.Parameters.AddWithValue("@isRead", isRead);
            command.Parameters.AddWithValue("@userId", item.UserId);
            command.ExecuteNonQuery();
        }
    }

    public void SavePost(NewsFeedItem item, RssUser user)
    {
        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE Items
                SET IsSaved = 1
                WHERE UserId = @userId AND FeedUrl = @feedUrl AND Href = @href";
            command.Parameters.AddWithValue("@userId", user.Id);
            command.Parameters.AddWithValue("@feedUrl", item.FeedUrl);
            command.Parameters.AddWithValue("@href", item.Href);
            command.ExecuteNonQuery();
        }
    }

    public void UnsavePost(NewsFeedItem item, RssUser user)
    {
        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.Open();
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
        this.semaphore.Dispose();
    }
}