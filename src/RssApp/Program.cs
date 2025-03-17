using Microsoft.Extensions.Caching.Memory;
using RssApp.Components;
using RssApp.Persistence;
using RssApp.RssClient;
using RssApp.Serialization;

var cancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += delegate {
        cancellationTokenSource.Cancel();
};

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<PersistedFeeds>();
builder.Services.AddSingleton<PersistedHiddenItems>();
// builder.Services.AddSingleton<IFeedClient>(sb =>
// {
//     return new RefreshingFeedClient(
//         sb.GetRequiredService<IMemoryCache>(),
//         sb.GetRequiredService<HttpClient>(),
//         sb.GetRequiredService<RssDeserializer>(),
//         sb.GetRequiredService<PersistedHiddenItems>(),
//         sb.GetRequiredService<ILogger<RefreshingFeedClient>>(),
//         cancellationTokenSource.Token);
// });
builder.Services.AddSingleton<IFeedClient, FeedClient>();
builder.Services.AddSingleton<RssDeserializer>();

var app = builder.Build();

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
