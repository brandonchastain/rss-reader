using System.Data.Common;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using RssApp.Contracts;

namespace RssApp.Data;

public class SQLiteItemRepository : IItemRepository, IDisposable
{
    private readonly string connectionString;
    private readonly ILogger<SQLiteItemRepository> logger;
    private readonly IFeedRepository feedStore;
    private readonly IUserRepository userStore;

    private SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);

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
            pragmaCommand.CommandText = "PRAGMA journal_mode=WAL;";
            pragmaCommand.ExecuteNonQuery();

            // Performance optimization settings
            pragmaCommand = connection.CreateCommand();
            pragmaCommand.CommandText = """
                PRAGMA cache_size=-20000;  -- Use 20MB of memory for page cache
                PRAGMA temp_store=MEMORY; -- Store temp tables and indices in memory
                PRAGMA synchronous=NORMAL; -- Slightly faster than FULL, still safe
                PRAGMA busy_timeout=5000; -- Wait up to 5s on locks
                PRAGMA mmap_size=268435456; -- Use memory mapping up to 256MB
            """;
            pragmaCommand.ExecuteNonQuery();

            var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS NewsFeedItems (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FeedUrl TEXT NOT NULL,
                    NewsFeedItemId TEXT,
                    Href TEXT,
                    CommentsHref TEXT,
                    Title TEXT,
                    PublishDate TEXT,
                    Content TEXT,
                    IsRead BOOLEAN DEFAULT 0,
                    UserId INTEGER NOT NULL,
                    ThumbnailUrl TEXT,
                    IsSaved BOOLEAN DEFAULT 0,
                    Tags TEXT,
                    FOREIGN KEY (UserId) REFERENCES Users(Id),
                    UNIQUE(FeedUrl, UserId, NewsFeedItemId, Href)
                )";
            command.ExecuteNonQuery();

            // FTS5 virtual table for full-text search
            command = connection.CreateCommand();
            command.CommandText = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS NewsFeedItems_fts
                USING fts5(
                    Title,
                    Content,
                    Href,
                    FeedUrl,
                    UserId UNINDEXED,
                    content='NewsFeedItems',
                    content_rowid='Id'
                );";
            command.ExecuteNonQuery();

            // Trigger to keep the FTS table in sync with the main table
            command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TRIGGER IF NOT EXISTS NewsFeedItems_after_insert
                AFTER INSERT ON NewsFeedItems
                BEGIN
                    INSERT INTO NewsFeedItems_fts (rowid, Title, Content, Href, FeedUrl, UserId)
                    VALUES (new.Id, new.Title, new.Content, new.Href, new.FeedUrl, new.UserId);
                END;";
            command.ExecuteNonQuery();

            // Update trigger
            command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TRIGGER IF NOT EXISTS NewsFeedItems_au AFTER UPDATE ON NewsFeedItems
                BEGIN
                    INSERT INTO NewsFeedItems_fts(NewsFeedItems_fts, rowid, Title, Content, Href, FeedUrl, UserId)
                    VALUES('delete', old.Id, old.Title, old.Content, old.Href, old.FeedUrl, old.UserId);
                    INSERT INTO NewsFeedItems_fts(rowid, Title, Content, Href, FeedUrl, UserId)
                    VALUES (new.Id, new.Title, new.Content, new.Href, new.FeedUrl, new.UserId);
                END;";
            command.ExecuteNonQuery();

            command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TRIGGER IF NOT EXISTS NewsFeedItems_after_delete
                AFTER DELETE ON NewsFeedItems
                BEGIN
                    DELETE FROM NewsFeedItems_fts WHERE rowid = old.Id;
                END;";
            command.ExecuteNonQuery();

            // Rebuild the FTS table (run if db gets out of sync with fts5)
            // logger.LogWarning("Rebuilding FTS table...");
            // command = connection.CreateCommand();
            // command.CommandText = @"INSERT INTO NewsFeedItems_fts(NewsFeedItems_fts) VALUES('rebuild');";
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
                        i.NewsFeedItemId,
                        i.Href,
                        i.CommentsHref,
                        i.Title,
                        i.PublishDate,
                        i.Content,
                        i.IsRead,
                        t.TagName,
                        i.UserId,
                        i.ThumbnailUrl,
                        s.SavedDate
                    FROM NewsFeedItems i
                    LEFT JOIN Feeds f
                        ON i.FeedUrl = f.Url
                    LEFT JOIN FeedTags t
                        ON f.Id = t.FeedId
                    LEFT JOIN SavedPosts s
                        ON i.Href = s.Href 
                        AND i.FeedUrl = s.FeedUrl 
                        AND i.UserId = s.UserId
                    WHERE (i.Id IN (
                        SELECT rowid FROM NewsFeedItems_fts
                        WHERE NewsFeedItems_fts MATCH @query)
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
        await this.semaphore.WaitAsync();

        try
        {
            // this.logger.LogInformation($"GetItemsAsync: feedUrl={feed.FeedUrl}, isFilterUnread={isFilterUnread}, isFilterSaved={isFilterSaved}, filterTag={filterTag}, page={page}, pageSize={pageSize}");
            var sw = Stopwatch.StartNew();
            var items = new List<NewsFeedItem>();
            var user = this.userStore.GetUserById(feed.UserId);

            using (var connection = new SqliteConnection(this.connectionString))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    WITH LatestItems AS (
                        SELECT Href, MAX(PublishDate) as MaxDate
                        FROM NewsFeedItems
                        WHERE UserId = @userId
                        GROUP BY Href
                    )
                    SELECT
                        i.FeedUrl,
                        i.NewsFeedItemId,
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
                    FROM NewsFeedItems i
                    INNER JOIN LatestItems li 
                        ON i.Href = li.Href 
                        AND i.PublishDate = li.MaxDate
                """;

                command.CommandText += " WHERE 1=1";
                command.Parameters.AddWithValue("@userId", user.Id);

                if (feed.FeedUrl != "%")
                {
                    command.CommandText += " AND i.FeedUrl LIKE @feedUrl";
                    command.Parameters.AddWithValue("@feedUrl", feed.FeedUrl);
                }

                if (isFilterUnread)
                {
                    command.CommandText += " AND i.IsRead = @isRead";
                    command.Parameters.AddWithValue("@isRead", false);
                }

                if (isFilterSaved)
                {
                    command.CommandText += " AND s.IsRead = 1";
                }

                if (!string.IsNullOrWhiteSpace(filterTag))
                {
                    command.CommandText += " AND t.Tags CONTAINS @tagName";
                    command.Parameters.AddWithValue("@tagName", filterTag);
                }

                command.CommandText += """
                    GROUP BY i.Href, i.FeedUrl, i.NewsFeedItemId, i.CommentsHref, i.Title, 
                             i.PublishDate, i.Content, i.IsRead, i.UserId, i.ThumbnailUrl,
                             i.IsSaved
                """;

                command.CommandText += " ORDER BY i.PublishDate DESC /* USING INDEX idx_items_timeline */";

                if (pageSize != null)
                {
                    command.CommandText += " LIMIT @pageSize";
                    command.Parameters.AddWithValue("@pageSize", pageSize);

                    if (lastId != null && lastPublishDate != null)
                    {
                        command.CommandText = command.CommandText.Replace("WHERE 1=1", """
                            WHERE (i.PublishDate < @lastPublishDate OR 
                                  (i.PublishDate = @lastPublishDate AND i.Id < @lastId))
                        """);
                        command.Parameters.AddWithValue("@lastId", lastId);
                        command.Parameters.AddWithValue("@lastPublishDate", lastPublishDate);
                    }
                }

                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var item = this.ReadItemFromResults(reader);
                        items.Add(item);
                    }
                }
            }

            this.logger.LogInformation($"GetItemsAsync took {sw.ElapsedMilliseconds}ms");
            return items;
        }
        finally
        {
            this.semaphore.Release();
        }
    }

    private NewsFeedItem ReadItemFromResults(DbDataReader reader)
    {
        var id = reader.IsDBNull(reader.GetOrdinal("NewsFeedItemId")) ? "" : reader.GetString(reader.GetOrdinal("NewsFeedItemId"));
        var userId = reader.IsDBNull(reader.GetOrdinal("UserId")) ? 1 : reader.GetInt32(reader.GetOrdinal("UserId"));
        var href = reader.IsDBNull(reader.GetOrdinal("Href")) ? "" : reader.GetString(reader.GetOrdinal("Href"));
        var commentsHref = reader.IsDBNull(reader.GetOrdinal("CommentsHref")) ? "" : reader.GetString(reader.GetOrdinal("CommentsHref"));
        var title = reader.IsDBNull(reader.GetOrdinal("Title")) ? "" : reader.GetString(reader.GetOrdinal("Title"));
        var publishDate = reader.IsDBNull(reader.GetOrdinal("PublishDate")) ? "" : reader.GetString(reader.GetOrdinal("PublishDate"));
        var content = reader.IsDBNull(reader.GetOrdinal("Content")) ? "" : reader.GetString(reader.GetOrdinal("Content"));
        var url = reader.IsDBNull(reader.GetOrdinal("FeedUrl")) ? "" : reader.GetString(reader.GetOrdinal("FeedUrl"));
        var isRead = reader.IsDBNull(reader.GetOrdinal("IsRead")) ? false : reader.GetBoolean(reader.GetOrdinal("IsRead"));
        var tagName = reader.IsDBNull(reader.GetOrdinal("TagName")) ? "" : reader.GetString(reader.GetOrdinal("TagName"));
        var thumbnailUrl = reader.IsDBNull(reader.GetOrdinal("ThumbnailUrl")) ? "" : reader.GetString(reader.GetOrdinal("ThumbnailUrl"));
        var isPaywalled = reader.IsDBNull(reader.GetOrdinal("IsPaywalled")) ? false : reader.GetBoolean(reader.GetOrdinal("IsPaywalled"));
        var isSaved = false;

        // Check if SavedPosts table was joined in the query
        if (reader.GetSchemaTable().Columns.Contains("SavedDate"))
        {
            isSaved = !reader.IsDBNull(reader.GetOrdinal("SavedDate"));
        }
        
        
        var item = new NewsFeedItem(id, userId, title, href, commentsHref, publishDate, content, thumbnailUrl)
                        {
            FeedUrl = url,
            IsRead = isRead,
            FeedTags = string.IsNullOrWhiteSpace(tagName) ? [] : [tagName],
            IsPaywalled = isPaywalled,
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
                SELECT * FROM NewsFeedItems
                LEFT JOIN Feeds ON NewsFeedItems.FeedUrl = Feeds.Url
                LEFT JOIN FeedTags ON NewsFeedItems.Id = FeedTags.FeedId
                WHERE NewsFeedItems.Href = @href AND NewsFeedItems.UserId = @userId
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

    public void AddItems(IEnumerable<NewsFeedItem> items)
    {
        this.semaphore.Wait();
        try
        {
            using (var connection = new SqliteConnection(this.connectionString))
            {
                connection.Open();

                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        foreach (var item in items)
                        {
                            try
                            {
                                var alreadyStored = this.GetItem(this.userStore.GetUserById(item.UserId), item.Href);
                                if (alreadyStored != null)
                                {
                                    //this.logger.LogInformation($"Item already exists in the database: {item.Href}");
                                    continue;
                                }
                                var command = connection.CreateCommand();
                                command.CommandText = @"
                                    INSERT INTO NewsFeedItems (
                                        FeedUrl,
                                        NewsFeedItemId,
                                        Href,
                                        CommentsHref,
                                        Title,
                                        PublishDate,
                                        Content,
                                        UserId,
                                        ThumbnailUrl
                                    ) 
                                    VALUES (@feedUrl, @newsFeedItemId, @href, @commentsHref, @title, @publishDate, @content, @userId, @thumbnailUrl)";
                                command.Parameters.AddWithValue("@feedUrl", item.FeedUrl ?? "");
                                command.Parameters.AddWithValue("@newsFeedItemId", item.Id ?? "");
                                command.Parameters.AddWithValue("@href", item.Href ?? "");
                                command.Parameters.AddWithValue("@commentsHref", (object?)item.CommentsHref ?? DBNull.Value);
                                command.Parameters.AddWithValue("@title", item.Title ?? "");
                                command.Parameters.AddWithValue("@publishDate", item.PublishDate ?? "");
                                command.Parameters.AddWithValue("@content", (object?)item.Content ?? DBNull.Value);
                                command.Parameters.AddWithValue("@userId", item.UserId);
                                command.Parameters.AddWithValue("@thumbnailUrl", (object?)item.ThumbnailUrl ?? DBNull.Value);
                                command.ExecuteNonQuery();
                            }
                            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && ex.Message.Contains("UNIQUE"))
                            {
                                // A duplicate entry was found, just skip it.
                                this.logger.LogWarning(ex, "Unique constraint violation while adding items to SQLite database");
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

    public void MarkAsRead(NewsFeedItem item, bool isRead)
    {
        using (var connection = new SqliteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE NewsFeedItems
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
                INSERT OR REPLACE INTO SavedPosts (UserId, FeedUrl, Href, SavedDate)
                VALUES (@userId, @feedUrl, @href, @savedDate)";
            command.Parameters.AddWithValue("@userId", user.Id);
            command.Parameters.AddWithValue("@feedUrl", item.FeedUrl);
            command.Parameters.AddWithValue("@href", item.Href);
            command.Parameters.AddWithValue("@savedDate", DateTime.UtcNow.ToString("o"));
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
                DELETE FROM SavedPosts 
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