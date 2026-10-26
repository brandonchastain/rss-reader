using Microsoft.Extensions.Logging;
using RssApp.Contracts;
using RssReader.Server.Services;

namespace RssApp.Data;

public class RepositoryFactory
{
    private readonly IDbConnections connections;
    private readonly IServiceProvider serviceProvider;
    private readonly bool rebuildFtsOnStartup;

    public RepositoryFactory(
        IDbConnections connections,
        IServiceProvider serviceProvider,
        bool rebuildFtsOnStartup = false)
    {
        this.connections = connections;
        this.serviceProvider = serviceProvider;
        this.rebuildFtsOnStartup = rebuildFtsOnStartup;
    }

    public IUserRepository CreateUserRepository()
    {
        return new SQLiteUserRepository(
            this.connections,
            this.serviceProvider.GetRequiredService<ILogger<SQLiteUserRepository>>());
    }

    public IFeedRepository CreateFeedRepository()
    {
        return new SQLiteFeedRepository(
            this.connections,
            this.serviceProvider.GetRequiredService<ILogger<SQLiteFeedRepository>>());
    }

    public IItemRepository CreateItemRepository()
    {
        return new SQLiteItemRepository(
            this.connections,
            this.serviceProvider.GetRequiredService<ILogger<SQLiteItemRepository>>(),
            this.serviceProvider.GetRequiredService<IFeedRepository>(),
            this.serviceProvider.GetRequiredService<IUserRepository>(),
            this.rebuildFtsOnStartup);
    }
}
