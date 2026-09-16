using Microsoft.Data.Sqlite;
using VocaLink.Application.Abstractions;

namespace VocaLink.Infrastructure.Persistence;

public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly string _connectionString;

    public SqliteSettingsStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task SetAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var hasUpdatedAt = await HasUpdatedAtColumnAsync(
            connection,
            cancellationToken);
        command.CommandText = hasUpdatedAt
            ?
            """
            INSERT INTO Settings (Key, Value, UpdatedAt)
            VALUES ($key, $value, $updatedAt)
            ON CONFLICT(Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedAt = excluded.UpdatedAt;
            """
            :
            """
            INSERT INTO Settings (Key, Value)
            VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        if (hasUpdatedAt)
        {
            command.Parameters.AddWithValue(
                "$updatedAt",
                DateTimeOffset.UtcNow.ToString("O"));
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasUpdatedAtColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(Settings);";
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(
                    reader.GetString(1),
                    "UpdatedAt",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
