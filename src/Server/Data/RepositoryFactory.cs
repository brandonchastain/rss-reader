using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RssApp.Contracts;
using RssReader.Server.Services;

namespace RssApp.Data;

public class RepositoryFactory
{
    private readonly string connectionString;
    private readonly IServiceProvider serviceProvider;
    private readonly bool isReadOnly;
    private readonly bool rebuildFtsOnStartup;

    public RepositoryFactory(
        string connectionString,
        IServiceProvider serviceProvider,
        bool isReadOnly = false,
        bool rebuildFtsOnStartup = false)
    {
        this.connectionString = connectionString;
        this.serviceProvider = serviceProvider;
        this.isReadOnly = isReadOnly;
        this.rebuildFtsOnStartup = rebuildFtsOnStartup;
    }

    public IUserRepository CreateUserRepository()
    {
        return new SQLiteUserRepository(
            this.connectionString,
            this.serviceProvider.GetRequiredService<ILogger<SQLiteUserRepository>>(),
            this.isReadOnly);
    }

    public IFeedRepository CreateFeedRepository()
    {
        return new SQLiteFeedRepository(
            this.connectionString,
            this.serviceProvider.GetRequiredService<ILogger<SQLiteFeedRepository>>(),
            this.isReadOnly);
    }

    public IItemRepository CreateItemRepository()
    {
        return new SQLiteItemRepository(
            this.connectionString,
            this.serviceProvider.GetRequiredService<ILogger<SQLiteItemRepository>>(),
            this.serviceProvider.GetRequiredService<IFeedRepository>(),
            this.serviceProvider.GetRequiredService<IUserRepository>(),
            this.serviceProvider.GetRequiredService<FeedThumbnailRetriever>(),
            this.isReadOnly,
            this.rebuildFtsOnStartup);
    }
}