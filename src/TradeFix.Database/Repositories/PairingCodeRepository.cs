using Microsoft.Data.Sqlite;

namespace TradeFix.Database.Repositories;

public sealed class PairingCodeRepository(SqliteConnectionFactory connectionFactory)
{
    public void Insert(string code, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO pairing_codes (code, created_at, expires_at, consumed) VALUES ($code, $created, $expires, 0);";
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        command.Parameters.AddWithValue("$expires", expiresAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>Atomically checks the code is valid (exists, unconsumed, unexpired) and marks it consumed.
    /// Returns true if the code was valid and has now been consumed.</summary>
    public bool TryConsume(string code, DateTimeOffset now)
    {
        using var connection = connectionFactory.Open();
        using var transaction = connection.BeginTransaction();

        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT expires_at, consumed FROM pairing_codes WHERE code = $code;";
            select.Parameters.AddWithValue("$code", code);
            using var reader = select.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            var expiresAt = DateTimeOffset.Parse(reader.GetString(0));
            var consumed = reader.GetInt64(1) != 0;
            if (consumed || expiresAt < now)
            {
                return false;
            }
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE pairing_codes SET consumed = 1 WHERE code = $code;";
            update.Parameters.AddWithValue("$code", code);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }
}
