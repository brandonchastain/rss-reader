using Microsoft.Data.Sqlite;
using RssApp.Data;
using RssReader.Server.Services;

namespace RssReaderTests;

/// <summary>The numbers /api/healthz reports for the restore drill (BLOB-DURABILITY-SPEC.md 9.5).</summary>
[TestClass]
public class DatabaseRowCountsTests
{
    [TestMethod]
    public async Task Counts_MatchTheTables()
    {
        var path = Path.Combine(Path.GetTempPath(), "rowcounts-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE Posts (Id INTEGER PRIMARY KEY, Title TEXT);
                    CREATE TABLE Subscriptions (Id INTEGER PRIMARY KEY);
                    CREATE TABLE UserPostState (UserId INTEGER, PostId INTEGER);
                    INSERT INTO Posts (Id) VALUES (3), (10), (42);
                    INSERT INTO Subscriptions (Id) VALUES (1), (2);
                    INSERT INTO UserPostState VALUES (1, 3);";
                cmd.ExecuteNonQuery();
            }

            var counts = await DatabaseRowCounts.ReadAsync(new SqliteDbConnections(path, isReadOnly: false));
            Assert.AreEqual(3, counts.Posts);
            Assert.AreEqual(2, counts.Subscriptions);
            Assert.AreEqual(1, counts.UserPostState);
            Assert.AreEqual(42, counts.MaxPostId);
            Assert.IsNull(counts.Error);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Unavailable_CarriesTheError()
    {
        var c = DatabaseRowCounts.Unavailable("no such table");
        Assert.AreEqual(-1, c.Posts);
        Assert.AreEqual("no such table", c.Error);
    }
}
