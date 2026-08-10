using Microsoft.Data.Sqlite;

namespace TradeFix.Database.Repositories;

public sealed class PairedNodeRepository(SqliteConnectionFactory connectionFactory)
{
    public void Upsert(PairedNodeRecord node)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO paired_nodes (node_id, name, role, session_token_hash, last_known_ip, created_at, last_seen_at)
            VALUES ($id, $name, $role, $hash, $ip, $created, $seen)
            ON CONFLICT(node_id) DO UPDATE SET
                name = excluded.name,
                role = excluded.role,
                session_token_hash = excluded.session_token_hash,
                last_known_ip = excluded.last_known_ip,
                last_seen_at = excluded.last_seen_at;
            """;
        command.Parameters.AddWithValue("$id", node.NodeId);
        command.Parameters.AddWithValue("$name", node.Name);
        command.Parameters.AddWithValue("$role", node.Role);
        command.Parameters.AddWithValue("$hash", node.SessionTokenHash);
        command.Parameters.AddWithValue("$ip", (object?)node.LastKnownIp ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", node.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$seen", (object?)node.LastSeenAt?.ToString("O") ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public PairedNodeRecord? GetByNodeId(string nodeId)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT node_id, name, role, session_token_hash, last_known_ip, created_at, last_seen_at FROM paired_nodes WHERE node_id = $id;";
        command.Parameters.AddWithValue("$id", nodeId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<PairedNodeRecord> GetAll()
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT node_id, name, role, session_token_hash, last_known_ip, created_at, last_seen_at FROM paired_nodes;";

        using var reader = command.ExecuteReader();
        var results = new List<PairedNodeRecord>();
        while (reader.Read())
        {
            results.Add(Map(reader));
        }

        return results;
    }

    private static PairedNodeRecord Map(SqliteDataReader reader) => new()
    {
        NodeId = reader.GetString(0),
        Name = reader.GetString(1),
        Role = reader.GetString(2),
        SessionTokenHash = reader.GetString(3),
        LastKnownIp = reader.IsDBNull(4) ? null : reader.GetString(4),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(5)),
        LastSeenAt = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6))
    };
}
