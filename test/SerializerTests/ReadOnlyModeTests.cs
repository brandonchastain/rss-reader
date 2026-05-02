namespace SerializerTests;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Contracts;
using RssApp.Data;
using RssApp.Filters;

[TestClass]
public sealed class ReadOnlyModeTests
{
    [TestMethod]
    public void IsReadOnly_DefaultsToFalse()
    {
        var config = new RssAppConfig();
        Assert.IsFalse(config.IsReadOnly);
    }

    [TestMethod]
    public async Task NoOpFeedRefresher_AddFeed_CompletesWithoutError()
    {
        var refresher = new NoOpFeedRefresher();
        await refresher.AddFeedAsync(new NewsFeed("https://example.com/rss", 1));
    }

    [TestMethod]
    public async Task NoOpFeedRefresher_Refresh_CompletesWithoutError()
    {
        var refresher = new NoOpFeedRefresher();
        await refresher.RefreshAsync(new RssUser("testuser", 1));
    }

    [TestMethod]
    public void NoOpFeedRefresher_GetRefreshStatus_AlwaysReturnsInactive()
    {
        var refresher = new NoOpFeedRefresher();
        var status = refresher.GetRefreshStatus(new RssUser("testuser", 1));
        Assert.IsFalse(status.HasNewItems);
        Assert.IsFalse(status.IsRefreshing);
        Assert.AreEqual(0, status.PendingFeeds);
    }

    [TestMethod]
    public void NoOpFeedRefresher_ResetRefreshCooldown_DoesNotThrow()
    {
        var refresher = new NoOpFeedRefresher();
        refresher.ResetRefreshCooldown();
    }

    // ========================================================================
    // ReadOnlyActionFilter tests
    // ========================================================================

    [TestMethod]
    public void ReadOnlyFilter_AllowsGet_WhenReadOnly()
    {
        var filter = new ReadOnlyActionFilter(new RssAppConfig { IsReadOnly = true });
        var context = CreateActionContext("GET");

        filter.OnActionExecuting(context);

        Assert.IsNull(context.Result);
    }

    [TestMethod]
    public void ReadOnlyFilter_AllowsHead_WhenReadOnly()
    {
        var filter = new ReadOnlyActionFilter(new RssAppConfig { IsReadOnly = true });
        var context = CreateActionContext("HEAD");

        filter.OnActionExecuting(context);

        Assert.IsNull(context.Result);
    }

    [TestMethod]
    public void ReadOnlyFilter_RejectsPost_WhenReadOnly()
    {
        var filter = new ReadOnlyActionFilter(new RssAppConfig { IsReadOnly = true });
        var context = CreateActionContext("POST");

        filter.OnActionExecuting(context);

        Assert.IsNotNull(context.Result);
        var objectResult = (ObjectResult)context.Result;
        Assert.AreEqual(405, objectResult.StatusCode);
    }

    [TestMethod]
    public void ReadOnlyFilter_RejectsPut_WhenReadOnly()
    {
        var filter = new ReadOnlyActionFilter(new RssAppConfig { IsReadOnly = true });
        var context = CreateActionContext("PUT");

        filter.OnActionExecuting(context);

        Assert.IsNotNull(context.Result);
        Assert.AreEqual(405, ((ObjectResult)context.Result).StatusCode);
    }

    [TestMethod]
    public void ReadOnlyFilter_RejectsDelete_WhenReadOnly()
    {
        var filter = new ReadOnlyActionFilter(new RssAppConfig { IsReadOnly = true });
        var context = CreateActionContext("DELETE");

        filter.OnActionExecuting(context);

        Assert.IsNotNull(context.Result);
        Assert.AreEqual(405, ((ObjectResult)context.Result).StatusCode);
    }

    [TestMethod]
    public void ReadOnlyFilter_AllowsPost_WhenNotReadOnly()
    {
        var filter = new ReadOnlyActionFilter(new RssAppConfig { IsReadOnly = false });
        var context = CreateActionContext("POST");

        filter.OnActionExecuting(context);

        Assert.IsNull(context.Result);
    }

    // ========================================================================
    // IDbConnections read-replica enforcement tests
    // ========================================================================

    [TestMethod]
    public void IDbConnections_OpenWrite_ThrowsInvalidOperationException_WhenReadOnly()
    {
        var dbPath = $"readonly_test_{Guid.NewGuid():N}.db";
        try
        {
            // Seed the DB file so ReadOnly mode can open it
            var writer = new SqliteDbConnections(dbPath, isReadOnly: false);
            using (var conn = writer.OpenWrite())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE Test (Id INTEGER PRIMARY KEY)";
                cmd.ExecuteNonQuery();
            }

            var readOnly = new SqliteDbConnections(dbPath, isReadOnly: true);
            Assert.ThrowsException<InvalidOperationException>(() => readOnly.OpenWrite());
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [TestMethod]
    public async Task IDbConnections_OpenWriteAsync_ThrowsInvalidOperationException_WhenReadOnly()
    {
        var dbPath = $"readonly_test_{Guid.NewGuid():N}.db";
        try
        {
            var writer = new SqliteDbConnections(dbPath, isReadOnly: false);
            using (var conn = writer.OpenWrite())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE Test (Id INTEGER PRIMARY KEY)";
                cmd.ExecuteNonQuery();
            }

            var readOnly = new SqliteDbConnections(dbPath, isReadOnly: true);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => readOnly.OpenWriteAsync());
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [TestMethod]
    public void IDbConnections_OpenRead_Succeeds_WhenReadOnly()
    {
        var dbPath = $"readonly_test_{Guid.NewGuid():N}.db";
        try
        {
            var writer = new SqliteDbConnections(dbPath, isReadOnly: false);
            using (var conn = writer.OpenWrite())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE Test (Id INTEGER PRIMARY KEY)";
                cmd.ExecuteNonQuery();
            }

            var readOnly = new SqliteDbConnections(dbPath, isReadOnly: true);
            using var readConn = readOnly.OpenRead();
            using var selectCmd = readConn.CreateCommand();
            selectCmd.CommandText = "SELECT COUNT(*) FROM Test";
            var result = selectCmd.ExecuteScalar();
            Assert.AreEqual(0L, result);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    // ========================================================================
    // Repository read-only mode tests (skip schema init)
    // ========================================================================

    [TestMethod]
    public void FeedRepo_SkipsSchemaInit_WhenReadOnly()
    {
        var dbPath = $"readonly_test_{Guid.NewGuid():N}.db";
        try
        {
            // Create DB with schema using writer mode
            var writerConnections = new SqliteDbConnections(dbPath, isReadOnly: false);
            var repo = new SQLiteFeedRepository(
                writerConnections,
                NullLogger<SQLiteFeedRepository>.Instance,
                isReadOnly: false);

            // Now open in read-only mode  should NOT throw even without schema init
            var readerConnections = new SqliteDbConnections(dbPath, isReadOnly: true);
            var readOnlyRepo = new SQLiteFeedRepository(
                readerConnections,
                NullLogger<SQLiteFeedRepository>.Instance,
                isReadOnly: true);

            // Verify we can query (tables exist from writer init)
            var feeds = readOnlyRepo.GetFeeds(new RssUser("testuser", 1));
            Assert.IsNotNull(feeds);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [TestMethod]
    public void UserRepo_SkipsSchemaInit_WhenReadOnly()
    {
        var dbPath = $"readonly_test_{Guid.NewGuid():N}.db";
        try
        {
            var writerConnections = new SqliteDbConnections(dbPath, isReadOnly: false);
            var repo = new SQLiteUserRepository(
                writerConnections,
                NullLogger<SQLiteUserRepository>.Instance,
                isReadOnly: false);

            var readerConnections = new SqliteDbConnections(dbPath, isReadOnly: true);
            var readOnlyRepo = new SQLiteUserRepository(
                readerConnections,
                NullLogger<SQLiteUserRepository>.Instance,
                isReadOnly: true);

            // Should be able to query without error
            var user = readOnlyRepo.GetUserByName("nonexistent");
            Assert.IsNull(user);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static ActionExecutingContext CreateActionContext(string method)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: null!);
    }

    private static void CleanupDb(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var f = path + suffix;
            if (File.Exists(f)) File.Delete(f);
        }
    }
}
