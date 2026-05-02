namespace RssApp.Data;
using Microsoft.Data.Sqlite;

/// <summary>
/// Owns the SQLite connection-string and pragma logic for a single database file.
/// Replaces the two-string (write/read) ceremony that was scattered across every repository.
/// In read-replica mode (<paramref name="isReadOnly"/> = true), <see cref="OpenWrite"/> and
/// <see cref="OpenWriteAsync"/> throw <see cref="InvalidOperationException"/> instead of
/// handing out a writable connection.
/// </summary>
public sealed class SqliteDbConnections : IDbConnections
{
    private readonly string _readString;
    private readonly string _writeString;
    private readonly bool _isReadOnly;

    public SqliteDbConnections(string dbLocation, bool isReadOnly)
    {
        _isReadOnly = isReadOnly;
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
        if (_isReadOnly)
            throw new InvalidOperationException("Cannot open write connection in read-replica mode.");
        var conn = new SqliteConnection(_writeString);
        conn.OpenWithWritePragmas();
        return conn;
    }

    public async Task<SqliteConnection> OpenWriteAsync()
    {
        if (_isReadOnly)
            throw new InvalidOperationException("Cannot open write connection in read-replica mode.");
        var conn = new SqliteConnection(_writeString);
        await conn.OpenWithWritePragmasAsync();
        return conn;
    }
}
