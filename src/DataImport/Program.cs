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
    // Import feeds from csv
    var feedsToImport = new List<string>();
    using (var reader = new StreamReader(feedCsvFileName))
    {
        reader.ReadLine(); // skip header
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            var feed = NewsFeed.ReadFromCsv(line);
            feedStore.AddFeed(feed);
        }
    }

    // Import items from csv
    using (var reader = new StreamReader(itemCsvFileName))
    {
        reader.ReadLine(); // skip header
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            var item = NewsFeedItem.ReadFromCsv(line);
            itemStore.AddItem(item);
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine(ex);
}

