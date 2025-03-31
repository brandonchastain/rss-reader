using System.Net;
using RssApp.Components;
using RssApp.Contracts;
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
const string cacheReloadIntervalMinsVar = "RSS_BC_CACHE_RELOAD_INTERVAL";
const string cacheReloadStartupDelayMinsVar = "RSS_BC_CACHE_STARTUP_DELAY";

var userDb = Environment.GetEnvironmentVariable(userDbVar) ?? "C:\\home\\data\\users.db";
var itemDb = Environment.GetEnvironmentVariable(itemDbVar) ?? "C:\\home\\data\\newsFeedItems.db";
var feedDb = Environment.GetEnvironmentVariable(feedDbVar) ?? "C:\\home\\data\\feeds.db";
var isTestUserEnabled = Environment.GetEnvironmentVariable(testUserEnabledVar) ?? "false";
string cacheReloadIntervalMins = Environment.GetEnvironmentVariable(cacheReloadIntervalMinsVar) ?? null;
string cacheReloadStartupDelayMins = Environment.GetEnvironmentVariable(cacheReloadStartupDelayMinsVar) ?? null;
TimeSpan? cacheReloadInterval = cacheReloadIntervalMins == null ? null : TimeSpan.FromMinutes(int.Parse(cacheReloadIntervalMins));
TimeSpan? cacheReloadStartupDelay = cacheReloadStartupDelayMins == null ? null : TimeSpan.FromMinutes(int.Parse(cacheReloadStartupDelayMins));

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLogging(loggingBuilder =>
{
    loggingBuilder.ClearProviders();
    loggingBuilder.AddConsole();
    loggingBuilder.AddDebug();
    loggingBuilder.AddAzureWebAppDiagnostics();
});

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
builder.Services.AddSingleton<FeedRefresher>(sp =>
{
    return new FeedRefresher(
        sp.GetRequiredService<HttpClient>(),
        sp.GetRequiredService<RssDeserializer>(),
        sp.GetRequiredService<ILogger<FeedClient>>(),
        sp.GetRequiredService<IFeedRepository>(),
        sp.GetRequiredService<IItemRepository>(),
        sp.GetRequiredService<IUserRepository>(),
        cacheReloadInterval, 
        cacheReloadStartupDelay);
})
.AddTransient<IFeedClient, FeedClient>();

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
    int index = 1;
    RssUser loggedInUser = null;
    var users = new RssUser[]{
        new RssUser("defaultuser", 1),
        new RssUser("default2", 2),
    };

    app.MapGet("/.auth/me", () => new []
    {
        new
        {
            user_id = loggedInUser?.Username ?? "defaultuser",
        }
    });

    app.MapGet("/.auth/login", (context) =>
    {
        loggedInUser = users[index % users.Length];
        index++;
        context.Response.StatusCode = (int)HttpStatusCode.Redirect;
        context.Response.Headers["Location"] = "/";
        return Task.CompletedTask;
    });

    app.MapGet("/.auth/logout", (context) =>
    {
        loggedInUser = null;
        string redirect = context.Request.Query["post_logout_redirect_uri"];
        context.Response.StatusCode = (int)HttpStatusCode.Redirect;
        context.Response.Headers["Location"] = redirect;
        return Task.CompletedTask;
    });
}

app.Run();
