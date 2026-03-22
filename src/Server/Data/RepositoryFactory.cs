using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RssApp.Contracts;
using RssReader.Server.Services;

namespace RssApp.Data;

public class RepositoryFactory
{
    private readonly string writeConnectionString;
    private readonly string readConnectionString;
    private readonly IServiceProvider serviceProvider;

    public RepositoryFactory(
        string writeConnectionString,
        string readConnectionString,
        IServiceProvider serviceProvider)
    {
        this.writeConnectionString = writeConnectionString;
        this.readConnectionString = readConnectionString;
        this.serviceProvider = serviceProvider;
    }

    public IUserRepository CreateUserRepository()
    {
        return new SQLiteUserRepository(
            this.writeConnectionString,
            this.readConnectionString,
            this.serviceProvider.GetRequiredService<ILogger<SQLiteUserRepository>>());
    }

    public IFeedRepository CreateFeedRepository()
    {
        return new SQLiteFeedRepository(
            this.writeConnectionString,
            this.readConnectionString,
            this.serviceProvider.GetRequiredService<ILogger<SQLiteFeedRepository>>());
    }

    public IItemRepository CreateItemRepository()
    {
        return new SQLiteItemRepository(
            this.writeConnectionString,
            this.readConnectionString,
            this.serviceProvider.GetRequiredService<ILogger<SQLiteItemRepository>>(),
            this.serviceProvider.GetRequiredService<IFeedRepository>(),
            this.serviceProvider.GetRequiredService<IUserRepository>(),
            this.serviceProvider.GetRequiredService<FeedThumbnailRetriever>());
    }
}