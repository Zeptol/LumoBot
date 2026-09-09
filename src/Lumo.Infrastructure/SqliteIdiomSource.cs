using Lumo.Application;
using Microsoft.Data.Sqlite;

namespace Lumo.Infrastructure;

public sealed class SqliteIdiomSource : IIdiomSource
{
    private readonly string _connectionString;

    public SqliteIdiomSource(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task<string?> GetRandomIdiomAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT e.Name
            FROM Entities e
            INNER JOIN Questions q ON q.EntityId = e.Id
            WHERE e.EntityType = 'Idiom'
              AND q.Enabled = 1
              AND length(e.Name) = 4
            ORDER BY RANDOM()
            LIMIT 1;
            """;
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(
        string idiom,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM Entities e
                INNER JOIN Questions q ON q.EntityId = e.Id
                WHERE e.EntityType = 'Idiom'
                  AND e.Name = $idiom
                  AND q.Enabled = 1
                LIMIT 1
            );
            """;
        command.Parameters.AddWithValue("$idiom", idiom.Trim());
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<IReadOnlyList<string>> GetStartingWithAsync(
        string firstCharacter,
        int limit = 32,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 128);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT e.Name
            FROM Entities e
            INNER JOIN Questions q ON q.EntityId = e.Id
            WHERE e.EntityType = 'Idiom'
              AND q.Enabled = 1
              AND length(e.Name) = 4
              AND substr(e.Name, 1, 1) = $firstCharacter
            ORDER BY RANDOM()
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$firstCharacter", firstCharacter);
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
