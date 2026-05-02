using Microsoft.Extensions.Logging;
using RssApp.Contracts;
using RssReader.Server.Services;

namespace RssApp.Data;

public class RepositoryFactory
{
    private readonly IDbConnections connections;
    private readonly IServiceProvider serviceProvider;
    private readonly bool isReadOnly;
    private readonly bool rebuildFtsOnStartup;

    public RepositoryFactory(
        IDbConnections connections,
        IServiceProvider serviceProvider,
        bool isReadOnly = false,
        bool rebuildFtsOnStartup = false)
    {
        this.connections = connections;
        this.serviceProvider = serviceProvider;
        this.isReadOnly = isReadOnly;
        this.rebuildFtsOnStartup = rebuildFtsOnStartup;
    }

    public IUserRepository CreateUserRepository()
    {
        return new SQLiteUserRepository(
            this.connections,
            this.serviceProvider.GetRequiredService<ILogger<SQLiteUserRepository>>(),
            this.isReadOnly);
    }

    public IFeedRepository CreateFeedRepository()
    {
        return new SQLiteFeedRepository(
            this.connections,
            this.serviceProvider.GetRequiredService<ILogger<SQLiteFeedRepository>>(),
            this.isReadOnly);
    }

    public IItemRepository CreateItemRepository()
    {
        return new SQLiteItemRepository(
            this.connections,
            this.serviceProvider.GetRequiredService<ILogger<SQLiteItemRepository>>(),
            this.serviceProvider.GetRequiredService<IFeedRepository>(),
            this.serviceProvider.GetRequiredService<IUserRepository>(),
            this.serviceProvider.GetRequiredService<FeedThumbnailRetriever>(),
            this.isReadOnly,
            this.rebuildFtsOnStartup);
    }
}