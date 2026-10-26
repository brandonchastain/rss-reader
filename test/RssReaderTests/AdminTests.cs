namespace RssReaderTests;

using System.Security.Claims;
using RssApp.Config;
using RssApp.Contracts;
using RssApp.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AdminTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static ClaimsPrincipal MakePrincipal(string nameIdentifier)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, nameIdentifier) },
            "Test");
        return new ClaimsPrincipal(identity);
    }

    private static bool CallIsAdmin(ClaimsPrincipal principal, RssAppConfig config)
    {
        // Replicate the IsAdmin logic from AdminController / UserController
        if (config.IsTestUserEnabled)
            return true;

        var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
            return false;

        var adminIds = config.AdminAadUserIds
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);

        return System.Linq.Enumerable.Contains(adminIds, userId, System.StringComparer.OrdinalIgnoreCase);
    }

    // ── admin detection tests ─────────────────────────────────────────────────

    [TestMethod]
    public void IsAdmin_MatchingAadId_ReturnsTrue()
    {
        var config = new RssAppConfig
        {
            AdminAadUserIds = "024d97a14bc74c58b9a83e88b919a92c",
            IsTestUserEnabled = false
        };
        var principal = MakePrincipal("024d97a14bc74c58b9a83e88b919a92c");

        Assert.IsTrue(CallIsAdmin(principal, config));
    }

    [TestMethod]
    public void IsAdmin_NonMatchingAadId_ReturnsFalse()
    {
        var config = new RssAppConfig
        {
            AdminAadUserIds = "024d97a14bc74c58b9a83e88b919a92c",
            IsTestUserEnabled = false
        };
        var principal = MakePrincipal("some-other-user-id");

        Assert.IsFalse(CallIsAdmin(principal, config));
    }

    [TestMethod]
    public void IsAdmin_TestUserEnabled_ReturnsTrue()
    {
        var config = new RssAppConfig
        {
            AdminAadUserIds = "",
            IsTestUserEnabled = true
        };
        var principal = MakePrincipal("any-user");

        Assert.IsTrue(CallIsAdmin(principal, config));
    }

    [TestMethod]
    public void IsAdmin_MultipleAdminIds_MatchesCorrectOne()
    {
        var config = new RssAppConfig
        {
            AdminAadUserIds = "aaa, 024d97a14bc74c58b9a83e88b919a92c, bbb",
            IsTestUserEnabled = false
        };
        var adminPrincipal  = MakePrincipal("024d97a14bc74c58b9a83e88b919a92c");
        var regularPrincipal = MakePrincipal("ccc-not-admin");

        Assert.IsTrue(CallIsAdmin(adminPrincipal, config));
        Assert.IsFalse(CallIsAdmin(regularPrincipal, config));
    }

    // ── stats recording and retrieval ─────────────────────────────────────────

    private SQLiteSystemStatsRepository CreateRepo(string db)
        => new SQLiteSystemStatsRepository(new SqliteDbConnections(db));

    [TestMethod]
    public void RecordSnapshot_ThenGetLatest_ReturnsCorrectData()
    {
        var db   = $"admin-stats-{System.Guid.NewGuid():N}.db";
        var repo = CreateRepo(db);

        var snap = new SystemStatsSnapshot
        {
            Timestamp   = DateTime.UtcNow,
            UserCount   = 5,
            FeedCount   = 20,
            ItemCount   = 1000,
            DbSizeBytes = 512000
        };
        repo.RecordSnapshot(snap);

        var latest = repo.GetLatestSnapshot();

        Assert.IsNotNull(latest);
        Assert.AreEqual(5,      latest.UserCount);
        Assert.AreEqual(20,     latest.FeedCount);
        Assert.AreEqual(1000,   latest.ItemCount);
        Assert.AreEqual(512000, latest.DbSizeBytes);

        Cleanup(db);
    }

    [TestMethod]
    public void GetHistory_ReturnsSnapshotsWithinRange()
    {
        var db   = $"admin-stats-{System.Guid.NewGuid():N}.db";
        var repo = CreateRepo(db);

        // Insert 3 recent snapshots
        for (int i = 1; i <= 3; i++)
        {
            repo.RecordSnapshot(new SystemStatsSnapshot
            {
                Timestamp   = DateTime.UtcNow.AddDays(-i),
                UserCount   = i,
                FeedCount   = i * 10,
                ItemCount   = i * 100,
                DbSizeBytes = i * 1024
            });
        }

        var history = repo.GetHistory(30).ToList();

        Assert.AreEqual(3, history.Count);

        Cleanup(db);
    }

    [TestMethod]
    public void CleanupOlderThan_RemovesOldEntries_KeepsRecent()
    {
        var db   = $"admin-stats-{System.Guid.NewGuid():N}.db";
        var repo = CreateRepo(db);

        // Old snapshot (31 days ago — should be deleted)
        repo.RecordSnapshot(new SystemStatsSnapshot
        {
            Timestamp   = DateTime.UtcNow.AddDays(-31),
            UserCount   = 99,
            FeedCount   = 99,
            ItemCount   = 99,
            DbSizeBytes = 99
        });

        // Recent snapshot (1 day ago — should be kept)
        repo.RecordSnapshot(new SystemStatsSnapshot
        {
            Timestamp   = DateTime.UtcNow.AddDays(-1),
            UserCount   = 1,
            FeedCount   = 2,
            ItemCount   = 3,
            DbSizeBytes = 4
        });

        repo.CleanupOlderThan(30);

        var history = repo.GetHistory(60).ToList();
        Assert.AreEqual(1, history.Count, "Only the recent snapshot should remain");
        Assert.AreEqual(1, history[0].UserCount);

        Cleanup(db);
    }

    [TestMethod]
    public void HistoryRange_Keys_ResolveAndLegacyDaysStillWork()
    {
        var r = typeof(RssReader.Server.Services.StatsHistoryRange);
        Assert.IsTrue(RssReader.Server.Services.StatsHistoryRange.TryResolve("1h", null, out var w) && w == TimeSpan.FromHours(1));
        Assert.IsTrue(RssReader.Server.Services.StatsHistoryRange.TryResolve("1Y", null, out w) && w == TimeSpan.FromDays(365), "case-insensitive");
        Assert.IsTrue(RssReader.Server.Services.StatsHistoryRange.TryResolve(null, 7, out w) && w == TimeSpan.FromDays(7), "legacy ?days=");
        Assert.IsFalse(RssReader.Server.Services.StatsHistoryRange.TryResolve("2h", null, out _));
        Assert.IsFalse(RssReader.Server.Services.StatsHistoryRange.TryResolve(null, 14, out _));
        Assert.IsFalse(RssReader.Server.Services.StatsHistoryRange.TryResolve("1h", 14, out w) && w != TimeSpan.FromHours(1), "range wins over days");
        CollectionAssert.AreEqual(new[] { "1h", "1d", "7d", "30d", "1y" }, RssReader.Server.Services.StatsHistoryRange.Ranges.Keys.ToList());
    }

    [TestMethod]
    public void GetHistory_Window_OnlyReturnsSnapshotsInsideIt()
    {
        var db   = $"admin-stats-{System.Guid.NewGuid():N}.db";
        var repo = CreateRepo(db);
        repo.RecordSnapshot(new SystemStatsSnapshot { Timestamp = DateTime.UtcNow.AddMinutes(-20), UserCount = 1 });
        repo.RecordSnapshot(new SystemStatsSnapshot { Timestamp = DateTime.UtcNow.AddMinutes(-90), UserCount = 2 });
        var hour = repo.GetHistory(TimeSpan.FromHours(1)).ToList();
        Assert.AreEqual(1, hour.Count);
        Assert.AreEqual(1, hour[0].UserCount);
        Assert.AreEqual(2, repo.GetHistory(TimeSpan.FromHours(2)).Count());
        Cleanup(db);
    }

    [TestMethod]
    public void HistoryRange_OnlyTheFourRangesAreAllowed()
    {
        foreach (var days in new[] { 1, 7, 30, 365 })
            Assert.IsTrue(RssReader.Server.Services.StatsHistoryRange.IsAllowed(days), days.ToString());
        foreach (var days in new[] { 0, -1, 2, 14, 90, 366, 100000 })
            Assert.IsFalse(RssReader.Server.Services.StatsHistoryRange.IsAllowed(days), days.ToString());
        Assert.IsTrue(RssReader.Server.Services.StatsHistoryRange.RetentionDays > 365, "retention must cover the longest range");
    }

    [TestMethod]
    public void HistoryRange_Thin_KeepsEndsAndSpreadsEvenly()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var hourly = Enumerable.Range(0, 24 * 365)
            .Select(i => new SystemStatsSnapshot { Timestamp = start.AddHours(i), ItemCount = i })
            .ToList();

        var thinned = RssReader.Server.Services.StatsHistoryRange.Thin(hourly, 400);

        Assert.AreEqual(400, thinned.Count);
        Assert.AreEqual(0, thinned[0].ItemCount, "first point kept");
        Assert.AreEqual(hourly.Count - 1, thinned[^1].ItemCount, "last point kept");
        for (var i = 1; i < thinned.Count; i++)
            Assert.IsTrue(thinned[i].ItemCount > thinned[i - 1].ItemCount, "order preserved, no repeats");
        var gaps = Enumerable.Range(1, thinned.Count - 1).Select(i => thinned[i].ItemCount - thinned[i - 1].ItemCount).ToList();
        Assert.IsTrue(gaps.Max() - gaps.Min() <= 1, $"even spacing, gaps {gaps.Min()}..{gaps.Max()}");

        var small = hourly.Take(50).ToList();
        CollectionAssert.AreEqual(small, RssReader.Server.Services.StatsHistoryRange.Thin(small, 400), "short lists pass through untouched");
    }

    [TestMethod]
    public async Task CaptureNow_RecordsASnapshotEvenWhenTheLatestIsFresh()
    {
        var db   = $"admin-stats-{System.Guid.NewGuid():N}.db";
        var repo = CreateRepo(db);
        // No live database at this path: counts come back as zero, which is fine
        // here; the point is the recording cadence, not the numbers.
        var config = new RssApp.Config.RssAppConfig { DbLocation = Path.Combine(Path.GetTempPath(), $"missing-{System.Guid.NewGuid():N}.db") };
        var collector = new RssReader.Server.Services.SystemStatsCollector(repo, config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RssReader.Server.Services.SystemStatsCollector>.Instance);

        var first = await collector.GetOrCaptureAsync();
        var cached = await collector.GetOrCaptureAsync();
        Assert.AreEqual(first.Timestamp, cached.Timestamp, "within MinInterval the dashboard load reuses the latest snapshot");

        var forced = await collector.CaptureNowAsync();
        // Two captures can share a clock tick, so the proof is the row count, not
        // the timestamps: the forced capture recorded a second snapshot where the
        // cached dashboard load did not.
        Assert.IsTrue(forced.Timestamp >= first.Timestamp);
        Assert.AreEqual(2, repo.GetHistory(1).Count(), "the refresh button always records a new one");

        Cleanup(db);
    }

    [TestMethod]
    public void GetLatestSnapshot_WhenEmpty_ReturnsNull()
    {
        var db   = $"admin-stats-{System.Guid.NewGuid():N}.db";
        var repo = CreateRepo(db);

        var result = repo.GetLatestSnapshot();
        Assert.IsNull(result);

        Cleanup(db);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void Cleanup(string dbFile)
    {
        foreach (var f in new[] { dbFile, dbFile + "-shm", dbFile + "-wal" })
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* ignore */ }
        }
    }
}
