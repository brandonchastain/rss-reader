using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Data;
using RssApp.RssClient;
using RssApp.Serialization;

var config = RssAppConfig.LoadFromEnvironment();
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLogging(loggingBuilder =>
{
    loggingBuilder.ClearProviders();
    loggingBuilder.AddConsole();
    loggingBuilder.AddDebug();
    loggingBuilder.AddAzureWebAppDiagnostics();
});

builder.Services.AddCors(options =>
{
    options.AddPolicy(name: "AllowSpecificOrigins",
                      policy =>
                      {
                          policy.WithOrigins("*")
                            .AllowAnyHeader()
                            .AllowAnyMethod();
                      });
});

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddControllers();

if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddLettuceEncrypt();
}

if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.UseKestrel(k =>
    {
        var appServices = k.ApplicationServices;
        k.ConfigureHttpsDefaults(h =>
        {
                h.UseLettuceEncrypt(appServices);
        });
    });
}

builder.Services
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
.AddSingleton<BackgroundWorkQueue>()
.AddHostedService<BackgroundWorker>()
.AddSingleton<IFeedRefresher>(sp =>
{
return new FeedRefresher(
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<RssDeserializer>(),
    sp.GetRequiredService<ILogger<FeedRefresher>>(),
    sp.GetRequiredService<IFeedRepository>(),
    sp.GetRequiredService<IItemRepository>(),
    sp.GetRequiredService<IUserRepository>(),
    sp.GetRequiredService<BackgroundWorkQueue>(),
    cacheReloadInterval: TimeSpan.FromMinutes(5),
    cacheReloadStartupDelay: TimeSpan.FromSeconds(10));
});

builder.Services.AddHttpClient<FeedRefresher>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        UseDefaultCredentials = true
    });

var app = builder.Build();

var a = app.Services.GetRequiredService<IFeedRepository>();
var b = app.Services.GetRequiredService<IUserRepository>();
var c = app.Services.GetRequiredService<IItemRepository>();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors("AllowSpecificOrigins");

//app.UseCors();
//app.UseAuthorization();

app.MapControllers();
app.Run();