using System.Data.Common;
using System.Data.SQLite;
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
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            // Set WAL journal mode for better concurrency
            var pragmaCommand = connection.CreateCommand();
            pragmaCommand.CommandText = "PRAGMA journal_mode=WAL;";
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
                    FOREIGN KEY (UserId) REFERENCES Users(Id),
                    UNIQUE(FeedUrl, UserId, NewsFeedItemId, Href)
                )";
            command.ExecuteNonQuery();

            command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS SavedPosts (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserId INTEGER NOT NULL,
                    FeedUrl TEXT NOT NULL,
                    Href TEXT NOT NULL,
                    SavedDate TEXT NOT NULL,
                    FOREIGN KEY (UserId) REFERENCES Users(Id),
                    UNIQUE(UserId, FeedUrl, Href)
                )";
            command.ExecuteNonQuery();

            // indexes
            command = connection.CreateCommand();
            command.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_items_feedurl_userid
                ON NewsFeedItems (FeedUrl, UserId);";
            command.ExecuteNonQuery();

            command = connection.CreateCommand();
            command.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_items_href
                ON NewsFeedItems (Href);";
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

            // Rebuild the FTS table
            // command = connection.CreateCommand();
            // command.CommandText = @"INSERT INTO NewsFeedItems_fts(NewsFeedItems_fts) VALUES('rebuild');";
            // command.ExecuteNonQuery();
        }
    }

    public async Task<IEnumerable<NewsFeedItem>> SearchItemsAsync(string query, RssUser user, int page, int pageSize)
    {
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT * FROM NewsFeedItems
                WHERE Id IN (
                    SELECT rowid FROM NewsFeedItems_fts
                    WHERE NewsFeedItems_fts MATCH @query
                    LIMIT @pageSize OFFSET @offset
                )
                AND UserId = @userId
                ORDER BY PublishDate DESC
            """;
            command.Parameters.AddWithValue("@query", query);
            command.Parameters.AddWithValue("@userId", user.Id);
            command.Parameters.AddWithValue("@pageSize", pageSize);
            command.Parameters.AddWithValue("@offset", page * pageSize);

            using (var reader = await command.ExecuteReaderAsync())
            {

                var set = new HashSet<NewsFeedItem>();

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

                return set;
            }
        }
    }

    public async Task<IEnumerable<NewsFeedItem>> GetItemsAsync(
        NewsFeed feed,
        bool isFilterUnread,
        bool isFilterSaved,
        string filterTag,
        int? page = null,
        int? pageSize = null)
    {
        await this.semaphore.WaitAsync();

        try
        {
            // this.logger.LogInformation($"GetItemsAsync: feedUrl={feed.FeedUrl}, isFilterUnread={isFilterUnread}, isFilterSaved={isFilterSaved}, filterTag={filterTag}, page={page}, pageSize={pageSize}");
            var sw = Stopwatch.StartNew();
            var set = new HashSet<NewsFeedItem>();
            var user = this.userStore.GetUserById(feed.UserId);

            using (var connection = new SQLiteConnection(this.connectionString))
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
                        f.IsPaywalled,
                        s.SavedDate
                    FROM NewsFeedItems i
                    LEFT JOIN SavedPosts s
                        ON i.Href = s.Href 
                        AND i.FeedUrl = s.FeedUrl 
                        AND i.UserId = s.UserId
                    LEFT JOIN Feeds f
                        ON i.FeedUrl = f.Url
                    LEFT JOIN FeedTags t
                        ON f.Id = t.FeedId
                """;

                command.CommandText += " WHERE i.UserId = @userId";
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
                    command.CommandText += " AND s.SavedDate IS NOT NULL";
                }

                if (!string.IsNullOrWhiteSpace(filterTag))
                {
                    command.CommandText += " AND t.TagName = @tagName";
                    command.Parameters.AddWithValue("@tagName", filterTag);
                }

                command.CommandText += " GROUP BY i.Href HAVING MAX(i.PublishDate)";
                command.CommandText += " ORDER BY i.PublishDate DESC";

                if (page != null && pageSize != null)
                {
                    command.CommandText += " LIMIT @pageSize OFFSET @offset";
                    command.Parameters.AddWithValue("@pageSize", pageSize);
                    command.Parameters.AddWithValue("@offset", page * pageSize);
                }

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

            // this.logger.LogInformation($"GetItemsAsync took {sw.ElapsedMilliseconds}ms");
            return set.ToList();
        }
        finally
        {
            this.semaphore.Release();
        }
    }

    private NewsFeedItem ReadItemFromResults(DbDataReader reader)
    {
        var id = reader.IsDBNull(reader.GetOrdinal("NewFeedItemId")) ? "" : reader.GetString(reader.GetOrdinal("NewsFeedItemId"));
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
        isSaved = !reader.IsDBNull(reader.GetOrdinal("SavedDate"));
        
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
        using (var connection = new SQLiteConnection(this.connectionString))
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
            using (var connection = new SQLiteConnection(this.connectionString))
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
                                    this.logger.LogInformation($"Item already exists in the database: {item.Href}");
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
                                command.Parameters.AddWithValue("@feedUrl", item.FeedUrl);
                                command.Parameters.AddWithValue("@newsFeedItemId", item.Id);
                                command.Parameters.AddWithValue("@href", item.Href);
                                command.Parameters.AddWithValue("@commentsHref", item.CommentsHref);
                                command.Parameters.AddWithValue("@title", item.Title);
                                command.Parameters.AddWithValue("@publishDate", item.PublishDate);
                                command.Parameters.AddWithValue("@content", item.Content);
                                command.Parameters.AddWithValue("@userId", item.UserId);
                                command.Parameters.AddWithValue("@thumbnailUrl", item.ThumbnailUrl);
                                command.ExecuteNonQuery();
                            }
                            catch (SQLiteException ex) when (ex.ResultCode == SQLiteErrorCode.Constraint && ex.Message.Contains("UNIQUE"))
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
        using (var connection = new SQLiteConnection(this.connectionString))
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
        using (var connection = new SQLiteConnection(this.connectionString))
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
        using (var connection = new SQLiteConnection(this.connectionString))
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