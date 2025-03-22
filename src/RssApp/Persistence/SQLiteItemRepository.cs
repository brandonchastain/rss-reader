
using System.Data.SQLite;
using RssApp.Contracts;

namespace RssApp.Persistence;

public class SQLiteItemRepository : IItemRepository
{
    private readonly string connectionString;
    private readonly ILogger<SQLiteItemRepository> logger;
    private readonly IFeedRepository feedStore;

    public SQLiteItemRepository(string connectionString,
    ILogger<SQLiteItemRepository> logger,
    IFeedRepository feedStore)
    {
        this.connectionString = connectionString;
        this.logger = logger;
        this.feedStore = feedStore;
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
                    UNIQUE(FeedUrl, NewsFeedItemId, Href)
                )";
            command.ExecuteNonQuery();
        }
    }

    public IEnumerable<NewsFeedItem> GetItems(NewsFeed feed)
    {
        var feedUrl = feed.FeedUrl;
        var updatedFeed = this.feedStore.GetFeeds().FirstOrDefault(f => f.FeedUrl == feedUrl);
        using (var connection = new SQLiteConnection(this.connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM NewsFeedItems WHERE FeedUrl = @feedUrl";
            command.Parameters.AddWithValue("@feedUrl", feedUrl);

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var id = reader.IsDBNull(reader.GetOrdinal("NewFeedItemId")) ? "" : reader.GetString(reader.GetOrdinal("NewsFeedItemId"));
                    var href = reader.IsDBNull(reader.GetOrdinal("Href")) ? "" : reader.GetString(reader.GetOrdinal("Href"));
                    var commentsHref = reader.IsDBNull(reader.GetOrdinal("CommentsHref")) ? "" : reader.GetString(reader.GetOrdinal("CommentsHref"));
                    var title = reader.IsDBNull(reader.GetOrdinal("Title")) ? "" : reader.GetString(reader.GetOrdinal("Title"));
                    var publishDate = reader.IsDBNull(reader.GetOrdinal("PublishDate")) ? "" : reader.GetString(reader.GetOrdinal("PublishDate"));
                    var content = reader.IsDBNull(reader.GetOrdinal("Content")) ? "" : reader.GetString(reader.GetOrdinal("Content"));
                    var url = reader.IsDBNull(reader.GetOrdinal("FeedUrl")) ? "" : reader.GetString(reader.GetOrdinal("FeedUrl"));
                    var isRead = reader.IsDBNull(reader.GetOrdinal("IsRead")) ? false : reader.GetBoolean(reader.GetOrdinal("IsRead"));
                    
                    var item = new NewsFeedItem(id, title, href, commentsHref, publishDate, content)
                    {
                        FeedUrl = url,
                        IsRead = isRead
                    };
                    
                    if (updatedFeed.IsPaywalled)
                    {
                        item.IsPaywalled = true;
                    }

                    //this.logger.LogInformation($"[DATABASE] returning item {item.Id}, {item.Href} feedUrl {item.FeedUrl}.");
                    yield return item;
                }
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
                    Content
                ) 
                VALUES (@feedUrl, @newsFeedItemId, @href, @commentsHref, @title, @publishDate, @content)";
            command.Parameters.AddWithValue("@feedUrl", item.FeedUrl);
            command.Parameters.AddWithValue("@newsFeedItemId", item.Id);
            command.Parameters.AddWithValue("@href", item.Href);
            command.Parameters.AddWithValue("@commentsHref", item.CommentsHref);
            command.Parameters.AddWithValue("@title", item.Title);
            command.Parameters.AddWithValue("@publishDate", item.PublishDate);
            command.Parameters.AddWithValue("@content", item.Content);
            command.ExecuteNonQuery();
            
            //this.logger.LogInformation($"[DATABASE] added item {item.Id} with feedUrl {item.FeedUrl}.");
            //var res = this.GetItems(item.FeedUrl);
            //this.logger.LogInformation($"[DATABASE] contains item {item.Id}? {res?.Contains(item)}");
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
                WHERE FeedUrl = @feedUrl AND Href = @href";
            command.Parameters.AddWithValue("@feedUrl", item.FeedUrl);
            command.Parameters.AddWithValue("@href", item.Href);
            command.Parameters.AddWithValue("@isRead", isRead);
            command.ExecuteNonQuery();
            this.logger.LogInformation($"[DATABASE] marked item {item.Id} with feedUrl {item.FeedUrl} as {(isRead ? "read" : "unread")}.");
        }

    }
}