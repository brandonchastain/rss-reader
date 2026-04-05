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
    [TestCleanup]
    public void Cleanup()
    {
        DatabaseMode.ResetForTesting();
    }

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
    public async Task NoOpFeedRefresher_HasNewItems_AlwaysReturnsFalse()
    {
        var refresher = new NoOpFeedRefresher();
        var result = await refresher.HasNewItemsAsync(new RssUser("testuser", 1));
        Assert.IsFalse(result);
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
    // PRAGMA query_only tests (DB-level backstop)
    // ========================================================================

    [TestMethod]
    public void QueryOnly_RejectsInsert_AfterEnabled()
    {
        var dbPath = $"readonly_test_{Guid.NewGuid():N}.db";
        try
        {
            var connStr = $"Data Source={dbPath};Mode=ReadWriteCreate";

            // Create a table while query_only is off
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE Test (Id INTEGER PRIMARY KEY, Value TEXT)";
                cmd.ExecuteNonQuery();
            }

            // Now open with query_only = ON and try to insert
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var pragma = conn.CreateCommand();
                pragma.CommandText = "PRAGMA query_only = ON";
                pragma.ExecuteNonQuery();

                using var insert = conn.CreateCommand();
                insert.CommandText = "INSERT INTO Test (Value) VALUES ('should fail')";
                Assert.ThrowsException<SqliteException>(() => insert.ExecuteNonQuery());
            }
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [TestMethod]
    public void QueryOnly_AllowsSelect_AfterEnabled()
    {
        var dbPath = $"readonly_test_{Guid.NewGuid():N}.db";
        try
        {
            var connStr = $"Data Source={dbPath};Mode=ReadWriteCreate";

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE Test (Id INTEGER PRIMARY KEY, Value TEXT)";
                cmd.ExecuteNonQuery();
            }

            // SELECT should succeed with query_only
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var pragma = conn.CreateCommand();
                pragma.CommandText = "PRAGMA query_only = ON";
                pragma.ExecuteNonQuery();

                using var select = conn.CreateCommand();
                select.CommandText = "SELECT COUNT(*) FROM Test";
                var result = select.ExecuteScalar();
                Assert.AreEqual(0L, result);
            }
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [TestMethod]
    public void OpenWithPragmas_SetsQueryOnly_WhenDatabaseModeEnabled()
    {
        var dbPath = $"readonly_test_{Guid.NewGuid():N}.db";
        try
        {
            var connStr = $"Data Source={dbPath};Mode=ReadWriteCreate";

            // Create table before enabling query_only
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE Test (Id INTEGER PRIMARY KEY)";
                cmd.ExecuteNonQuery();
            }

            DatabaseMode.EnableQueryOnly();

            // OpenWithPragmas should now set query_only = ON
            using (var conn = new SqliteConnection(connStr))
            {
                conn.OpenWithPragmas();
                using var insert = conn.CreateCommand();
                insert.CommandText = "INSERT INTO Test (Id) VALUES (1)";
                Assert.ThrowsException<SqliteException>(() => insert.ExecuteNonQuery());
            }
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
