using RssApp.Components;
using RssApp.Persistence;
using RssApp.RssClient;
using RssApp.Serialization;

var cancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += delegate {
        cancellationTokenSource.Cancel();
};

const string feedDbVar = "RSS_BC_FEED_DB";
const string itemDbVar = "RSS_BC_ITEM_DB";
var feedDb = "C:\\home\\data\\feeds.db";
var itemDb = "C:\\home\\data\\newsFeedItems.db";

// read feedDb from environment variable if set
if (Environment.GetEnvironmentVariable(feedDbVar) != null)
{
    feedDb = Environment.GetEnvironmentVariable(feedDbVar);
}

// read itemDb from environment variable if set
if (Environment.GetEnvironmentVariable(itemDbVar) != null)
{
    itemDb = Environment.GetEnvironmentVariable(itemDbVar);
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<IFeedRepository>(sb =>
{
    return new SQLiteFeedRepository($"Data Source={feedDb}", sb.GetRequiredService<ILogger<SQLiteFeedRepository>>());
});
builder.Services.AddSingleton<PersistedHiddenItems>();
builder.Services.AddSingleton<IItemRepository>(sb =>
{
    return new SQLiteItemRepository(
        $"Data Source={itemDb}",
        sb.GetRequiredService<ILogger<SQLiteItemRepository>>(),
        sb.GetRequiredService<IFeedRepository>());
});
builder.Services.AddSingleton<IFeedClient, FeedClient>();
builder.Services.AddSingleton<RssDeserializer>();
var app = builder.Build();

// instantiate feed client to trigger the cache reload time
app.Services.GetRequiredService<IFeedClient>();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();


app.Run();
