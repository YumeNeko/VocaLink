using System.Globalization;
using Microsoft.Data.Sqlite;
using VocaLink.Application.Abstractions;
using VocaLink.Domain;

namespace VocaLink.Infrastructure.Persistence;

/// <summary>
/// 唯一主会话的 SQLite 仓储。
/// </summary>
public sealed class SqliteConversationRepository : IConversationRepository
{
    private readonly string _connectionString;

    public SqliteConversationRepository(string databasePath)
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
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS UserProfile (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                DisplayName TEXT NOT NULL,
                PreferredLanguage TEXT NOT NULL,
                Notes TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ConversationMessage (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Role TEXT NOT NULL,
                Content TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                IsOffline INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS ContextSummary (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Summary TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AppState (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await MigrateConversationMessageAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, Role, Content, CreatedAt, IsOffline
            FROM ConversationMessage
            ORDER BY Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<ConversationMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ConversationMessage(
                reader.GetInt64(0),
                Enum.Parse<ConversationRole>(reader.GetString(1), true),
                reader.GetString(2),
                DateTimeOffset.Parse(
                    reader.GetString(3),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                reader.GetInt64(4) == 1));
        }

        result.Reverse();
        return result;
    }

    public async Task<ConversationMessage> AddAsync(
        ConversationRole role,
        string content,
        bool isOffline,
        CancellationToken cancellationToken = default)
    {
        var createdAt = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ConversationMessage (Role, Content, CreatedAt, IsOffline)
            VALUES ($role, $content, $createdAt, $isOffline);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$role", role.ToString());
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$createdAt", createdAt.ToString("O"));
        command.Parameters.AddWithValue("$isOffline", isOffline ? 1 : 0);

        var id = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        return new ConversationMessage(id, role, content, createdAt, isOffline);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task MigrateConversationMessageAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var inspect = connection.CreateCommand())
        {
            inspect.CommandText = "PRAGMA table_info(ConversationMessage);";
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains("CreatedAt") && columns.Contains("Timestamp"))
        {
            await ExecuteMigrationAsync(
                connection,
                "ALTER TABLE ConversationMessage RENAME COLUMN Timestamp TO CreatedAt;",
                cancellationToken);
            columns.Add("CreatedAt");
        }

        if (!columns.Contains("CreatedAt"))
        {
            await ExecuteMigrationAsync(
                connection,
                """
                ALTER TABLE ConversationMessage
                ADD COLUMN CreatedAt TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000+00:00';
                """,
                cancellationToken);
        }

        if (!columns.Contains("IsOffline"))
        {
            await ExecuteMigrationAsync(
                connection,
                """
                ALTER TABLE ConversationMessage
                ADD COLUMN IsOffline INTEGER NOT NULL DEFAULT 0;
                """,
                cancellationToken);
            columns.Add("IsOffline");
        }

        if (columns.Contains("Emotion"))
        {
            await ExecuteMigrationAsync(
                connection,
                """
                BEGIN IMMEDIATE;
                CREATE TABLE ConversationMessage_Migrated (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Role TEXT NOT NULL,
                    Content TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    IsOffline INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO ConversationMessage_Migrated
                    (Id, Role, Content, CreatedAt, IsOffline)
                SELECT
                    Id,
                    Role,
                    Content,
                    CreatedAt,
                    IsOffline
                FROM ConversationMessage;
                DROP TABLE ConversationMessage;
                ALTER TABLE ConversationMessage_Migrated
                    RENAME TO ConversationMessage;
                COMMIT;
                """,
                cancellationToken);
        }
    }

    private static async Task ExecuteMigrationAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
