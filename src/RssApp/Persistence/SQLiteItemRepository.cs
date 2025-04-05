
using System.Data.Common;
using System.Data.SQLite;
using RssApp.Contracts;

namespace RssApp.Persistence;

public class SQLiteItemRepository : IItemRepository
{
    private readonly string connectionString;
    private readonly ILogger<SQLiteItemRepository> logger;
    private readonly IFeedRepository feedStore;
    private readonly IUserRepository userStore;

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
                    FOREIGN KEY (UserId) REFERENCES Users(Id),
                    UNIQUE(FeedUrl, UserId, NewsFeedItemId, Href)
                )";
            command.ExecuteNonQuery();
        }
    }

    public async Task<IEnumerable<NewsFeedItem>> GetItemsAsync(NewsFeed feed)
    {
        var set = new HashSet<NewsFeedItem>();
        var user = this.userStore.GetUserById(feed.UserId);
        var feedUrl = feed.FeedUrl;
        var updatedFeed = this.feedStore.GetFeeds(user).FirstOrDefault(f => f.FeedUrl == feedUrl);

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
                    i.UserId
                FROM NewsFeedItems i
                LEFT JOIN Feeds f ON i.FeedUrl = f.Url
                LEFT JOIN FeedTags t ON f.Id = t.FeedId
                WHERE i.FeedUrl = @feedUrl AND i.UserId = @userId
            """;
            command.Parameters.AddWithValue("@feedUrl", feedUrl);
            command.Parameters.AddWithValue("@userId", user.Id);
            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var item = this.ReadItemFromResults(reader);
                    
                    if (updatedFeed?.IsPaywalled == true)
                    {
                        item.IsPaywalled = true;
                    }

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

        var item = new NewsFeedItem(id, userId, title, href, commentsHref, publishDate, content)
        {
            FeedUrl = url,
            IsRead = isRead,
            FeedTags = string.IsNullOrWhiteSpace(tagName) ? [] : [tagName],
        };

        return item;
    }

    public NewsFeedItem GetItem(RssUser user, string href)
    {
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT * FROM NewsFeedItems
                LEFT JOIN Feeds ON NewsFeedItems.FeedUrl = Feeds.Url
                LEFT JOIN FeedTags ON NewsFeedItems.Id = FeedTags.FeedId
                WHERE Href = @href AND UserId = @userId
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

    public void AddItem(NewsFeedItem item)
    {
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
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
                    UserId
                ) 
                VALUES (@feedUrl, @newsFeedItemId, @href, @commentsHref, @title, @publishDate, @content, @userId)";
            command.Parameters.AddWithValue("@feedUrl", item.FeedUrl);
            command.Parameters.AddWithValue("@newsFeedItemId", item.Id);
            command.Parameters.AddWithValue("@href", item.Href);
            command.Parameters.AddWithValue("@commentsHref", item.CommentsHref);
            command.Parameters.AddWithValue("@title", item.Title);
            command.Parameters.AddWithValue("@publishDate", item.PublishDate);
            command.Parameters.AddWithValue("@content", item.Content);
            command.Parameters.AddWithValue("@userId", item.UserId);
            command.ExecuteNonQuery();
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
}