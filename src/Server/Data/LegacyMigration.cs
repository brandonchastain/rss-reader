using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Server.Authentication;

namespace RssApp.Data;

/// <summary>
/// What the one-shot migration did, for /api/healthz.
/// </summary>
public sealed record MigrationState(
    string Id,
    string Status,          // "migrated" | "already-applied" | "not-needed" | "failed"
    string AppliedUtc,
    long? DurationMs,
    string Notes);

/// <summary>
/// The one-shot, in-place migration from the per-subscriber Items schema to
/// the shared Posts schema (DB-REDESIGN-SPEC.md section 7). Runs once, on the
/// writer, before the repositories open the database:
///
///  1. Guard: only when the legacy Items table exists, no Legacy_ table exists,
///     Posts is absent or empty, and SchemaMigrations has no record.
///  2. One transaction: rename every superseded table to Legacy_* (Users and
///     Feeds share their names with the new tables, so the renames come first),
///     create the new schema, run the deterministic mapping in dependency
///     order, verify counts. Any failure rolls the whole transaction back and
///     the exception stops start-up.
///  3. Record SchemaMigrations, then VACUUM outside the transaction.
/// </summary>
public static class LegacyMigration
{
    private static readonly string[] Superseded =
    {
        "Items", "ItemContent", "Items_fts", "FeedValidators", "FeedFetchState", "UserTagSettings", "Users", "Feeds",
    };

    public static MigrationState RunIfNeeded(IDbConnections connections, ILogger logger)
    {
        using var connection = connections.OpenWrite();

        if (DatabaseSchema.TableExists(connection, "SchemaMigrations"))
        {
            var recorded = ReadRecord(connection);
            if (recorded != null) return recorded;
        }

        if (!DatabaseSchema.TableExists(connection, "Items") || !DatabaseSchema.TableExists(connection, "Feeds"))
        {
            // Fresh database (or one created by this version): nothing to move.
            return new MigrationState(DatabaseSchema.MigrationId, "not-needed", null, null, "no legacy Items table");
        }

        if (Superseded.Any(t => DatabaseSchema.TableExists(connection, "Legacy_" + t)))
        {
            throw new InvalidOperationException(
                "Legacy_ tables exist but SchemaMigrations has no record of the migration; refusing to start. " +
                "Restore the pre-migration database copy or finish the migration by hand.");
        }

        if (DatabaseSchema.TableExists(connection, "Posts") && DatabaseSchema.Scalar(connection, "SELECT count(*) FROM Posts") > 0)
        {
            throw new InvalidOperationException("Posts already holds rows next to a legacy Items table; refusing to start.");
        }

        var sizeBefore = DatabaseSize(connection);
        var sw = Stopwatch.StartNew();
        var counts = Migrate(connection, logger);
        var migrateMs = sw.ElapsedMilliseconds;

        logger.LogInformation("Migration committed in {Ms} ms; running VACUUM.", migrateMs);
        DatabaseSchema.Execute(connection, "VACUUM;");
        sw.Stop();
        var sizeAfter = DatabaseSize(connection);

        var notes = $"{counts}; migrate {migrateMs} ms, vacuum {sw.ElapsedMilliseconds - migrateMs} ms; " +
                    $"size {sizeBefore:N0} -> {sizeAfter:N0} bytes";
        logger.LogInformation("Migration {Id} complete: {Notes}", DatabaseSchema.MigrationId, notes);

        var appliedUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE SchemaMigrations SET DurationMs = @ms, Notes = @notes, AppliedUtc = @applied WHERE Id = @id";
            cmd.Parameters.AddWithValue("@ms", sw.ElapsedMilliseconds);
            cmd.Parameters.AddWithValue("@notes", notes);
            cmd.Parameters.AddWithValue("@applied", appliedUtc);
            cmd.Parameters.AddWithValue("@id", DatabaseSchema.MigrationId);
            cmd.ExecuteNonQuery();
        }

        return new MigrationState(DatabaseSchema.MigrationId, "migrated", appliedUtc, sw.ElapsedMilliseconds, notes);
    }

    public sealed record Counts(
        long Users, long Identities, long Feeds, long Subscriptions, long Tags, long SubscriptionTags,
        long Posts, long PostContent, long ReadStates, long SavedStates)
    {
        public override string ToString() =>
            $"users={Users} identities={Identities} feeds={Feeds} subscriptions={Subscriptions} tags={Tags} " +
            $"subscriptionTags={SubscriptionTags} posts={Posts} content={PostContent} read={ReadStates} saved={SavedStates}";
    }

    private static Counts Migrate(SqliteConnection connection, ILogger logger)
    {
        var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        using var tx = connection.BeginTransaction();
        try
        {
            // --- Superseded tables out of the way. The FTS triggers are dropped
            // (derived objects); every table is kept under a Legacy_ name until
            // the owner confirms the result and drops them by hand.
            foreach (var trigger in new[] { "Items_after_insert", "Items_au", "Items_after_delete" })
            {
                Exec(connection, tx, $"DROP TRIGGER IF EXISTS {trigger};");
            }
            foreach (var table in Superseded)
            {
                if (DatabaseSchema.TableExists(connection, table))
                {
                    Exec(connection, tx, $"ALTER TABLE {table} RENAME TO Legacy_{table};");
                }
            }
            var hasValidators = DatabaseSchema.TableExists(connection, "Legacy_FeedValidators");
            var hasFetchState = DatabaseSchema.TableExists(connection, "Legacy_FeedFetchState");
            var hasTagSettings = DatabaseSchema.TableExists(connection, "Legacy_UserTagSettings");
            var hasItemContent = DatabaseSchema.TableExists(connection, "Legacy_ItemContent");

            // --- New schema (tables and indexes; the FTS index comes after the load).
            Exec(connection, tx, DatabaseSchema.Tables);

            // --- Users
            Exec(connection, tx, $"INSERT INTO Users (Id, Username, CreatedUtc) SELECT Id, Username, '{now}' FROM Legacy_Users;");

            // --- UserIdentities: one row per stored id, split the same way the
            // resolver splits a login id.
            var hasAad = ColumnExists(connection, tx, "Legacy_Users", "AadUserId");
            var hasProvider = ColumnExists(connection, tx, "Legacy_Users", "ProviderUserId");
            var identities = new List<(int UserId, string Provider, string Subject)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"SELECT Id, {(hasAad ? "AadUserId" : "NULL")}, {(hasProvider ? "ProviderUserId" : "NULL")} FROM Legacy_Users";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var userId = reader.GetInt32(0);
                    foreach (var ordinal in new[] { 1, 2 })
                    {
                        if (reader.IsDBNull(ordinal)) continue;
                        var raw = reader.GetString(ordinal);
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        var (provider, subject) = UserIdentity.ParseIdentity(raw);
                        identities.Add((userId, provider, subject));
                    }
                }
            }
            foreach (var (userId, provider, subject) in identities)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO UserIdentities (Provider, Subject, UserId) VALUES (@p, @s, @u)";
                cmd.Parameters.AddWithValue("@p", provider);
                cmd.Parameters.AddWithValue("@s", subject);
                cmd.Parameters.AddWithValue("@u", userId);
                cmd.ExecuteNonQuery();
            }

            // --- Feeds: every distinct URL that was subscribed or has items (a
            // feed deleted by its last subscriber left its items behind; they
            // need a feed row to hang off, and retention removes the orphan later).
            Exec(connection, tx, $"""
                INSERT INTO Feeds (Url, ETag, LastModified, LastFetchedUtc, NextFetchUtc, IntervalSeconds, CreatedUtc)
                SELECT u.Url,
                       {(hasValidators ? "fv.ETag" : "NULL")},
                       {(hasValidators ? "fv.LastModified" : "NULL")},
                       {(hasFetchState ? "fs.LastFetchedUtc" : "NULL")},
                       {(hasFetchState ? "fs.NextEarliestFetchUtc" : "NULL")},
                       {(hasFetchState ? "fs.IntervalSeconds" : "NULL")},
                       '{now}'
                FROM (SELECT Url FROM Legacy_Feeds WHERE Url IS NOT NULL
                      UNION SELECT FeedUrl FROM Legacy_Items WHERE FeedUrl IS NOT NULL) u
                {(hasValidators ? "LEFT JOIN Legacy_FeedValidators fv ON fv.Url = u.Url" : "")}
                {(hasFetchState ? "LEFT JOIN Legacy_FeedFetchState fs ON fs.Url = u.Url" : "")}
                ORDER BY u.Url;
                """);

            // --- Subscriptions: every old per-user feed row whose user exists.
            Exec(connection, tx, $"""
                INSERT OR IGNORE INTO Subscriptions (UserId, FeedId, SubscribedUtc)
                SELECT lf.UserId, f.Id, '{now}'
                FROM Legacy_Feeds lf
                JOIN Feeds f ON f.Url = lf.Url
                JOIN Users u ON u.Id = lf.UserId
                ORDER BY lf.Id;
                """);

            // --- Tags and SubscriptionTags from the comma strings.
            var tagRows = new List<(int UserId, string Url, string Tag)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT UserId, Url, Tags FROM Legacy_Feeds WHERE Tags IS NOT NULL AND Tags <> '' ORDER BY Id";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var userId = reader.GetInt32(0);
                    var url = reader.GetString(1);
                    foreach (var tag in reader.GetString(2).Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).Distinct())
                    {
                        tagRows.Add((userId, url, tag));
                    }
                }
            }
            foreach (var (userId, url, tag) in tagRows)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR IGNORE INTO Tags (UserId, Name) VALUES (@u, @t);
                    INSERT OR IGNORE INTO SubscriptionTags (UserId, FeedId, TagId)
                    SELECT s.UserId, s.FeedId, t.Id
                    FROM Subscriptions s
                    JOIN Feeds f ON f.Id = s.FeedId
                    JOIN Tags t ON t.UserId = s.UserId AND t.Name = @t
                    WHERE s.UserId = @u AND f.Url = @url;
                    """;
                cmd.Parameters.AddWithValue("@u", userId);
                cmd.Parameters.AddWithValue("@t", tag);
                cmd.Parameters.AddWithValue("@url", url);
                cmd.ExecuteNonQuery();
            }
            if (hasTagSettings)
            {
                Exec(connection, tx, """
                    UPDATE Tags SET IsHidden = 1
                    WHERE EXISTS (SELECT 1 FROM Legacy_UserTagSettings s
                                  WHERE s.UserId = Tags.UserId AND s.Tag = Tags.Name COLLATE NOCASE AND s.IsHidden = 1);
                    """);
            }

            // --- Posts: one row per (FeedUrl, Href) group, represented by the
            // lowest old Id, inserted in old-Id order so ties on PublishDateOrder
            // keep their relative order. Indexes on the legacy tables make the
            // grouped lookups below linear instead of quadratic.
            Exec(connection, tx, "CREATE INDEX IF NOT EXISTS IX_Legacy_Items_FeedHref ON Legacy_Items(FeedUrl, Href);");
            if (hasItemContent)
            {
                Exec(connection, tx, "CREATE INDEX IF NOT EXISTS IX_Legacy_ItemContent_FeedHref ON Legacy_ItemContent(FeedUrl, Href);");
            }
            Exec(connection, tx, $"""
                INSERT INTO Posts (FeedId, Href, CommentsHref, Title, PublishDateOrder, PublishDate, ThumbnailUrl, FirstSeenUtc)
                SELECT f.Id, COALESCE(i.Href, ''), i.CommentsHref, i.Title, COALESCE(i.PublishDateOrder, 0), i.PublishDate, i.ThumbnailUrl, '{now}'
                FROM Legacy_Items i
                JOIN Feeds f ON f.Url = i.FeedUrl
                WHERE i.Id IN (SELECT MIN(Id) FROM Legacy_Items GROUP BY FeedUrl, COALESCE(Href, ''))
                ORDER BY i.Id;
                """);

            // --- Mapping table old item id -> post id, used by the state copy.
            Exec(connection, tx, """
                CREATE TEMP TABLE ItemMap (OldItemId INTEGER PRIMARY KEY, PostId INTEGER NOT NULL);
                INSERT INTO ItemMap (OldItemId, PostId)
                SELECT i.Id, p.Id
                FROM Legacy_Items i
                JOIN Feeds f ON f.Url = i.FeedUrl
                JOIN Posts p ON p.FeedId = f.Id AND p.Href = COALESCE(i.Href, '');
                """);

            // --- PostContent: the representative row's ItemContent, else any
            // subscriber's ItemContent, else Items.Content.
            Exec(connection, tx, $"""
                INSERT INTO PostContent (PostId, Title, Content)
                SELECT p.Id, p.Title,
                       COALESCE(
                           {(hasItemContent ? "(SELECT ic.Content FROM Legacy_ItemContent ic WHERE ic.FeedUrl = f.Url AND ic.Href = p.Href AND ic.Content IS NOT NULL AND ic.Content <> '' ORDER BY ic.Id LIMIT 1)," : "")}
                           (SELECT i.Content FROM Legacy_Items i WHERE i.FeedUrl = f.Url AND COALESCE(i.Href, '') = p.Href AND i.Content IS NOT NULL AND i.Content <> '' ORDER BY i.Id LIMIT 1),
                           '')
                FROM Posts p JOIN Feeds f ON f.Id = p.FeedId
                ORDER BY p.Id;
                """);

            // --- UserPostState: only rows that differ from "unread, not saved".
            Exec(connection, tx, """
                INSERT INTO UserPostState (UserId, PostId, IsRead, IsSaved)
                SELECT i.UserId, m.PostId, MAX(COALESCE(i.IsRead, 0)), MAX(COALESCE(i.IsSaved, 0))
                FROM Legacy_Items i
                JOIN ItemMap m ON m.OldItemId = i.Id
                JOIN Users u ON u.Id = i.UserId
                WHERE i.IsRead = 1 OR i.IsSaved = 1
                GROUP BY i.UserId, m.PostId;
                """);

            // --- Verification (spec 7, "Verification inside the migration").
            var oldPostGroups = Scalar(connection, tx, "SELECT count(*) FROM (SELECT 1 FROM Legacy_Items GROUP BY FeedUrl, COALESCE(Href, ''))");
            var newPosts = Scalar(connection, tx, "SELECT count(*) FROM Posts");
            Verify("posts", oldPostGroups, newPosts);

            var oldSubscriptions = Scalar(connection, tx, "SELECT count(*) FROM (SELECT DISTINCT lf.UserId, lf.Url FROM Legacy_Feeds lf JOIN Users u ON u.Id = lf.UserId)");
            var newSubscriptions = Scalar(connection, tx, "SELECT count(*) FROM Subscriptions");
            Verify("subscriptions", oldSubscriptions, newSubscriptions);

            var oldRead = Scalar(connection, tx, "SELECT count(*) FROM (SELECT 1 FROM Legacy_Items i JOIN ItemMap m ON m.OldItemId = i.Id JOIN Users u ON u.Id = i.UserId WHERE i.IsRead = 1 GROUP BY i.UserId, m.PostId)");
            var newRead = Scalar(connection, tx, "SELECT count(*) FROM UserPostState WHERE IsRead = 1");
            Verify("read states", oldRead, newRead);
            var oldReadRows = Scalar(connection, tx, "SELECT count(*) FROM Legacy_Items i JOIN Users u ON u.Id = i.UserId WHERE i.IsRead = 1");
            Verify("read rows", oldReadRows, newRead);

            var oldSaved = Scalar(connection, tx, "SELECT count(*) FROM (SELECT 1 FROM Legacy_Items i JOIN ItemMap m ON m.OldItemId = i.Id JOIN Users u ON u.Id = i.UserId WHERE i.IsSaved = 1 GROUP BY i.UserId, m.PostId)");
            var newSaved = Scalar(connection, tx, "SELECT count(*) FROM UserPostState WHERE IsSaved = 1");
            Verify("saved states", oldSaved, newSaved);
            var oldSavedRows = Scalar(connection, tx, "SELECT count(*) FROM Legacy_Items i JOIN Users u ON u.Id = i.UserId WHERE i.IsSaved = 1");
            Verify("saved rows", oldSavedRows, newSaved);

            var unmapped = Scalar(connection, tx, "SELECT count(*) FROM Legacy_Items i WHERE NOT EXISTS (SELECT 1 FROM ItemMap m WHERE m.OldItemId = i.Id)");
            Verify("unmapped legacy items", 0, unmapped);

            var skippedUsers = Scalar(connection, tx, "SELECT count(*) FROM Legacy_Feeds lf WHERE NOT EXISTS (SELECT 1 FROM Users u WHERE u.Id = lf.UserId)");
            if (skippedUsers > 0)
            {
                logger.LogWarning("Migration: {Count} legacy feed rows belonged to users that no longer exist and were not migrated.", skippedUsers);
            }

            Exec(connection, tx, "DROP TABLE ItemMap;");

            // --- Search index: table + triggers, then a full rebuild.
            Exec(connection, tx, DatabaseSchema.FtsTable);
            Exec(connection, tx, DatabaseSchema.FtsTriggers);
            Exec(connection, tx, "INSERT INTO Posts_fts(Posts_fts) VALUES('rebuild');");

            var counts = new Counts(
                Scalar(connection, tx, "SELECT count(*) FROM Users"),
                Scalar(connection, tx, "SELECT count(*) FROM UserIdentities"),
                Scalar(connection, tx, "SELECT count(*) FROM Feeds"),
                newSubscriptions,
                Scalar(connection, tx, "SELECT count(*) FROM Tags"),
                Scalar(connection, tx, "SELECT count(*) FROM SubscriptionTags"),
                newPosts,
                Scalar(connection, tx, "SELECT count(*) FROM PostContent"),
                newRead,
                newSaved);

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO SchemaMigrations (Id, AppliedUtc, DurationMs, Notes) VALUES (@id, @applied, 0, @notes)";
                cmd.Parameters.AddWithValue("@id", DatabaseSchema.MigrationId);
                cmd.Parameters.AddWithValue("@applied", now);
                cmd.Parameters.AddWithValue("@notes", counts.ToString());
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return counts;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Migration {Id} failed; rolled back, refusing to start.", DatabaseSchema.MigrationId);
            try { tx.Rollback(); } catch { /* already rolled back */ }
            throw;
        }
    }

    private static void Verify(string what, long expected, long actual)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException($"Migration verification failed: {what} expected {expected}, got {actual}.");
        }
    }

    private static MigrationState ReadRecord(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT AppliedUtc, DurationMs, Notes FROM SchemaMigrations WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", DatabaseSchema.MigrationId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new MigrationState(
            DatabaseSchema.MigrationId,
            "already-applied",
            reader.GetString(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static bool ColumnExists(SqliteConnection connection, SqliteTransaction tx, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT count(*) FROM pragma_table_info('{table}') WHERE name = @c";
        cmd.Parameters.AddWithValue("@c", column);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static long DatabaseSize(SqliteConnection connection)
        => DatabaseSchema.Scalar(connection, "PRAGMA page_count") * DatabaseSchema.Scalar(connection, "PRAGMA page_size");

    private static void Exec(SqliteConnection connection, SqliteTransaction tx, string sql) => DatabaseSchema.Execute(connection, sql, tx);
    private static long Scalar(SqliteConnection connection, SqliteTransaction tx, string sql) => DatabaseSchema.Scalar(connection, sql, tx);
}
