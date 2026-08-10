using Microsoft.Data.Sqlite;

namespace TradeFix.Database.Migrations;

/// <summary>
/// Minimal forward-only migration runner: numbered SQL steps applied once, tracked in
/// schema_version. Kept intentionally simple (no external migration framework) since the
/// schema is small and changes are additive during early phases.
/// </summary>
public static class Migrator
{
    private static readonly (int Version, string Sql)[] Steps =
    [
        (1, """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS paired_nodes (
                node_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                role TEXT NOT NULL,
                session_token_hash TEXT NOT NULL,
                last_known_ip TEXT,
                created_at TEXT NOT NULL,
                last_seen_at TEXT
            );

            CREATE TABLE IF NOT EXISTS pairing_codes (
                code TEXT PRIMARY KEY,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                consumed INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS log_entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                category TEXT NOT NULL,
                source TEXT NOT NULL,
                message TEXT NOT NULL,
                exception TEXT
            );
            """)
    ];

    public static void Apply(SqliteConnection connection)
    {
        using (var createVersionTable = connection.CreateCommand())
        {
            createVersionTable.CommandText =
                "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);";
            createVersionTable.ExecuteNonQuery();
        }

        var currentVersion = GetCurrentVersion(connection);

        foreach (var step in Steps)
        {
            if (step.Version <= currentVersion)
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = step.Sql;
                command.ExecuteNonQuery();
            }

            using (var recordVersion = connection.CreateCommand())
            {
                recordVersion.Transaction = transaction;
                recordVersion.CommandText = "DELETE FROM schema_version; INSERT INTO schema_version (version) VALUES ($v);";
                recordVersion.Parameters.AddWithValue("$v", step.Version);
                recordVersion.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    private static int GetCurrentVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version LIMIT 1;";
        var result = command.ExecuteScalar();
        return result is long v ? (int)v : 0;
    }
}
