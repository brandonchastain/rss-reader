using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RssApp.Contracts;
using RssReader.Server.Services;

namespace RssApp.Data;

public class RepositoryFactory
{
    private readonly string connectionString;
    private readonly IServiceProvider serviceProvider;

    public RepositoryFactory(
        string connectionString,
        IServiceProvider serviceProvider)
    {
        this.connectionString = connectionString;
        this.serviceProvider = serviceProvider;
    }

    public IUserRepository CreateUserRepository()
    {
        return new SQLiteUserRepository(
            this.connectionString,
            this.serviceProvider.GetRequiredService<ILogger<SQLiteUserRepository>>());
    }

    public IFeedRepository CreateFeedRepository()
    {
        return new SQLiteFeedRepository(
            this.connectionString,
            this.serviceProvider.GetRequiredService<ILogger<SQLiteFeedRepository>>());
    }

    public IItemRepository CreateItemRepository()
    {
        return new SQLiteItemRepository(
            this.connectionString,
            this.serviceProvider.GetRequiredService<ILogger<SQLiteItemRepository>>(),
            this.serviceProvider.GetRequiredService<IFeedRepository>(),
            this.serviceProvider.GetRequiredService<IUserRepository>(),
            this.serviceProvider.GetRequiredService<FeedThumbnailRetriever>());
    }
}