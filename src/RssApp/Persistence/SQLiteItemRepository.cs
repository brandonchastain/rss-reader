
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

    public IEnumerable<NewsFeedItem> GetItems(NewsFeed feed)
    {
        this.logger.LogInformation($"[DATABASE] getting items for feed {feed.FeedUrl}.");
        this.logger.LogInformation($"[DATABASE] getting items for user {feed.UserId}.");
        var user = this.userStore.GetUserById(feed.UserId);

        this.logger.LogInformation($"[DATABASE] getting items for user {feed.UserId} {user.Username}.");
        var feedUrl = feed.FeedUrl;
        var updatedFeed = this.feedStore.GetFeeds(user).FirstOrDefault(f => f.FeedUrl == feedUrl);

        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM NewsFeedItems WHERE FeedUrl = @feedUrl AND UserId = @userId";
            command.Parameters.AddWithValue("@feedUrl", feedUrl);
            command.Parameters.AddWithValue("@userId", user.Id);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var item = this.ReadItemFromResults(reader);
                    
                    if (updatedFeed.IsPaywalled)
                    {
                        item.IsPaywalled = true;
                    }

                    yield return item;
                }
            }
        }
    }

    private NewsFeedItem ReadItemFromResults(SQLiteDataReader reader)
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
        
        var item = new NewsFeedItem(id, userId, title, href, commentsHref, publishDate, content)
        {
            FeedUrl = url,
            IsRead = isRead
        };

        return item;
    }

    public NewsFeedItem GetItem(RssUser user, string href)
    {
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM NewsFeedItems WHERE Href = @href AND UserId = @userId";
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
        //this.logger.LogInformation($"[DATABASE] adding item {item.Id} with feedUrl {item.FeedUrl} href {item.Href}.");
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