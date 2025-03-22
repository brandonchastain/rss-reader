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
builder.Services.AddSingleton<IFeedRepository>(sb =>
{
    return new SQLiteFeedRepository("Data Source=../RssApp/feeds.db", sb.GetRequiredService<ILogger<SQLiteFeedRepository>>());
});
builder.Services.AddSingleton<PersistedHiddenItems>();
builder.Services.AddSingleton<IItemRepository>(sb =>
{
    return new SQLiteItemRepository(
        "Data Source=../RssApp/newsFeedItems.db",
        sb.GetRequiredService<ILogger<SQLiteItemRepository>>(),
        sb.GetRequiredService<IFeedRepository>());
});
//builder.Services.AddSingleton<IFeedClient, FeedClient>();
builder.Services.AddSingleton<RssDeserializer>();
var app = builder.Build();

// do the export
var feedStore = app.Services.GetRequiredService<IFeedRepository>();
var itemStore = app.Services.GetRequiredService<IItemRepository>();
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
            var items = itemStore.GetItems(feed);

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