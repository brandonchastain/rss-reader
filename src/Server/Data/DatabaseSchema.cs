using Microsoft.Data.Sqlite;

namespace RssApp.Data;

/// <summary>
/// The current schema (DB-REDESIGN-SPEC.md section 3): a post is stored once per
/// feed, users attach to feeds through subscriptions, and per-user facts live in
/// sparse state tables. Every statement is idempotent (CREATE ... IF NOT EXISTS),
/// so each repository can call <see cref="EnsureCreated"/> on start-up and a
/// fresh database gets the full schema from whichever repository opens it first.
/// </summary>
public static class DatabaseSchema
{
    public const string MigrationId = "posts-once-v1";

    public const string Tables = """
        CREATE TABLE IF NOT EXISTS Users (
            Id          INTEGER PRIMARY KEY,
            Username    TEXT NOT NULL UNIQUE,
            CreatedUtc  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS UserIdentities (
            Provider    TEXT NOT NULL,
            Subject     TEXT NOT NULL,
            UserId      INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
            PRIMARY KEY (Provider, Subject)
        );
        CREATE INDEX IF NOT EXISTS IX_UserIdentities_User ON UserIdentities(UserId);

        CREATE TABLE IF NOT EXISTS Feeds (
            Id                  INTEGER PRIMARY KEY,
            Url                 TEXT NOT NULL UNIQUE,
            Title               TEXT,
            SiteUrl             TEXT,
            IconUrl             TEXT,
            ETag                TEXT,
            LastModified        TEXT,
            LastFetchedUtc      TEXT,
            NextFetchUtc        TEXT,
            IntervalSeconds     INTEGER,
            ConsecutiveFailures INTEGER NOT NULL DEFAULT 0,
            LastError           TEXT,
            CreatedUtc          TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Feeds_NextFetch ON Feeds(NextFetchUtc);

        CREATE TABLE IF NOT EXISTS Subscriptions (
            UserId          INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
            FeedId          INTEGER NOT NULL REFERENCES Feeds(Id) ON DELETE CASCADE,
            SubscribedUtc   TEXT NOT NULL,
            HideBeforeOrder INTEGER,
            PRIMARY KEY (UserId, FeedId)
        );
        CREATE INDEX IF NOT EXISTS IX_Subscriptions_Feed ON Subscriptions(FeedId);

        CREATE TABLE IF NOT EXISTS Tags (
            Id       INTEGER PRIMARY KEY,
            UserId   INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
            Name     TEXT NOT NULL,
            IsHidden INTEGER NOT NULL DEFAULT 0,
            UNIQUE (UserId, Name)
        );

        CREATE TABLE IF NOT EXISTS SubscriptionTags (
            UserId INTEGER NOT NULL,
            FeedId INTEGER NOT NULL,
            TagId  INTEGER NOT NULL REFERENCES Tags(Id) ON DELETE CASCADE,
            PRIMARY KEY (UserId, FeedId, TagId),
            FOREIGN KEY (UserId, FeedId) REFERENCES Subscriptions(UserId, FeedId) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS IX_SubscriptionTags_Tag ON SubscriptionTags(TagId, FeedId);

        CREATE TABLE IF NOT EXISTS Posts (
            Id               INTEGER PRIMARY KEY,
            FeedId           INTEGER NOT NULL REFERENCES Feeds(Id) ON DELETE CASCADE,
            Href             TEXT NOT NULL,
            CommentsHref     TEXT,
            Title            TEXT,
            PublishDateOrder INTEGER NOT NULL,
            PublishDate      TEXT,
            ThumbnailUrl     TEXT,
            FirstSeenUtc     TEXT NOT NULL,
            UNIQUE (FeedId, Href)
        );
        CREATE INDEX IF NOT EXISTS IX_Posts_Timeline     ON Posts(PublishDateOrder DESC, Id DESC);
        CREATE INDEX IF NOT EXISTS IX_Posts_FeedTimeline ON Posts(FeedId, PublishDateOrder DESC, Id DESC);

        CREATE TABLE IF NOT EXISTS PostContent (
            PostId  INTEGER PRIMARY KEY REFERENCES Posts(Id) ON DELETE CASCADE,
            Title   TEXT,
            Content TEXT
        );

        CREATE TABLE IF NOT EXISTS UserPostState (
            UserId   INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
            PostId   INTEGER NOT NULL REFERENCES Posts(Id) ON DELETE CASCADE,
            IsRead   INTEGER NOT NULL DEFAULT 0,
            IsSaved  INTEGER NOT NULL DEFAULT 0,
            ReadUtc  TEXT,
            SavedUtc TEXT,
            PRIMARY KEY (UserId, PostId)
        );
        CREATE INDEX IF NOT EXISTS IX_UserPostState_Saved ON UserPostState(UserId, IsSaved) WHERE IsSaved = 1;

        CREATE TABLE IF NOT EXISTS SchemaMigrations (
            Id          TEXT PRIMARY KEY,
            AppliedUtc  TEXT NOT NULL,
            DurationMs  INTEGER NOT NULL,
            Notes       TEXT
        );
        """;

    public const string FtsTable = """
        CREATE VIRTUAL TABLE IF NOT EXISTS Posts_fts USING fts5(
            Title, Content,
            content='PostContent', content_rowid='PostId'
        );
        """;

    // Standard external-content triggers so Posts_fts follows PostContent.
    public const string FtsTriggers = """
        CREATE TRIGGER IF NOT EXISTS PostContent_ai AFTER INSERT ON PostContent BEGIN
            INSERT INTO Posts_fts(rowid, Title, Content) VALUES (new.PostId, new.Title, new.Content);
        END;
        CREATE TRIGGER IF NOT EXISTS PostContent_ad AFTER DELETE ON PostContent BEGIN
            INSERT INTO Posts_fts(Posts_fts, rowid, Title, Content) VALUES ('delete', old.PostId, old.Title, old.Content);
        END;
        CREATE TRIGGER IF NOT EXISTS PostContent_au AFTER UPDATE ON PostContent BEGIN
            INSERT INTO Posts_fts(Posts_fts, rowid, Title, Content) VALUES ('delete', old.PostId, old.Title, old.Content);
            INSERT INTO Posts_fts(rowid, Title, Content) VALUES (new.PostId, new.Title, new.Content);
        END;
        """;

    /// <summary>
    /// Creates every table, index, the FTS index and its triggers that do not exist
    /// yet. WAL mode is persistent per file and only needs setting once, but the
    /// pragma is idempotent too.
    /// </summary>
    public static void EnsureCreated(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, Tables);
        Execute(connection, FtsTable);
        Execute(connection, FtsTriggers);
    }

    public static bool TableExists(SqliteConnection connection, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=@n";
        cmd.Parameters.AddWithValue("@n", name);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public static void Execute(SqliteConnection connection, string sql, SqliteTransaction transaction = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static long Scalar(SqliteConnection connection, string sql, SqliteTransaction transaction = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }
}
