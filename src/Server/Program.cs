using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Data;
using RssApp.RssClient;
using RssApp.Serialization;
using RssReader.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// Add configuration from appsettings.json
builder.Configuration.AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true);    
var config = RssAppConfig.LoadFromAppSettings(builder.Configuration);

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxConcurrentConnections = 50;
    serverOptions.Limits.MaxConcurrentUpgradedConnections = 50;
    serverOptions.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10MB
});

var configureCors = (CorsOptions options) => 
{
    options.AddPolicy(
        name: "AllowSpecificOrigins",
        policy =>
        {
            policy
            .WithOrigins("*")
            .AllowAnyHeader()
            .AllowAnyMethod();
        });
};

builder.Services.AddLogging(loggingBuilder =>
{
    loggingBuilder.ClearProviders();
    loggingBuilder.AddConsole();
    loggingBuilder.AddDebug();
});

builder.Services.AddCors(configureCors);
builder.Services.AddControllers();

// RSS services
builder.Services
.AddSingleton<RssAppConfig>(_ => config)
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
        sb.GetRequiredService<IUserRepository>(),
        sb.GetRequiredService<FeedThumbnailRetriever>());
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
})
.AddSingleton<FeedThumbnailRetriever>()
.AddTransient<RedirectDowngradeHandler>()
.AddHttpClient("RssClient")
.AddHttpMessageHandler<RedirectDowngradeHandler>()
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseDefaultCredentials = true
});

// Configure HTTPS for prod
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddLettuceEncrypt();
    builder.WebHost.UseKestrel(k =>
    {
        var appServices = k.ApplicationServices;
        k.ConfigureHttpsDefaults(h =>
        {
            h.UseLettuceEncrypt(appServices);
        });
    });
}

if (!config.IsTestUserEnabled)
{
    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });
}

var app = builder.Build();

// Instantiate repositories to ensure db tables are created in order.
var a = app.Services.GetRequiredService<IFeedRepository>();
var b = app.Services.GetRequiredService<IUserRepository>();
var c = app.Services.GetRequiredService<IItemRepository>();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseCors("AllowSpecificOrigins");

if (!config.IsTestUserEnabled)
{
    // Accept SWA-authenticated requests by parsing the X-MS-CLIENT-PRINCIPAL header
    app.Use(async (context, next) =>
    {
        const string PrincipalHeader = "X-MS-CLIENT-PRINCIPAL";
        if (context.Request.Headers.TryGetValue(PrincipalHeader, out var headerValues))
        {
            try
            {
                var data = headerValues.ToString();
                if (!string.IsNullOrWhiteSpace(data))
                {
                    var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data));
                    var principalPayload = System.Text.Json.JsonSerializer.Deserialize<SwaPrincipal>(decoded);
                    if (principalPayload?.UserRoles != null && principalPayload.UserRoles.Any(r => !string.Equals(r, "anonymous", StringComparison.OrdinalIgnoreCase)))
                    {
                        var claims = new List<System.Security.Claims.Claim>
                        {
                            new(System.Security.Claims.ClaimTypes.NameIdentifier, principalPayload.UserId ?? string.Empty),
                            new(System.Security.Claims.ClaimTypes.Name, principalPayload.UserDetails ?? string.Empty)
                        };
                        claims.AddRange(principalPayload.UserRoles.Select(r => new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, r)));
                        var identity = new System.Security.Claims.ClaimsIdentity(claims, authenticationType: "SWA");
                        context.User = new System.Security.Claims.ClaimsPrincipal(identity);
                    }
                }
            }
            catch
            {
                // Ignore parsing errors and fall back to other auth schemes
            }
        }
        await next();
    });

    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapControllers();
app.Run();

// DTO for parsing X-MS-CLIENT-PRINCIPAL
file class SwaPrincipal
{
    public string IdentityProvider { get; set; }
    public string UserId { get; set; }
    public string UserDetails { get; set; }
    public IEnumerable<string> UserRoles { get; set; }
}