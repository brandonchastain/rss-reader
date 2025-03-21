using Microsoft.Extensions.Caching.Memory;
using RssApp.Components;
using RssApp.Persistence;
using RssApp.RssClient;
using RssApp.Serialization;
using RssApp.Contracts;

var cancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += delegate {
    cancellationTokenSource.Cancel();
};

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IPersistedFeeds>(sb =>
{
    return new SqlLitePersistedFeeds("Data Source=../RssApp/feeds.db", sb.GetRequiredService<ILogger<SqlLitePersistedFeeds>>());
});
builder.Services.AddSingleton<PersistedHiddenItems>();
builder.Services.AddSingleton<INewsFeedItemStore>(sb =>
{
    return new SQLiteNewsFeedItemStore("Data Source=../RssApp/newsFeedItems.db", sb.GetRequiredService<ILogger<SQLiteNewsFeedItemStore>>());
});
//builder.Services.AddSingleton<IFeedClient, FeedClient>();
builder.Services.AddSingleton<RssDeserializer>();
var app = builder.Build();

// do the export
var feedStore = app.Services.GetRequiredService<IPersistedFeeds>();
var itemStore = app.Services.GetRequiredService<INewsFeedItemStore>();
var feeds = feedStore.GetFeeds();

var feedCsvFileName = "../feeds.csv";
var itemCsvFileName = "../items.csv";

try
{
    // Export feeds to CSV
    using (var writer = new StreamWriter(feedCsvFileName))
    {
        await NewsFeed.WriteCsvHeaderAsync(writer);
        foreach (NewsFeed feed in feeds)
        {
            await feed.WriteCsvAsync(writer);
        }
    }


    // Export items to CSV
    using (var writer = new StreamWriter(itemCsvFileName, true))
    {
        await NewsFeedItem.WriteCsvHeaderAsync(writer);
        foreach (var feed in feeds)
        {
            var items = itemStore.GetItems(feed.FeedUrl);

            foreach (var item in items)
            {
                await item.WriteCsvAsync(writer);
            }
        }
    }
}
catch (Exception ex)
{
    throw;
}