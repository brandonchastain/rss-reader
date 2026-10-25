#nullable enable
using System;
using System.Threading.Tasks;
using RssApp.Data;

namespace RssReader.Server.Services
{
    /// <summary>
    /// The numbers the restore check at cutover compares (BLOB-DURABILITY-SPEC.md 9.5):
    /// reported on <c>/api/healthz</c> by the live writer and computed with the same
    /// SQL on a restore of the replica, by hand.
    /// </summary>
    public sealed record DatabaseRowCounts(long Posts, long Subscriptions, long UserPostState, long MaxPostId, DateTimeOffset ReadUtc, string? Error = null)
    {
        public const string Sql =
            "SELECT (SELECT count(*) FROM Posts), (SELECT count(*) FROM Subscriptions), " +
            "(SELECT count(*) FROM UserPostState), (SELECT coalesce(max(Id), 0) FROM Posts)";

        public static async Task<DatabaseRowCounts> ReadAsync(IDbConnections db)
        {
            using var conn = await db.OpenReadAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = Sql;
            using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            return new DatabaseRowCounts(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), DateTimeOffset.UtcNow);
        }

        public static DatabaseRowCounts Unavailable(string error) => new(-1, -1, -1, -1, DateTimeOffset.UtcNow, error);
    }
}
