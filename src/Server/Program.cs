using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Data;
using RssApp.RssClient;
using RssApp.Serialization;
using RssReader.Server.Services;
using Server.Authentication;

// Add configuration from appsettings.json
var builder = WebApplication.CreateBuilder(args);
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true);    
var config = RssAppConfig.LoadFromAppSettings(builder.Configuration);
string dbConnectionString = $"Data Source={config.DbLocation};Mode=ReadWriteCreate;Cache=Shared;Pooling=True";

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxConcurrentConnections = 50;
    serverOptions.Limits.MaxConcurrentUpgradedConnections = 50;
    serverOptions.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10MB
});

builder.Services.AddLogging(loggingBuilder =>
{
    loggingBuilder.ClearProviders();
    loggingBuilder.AddConsole();
    loggingBuilder.AddDebug();
});

// Add authn and authz
builder.Services
    .AddAuthorization()
    .AddAuthentication(StaticWebAppsAuthenticationHandler.AuthenticationScheme)
    .AddScheme<StaticWebAppsAuthenticationOptions, StaticWebAppsAuthenticationHandler>(
        StaticWebAppsAuthenticationHandler.AuthenticationScheme,
        options =>
        {
            options.IsTestUserEnabled = config.IsTestUserEnabled;
        });

// Register services for DI
builder.Services
    .AddSingleton<RssAppConfig>(_ => config)
    .AddSingleton<RepositoryFactory>(sb => new RepositoryFactory(dbConnectionString, sb))
    .AddSingleton<IFeedRepository>(sb => sb.GetRequiredService<RepositoryFactory>().CreateFeedRepository())
    .AddSingleton<IUserRepository>(sb => sb.GetRequiredService<RepositoryFactory>().CreateUserRepository())
    .AddSingleton<IItemRepository>(sb => sb.GetRequiredService<RepositoryFactory>().CreateItemRepository())
    .AddSingleton<RssDeserializer>()
    .AddSingleton<BackgroundWorkQueue>()
    .AddSingleton<DatabaseBackupService>()
    .AddHostedService(p => p.GetRequiredService<DatabaseBackupService>())
    .AddHostedService<BackgroundWorker>()
    .AddSingleton<IFeedRefresher, FeedRefresher>()
    .AddSingleton<FeedThumbnailRetriever>()
    .AddTransient<RedirectDowngradeHandler>()
    .AddHttpClient("RssClient")
    .AddHttpMessageHandler<RedirectDowngradeHandler>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false
    });

builder.Services.AddControllers();

if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.UseKestrel();
}

var app = builder.Build();

// Restore database from backup
var backup = app.Services.GetRequiredService<DatabaseBackupService>();
await backup.RestoreFromBackupAsync(CancellationToken.None);

// Instantiate repos to ensure database tables are created in order.
var a = app.Services.GetRequiredService<IFeedRepository>();
var b = app.Services.GetRequiredService<IUserRepository>();
var c = app.Services.GetRequiredService<IItemRepository>();

// Enable middleware 
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// Setup endpoints
app.MapControllers();
app.MapGet("/api/healthz", () => Results.Ok("healthy")).AllowAnonymous();

if (config.IsTestUserEnabled)
{
    app.MapGet("/.auth/me", (HttpContext context) =>
    {
        return Results.Json(new
        {
            clientPrincipal = new
            {
                identityProvider = "test",
                userId = "testuser2",
                userDetails = "testuser2",
                userRoles = new string[] { "authenticated", "admin" }
            }
        });
    }).AllowAnonymous();
}

// Start the application
app.Run();