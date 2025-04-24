using System.Net;
using RssApp.Components;
using RssApp.Config;
using RssApp.Contracts;
using RssApp.Persistence;
using RssApp.RssClient;
using RssApp.Serialization;

// Refactor configuration loading into a function
var config = RssAppConfig.LoadFromEnvironment();

var cancellationTokenSource = new CancellationTokenSource();

Console.CancelKeyPress += delegate {
    cancellationTokenSource.Cancel();
};

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLogging(loggingBuilder =>
{
    loggingBuilder.ClearProviders();
    loggingBuilder.AddConsole();
    loggingBuilder.AddDebug();
    loggingBuilder.AddAzureWebAppDiagnostics();
});

builder.Services
    .AddHttpClient()
    .AddMemoryCache()
    .AddSingleton<IUserRepository>(sb =>
    {
        return new SQLiteUserRepository(
            $"Data Source={config.UserDb}",
            sb.GetRequiredService<ILogger<SQLiteUserRepository>>());
    })
    .AddSingleton<IFeedRepository>(sb =>
    {
        return new SQLiteFeedRepository(
            $"Data Source={config.FeedDb}",
            sb.GetRequiredService<ILogger<SQLiteFeedRepository>>());
    })
    .AddSingleton<IItemRepository>(sb =>
    {
        return new SQLiteItemRepository(
            $"Data Source={config.ItemDb}",
            sb.GetRequiredService<ILogger<SQLiteItemRepository>>(),
            sb.GetRequiredService<IFeedRepository>(),
            sb.GetRequiredService<IUserRepository>());
    })
    .AddSingleton<RssDeserializer>()
    .AddSingleton<FeedRefresher>(sp =>
    {
        return new FeedRefresher(
            sp.GetRequiredService<RssDeserializer>(),
            sp.GetRequiredService<ILogger<FeedRefresher>>(),
            sp.GetRequiredService<IFeedRepository>(),
            sp.GetRequiredService<IItemRepository>(),
            sp.GetRequiredService<IUserRepository>(),
            config.CacheReloadInterval, 
            config.CacheReloadStartupDelay);
    })
    .AddTransient<IFeedClient, FeedClient>()
    .AddSingleton<OpmlSerializer>();

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

var refresher = app.Services.GetRequiredService<FeedRefresher>();
await refresher.StartAsync(cancellationTokenSource.Token);

var a = app.Services.GetRequiredService<IFeedRepository>();
var b = app.Services.GetRequiredService<IUserRepository>();
var c = app.Services.GetRequiredService<IItemRepository>();

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
