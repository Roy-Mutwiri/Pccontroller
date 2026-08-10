using Microsoft.Data.Sqlite;

namespace TradeFix.Database;

public sealed class SqliteConnectionFactory(string databasePath)
{
    public string DatabasePath { get; } = databasePath;

    public SqliteConnection Open()
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }
}
