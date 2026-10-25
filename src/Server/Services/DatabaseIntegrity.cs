#nullable enable
using System;
using Microsoft.Data.Sqlite;

namespace RssReader.Server.Services
{
    /// <summary>Integrity check run on every database file before it becomes the live one.</summary>
    public static class DatabaseIntegrity
    {
        /// <summary>
        /// <c>PRAGMA quick_check</c> on a read-only connection. Never throws: a file that cannot
        /// be opened reports <c>Ok=false</c> with the exception in <c>Error</c>.
        /// </summary>
        public static (bool Ok, string? Result, string? Error) QuickCheck(string path)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA quick_check";
                var result = cmd.ExecuteScalar() as string;
                return (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase), result, null);
            }
            catch (Exception ex)
            {
                return (false, null, ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
