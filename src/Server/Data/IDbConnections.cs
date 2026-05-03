namespace RssApp.Data;
using Microsoft.Data.Sqlite;

public interface IDbConnections
{
    SqliteConnection OpenRead();
    Task<SqliteConnection> OpenReadAsync();
    SqliteConnection OpenWrite();              // throws InvalidOperationException in replica mode
    Task<SqliteConnection> OpenWriteAsync();   // throws InvalidOperationException in replica mode
}
