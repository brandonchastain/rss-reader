using RssApp.Components;
using RssApp.Persistence;
using RssApp.RssClient;
using RssApp.Serialization;

var cancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += delegate {
        cancellationTokenSource.Cancel();
};

const string userDbVar = "RSS_BC_USER_DB";
const string feedDbVar = "RSS_BC_FEED_DB";
const string itemDbVar = "RSS_BC_ITEM_DB";
const string testUserEnabledVar = "RSS_BC_ENABLE_TEST_USER";
var userDb = Environment.GetEnvironmentVariable(userDbVar) ?? "C:\\home\\data\\users.db";
var itemDb = Environment.GetEnvironmentVariable(itemDbVar) ?? "C:\\home\\data\\newsFeedItems.db";
var feedDb = Environment.GetEnvironmentVariable(feedDbVar) ?? "C:\\home\\data\\feeds.db";
var isTestUserEnabled = Environment.GetEnvironmentVariable(testUserEnabledVar) ?? "false";

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<IUserRepository>(sb =>
{
    return new SQLiteUserRepository($"Data Source={userDb}", sb.GetRequiredService<ILogger<SQLiteUserRepository>>());
});
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
        sb.GetRequiredService<IFeedRepository>(),
        sb.GetRequiredService<IUserRepository>());
});

builder.Services.AddSingleton<RssDeserializer>();
builder.Services.AddSingleton<FeedRefresher>();
builder.Services.AddTransient<IFeedClient>(sp =>
{
    var httpClient = sp.GetRequiredService<HttpClient>();
    var hiddenItems = sp.GetRequiredService<PersistedHiddenItems>();
    var logger = sp.GetRequiredService<ILogger<FeedClient>>();
    var persistedFeeds = sp.GetRequiredService<IFeedRepository>();
    var newsFeedItemStore = sp.GetRequiredService<IItemRepository>();
    var userStore = sp.GetRequiredService<IUserRepository>();
    return new FeedClient(httpClient, hiddenItems, logger, persistedFeeds, newsFeedItemStore, userStore, bool.Parse(isTestUserEnabled));
});

var app = builder.Build();

// instantiate feed client to trigger the cache reload time
var refresher = app.Services.GetRequiredService<FeedRefresher>();
await refresher.StartAsync(cancellationTokenSource.Token);

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

if (app.Environment.IsDevelopment())
{
    app.MapGet("/.auth/me", () => new
    {
        clientPrincipal = new {
            userDetails = "defaultuser"
        }
    });
}

app.Run();
