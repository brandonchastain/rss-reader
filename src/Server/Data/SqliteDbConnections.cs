namespace RssApp.Data;
using Microsoft.Data.Sqlite;

/// <summary>
/// Owns the SQLite connection-string and pragma logic for a single database file.
/// Replaces the two-string (write/read) ceremony that was scattered across every repository.
/// </summary>
public sealed class SqliteDbConnections : IDbConnections
{
    private readonly string _readString;
    private readonly string _writeString;

    public SqliteDbConnections(string dbLocation)
    {
        _readString  = $"Data Source={dbLocation};Mode=ReadOnly;Pooling=True";
        _writeString = $"Data Source={dbLocation};Mode=ReadWriteCreate;Cache=Shared;Pooling=True";
    }

    public SqliteConnection OpenRead()
    {
        var conn = new SqliteConnection(_readString);
        conn.OpenWithReadPragmas();
        return conn;
    }

    public async Task<SqliteConnection> OpenReadAsync()
    {
        var conn = new SqliteConnection(_readString);
        await conn.OpenWithReadPragmasAsync();
        return conn;
    }

    public SqliteConnection OpenWrite()
    {
        var conn = new SqliteConnection(_writeString);
        conn.OpenWithWritePragmas();
        return conn;
    }

    public async Task<SqliteConnection> OpenWriteAsync()
    {
        var conn = new SqliteConnection(_writeString);
        await conn.OpenWithWritePragmasAsync();
        return conn;
    }
}
