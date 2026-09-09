using Lumo.Application;
using Lumo.Domain;
using Microsoft.Data.Sqlite;

namespace Lumo.Infrastructure;

public sealed class SqliteQuestionCandidateSource : IQuestionCandidateSource
{
    private readonly string _connectionString;

    public SqliteQuestionCandidateSource(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task<IReadOnlyList<Question>> GetCandidatesAsync(
        GameMode gameMode,
        int limit,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 256);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var maxId = await GetMaxQuestionIdAsync(connection, gameMode, cancellationToken);
        if (maxId <= 0)
        {
            return [];
        }

        var rows = new Dictionary<long, CandidateRow>();
        const int windows = 4;
        var perWindow = Math.Max(1, (int)Math.Ceiling(limit / (double)windows));

        for (var index = 0; index < windows && rows.Count < limit; index++)
        {
            var startId = Random.Shared.NextInt64(1, maxId + 1);
            await LoadWindowAsync(
                connection,
                gameMode,
                startId,
                greaterThanOrEqual: true,
                perWindow,
                rows,
                cancellationToken);

            if (rows.Count >= limit)
            {
                break;
            }

            await LoadWindowAsync(
                connection,
                gameMode,
                startId,
                greaterThanOrEqual: false,
                Math.Max(1, perWindow / 2),
                rows,
                cancellationToken);
        }

        if (rows.Count < Math.Min(limit, 16))
        {
            await LoadFirstRowsAsync(
                connection,
                gameMode,
                limit - rows.Count,
                rows,
                cancellationToken);
        }

        var selectedRows = rows.Values.Take(limit).ToArray();
        if (selectedRows.Length == 0)
        {
            return [];
        }

        var ids = selectedRows.Select(row => row.Id).ToArray();
        var aliases = await LoadStringMapAsync(
            connection,
            "QuestionAliases",
            "Alias",
            ids,
            cancellationToken);
        var tags = await LoadStringMapAsync(
            connection,
            "QuestionTags",
            "Tag",
            ids,
            cancellationToken);

        return selectedRows
            .Select(row => new Question(
                row.Id,
                row.EntityId,
                row.GameMode,
                row.EntityType,
                row.EntityName,
                row.Prompt,
                row.Answer,
                row.Difficulty,
                row.MediaKind,
                row.MediaUrl,
                row.SourceUrl,
                row.License,
                aliases.GetValueOrDefault(row.Id) ?? [],
                tags.GetValueOrDefault(row.Id) ?? []))
            .ToArray();
    }

    private static async Task<long> GetMaxQuestionIdAsync(
        SqliteConnection connection,
        GameMode gameMode,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(MAX(Id), 0)
            FROM Questions
            WHERE GameMode = $gameMode
              AND Enabled = 1;
            """;
        command.Parameters.AddWithValue("$gameMode", (int)gameMode);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task LoadWindowAsync(
        SqliteConnection connection,
        GameMode gameMode,
        long startId,
        bool greaterThanOrEqual,
        int take,
        IDictionary<long, CandidateRow> destination,
        CancellationToken cancellationToken)
    {
        if (take <= 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                q.Id,
                q.EntityId,
                q.GameMode,
                COALESCE(e.EntityType, ''),
                COALESCE(e.Name, q.Answer),
                q.Prompt,
                q.Answer,
                q.Difficulty,
                q.MediaKind,
                q.MediaUrl,
                q.SourceUrl,
                q.License
            FROM Questions q
            LEFT JOIN Entities e ON e.Id = q.EntityId
            WHERE q.GameMode = $gameMode
              AND q.Enabled = 1
              AND q.Id {(greaterThanOrEqual ? ">=" : "<")} $startId
            ORDER BY q.Id
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$gameMode", (int)gameMode);
        command.Parameters.AddWithValue("$startId", startId);
        command.Parameters.AddWithValue("$take", take);
        await ReadRowsAsync(command, destination, cancellationToken);
    }

    private static async Task LoadFirstRowsAsync(
        SqliteConnection connection,
        GameMode gameMode,
        int take,
        IDictionary<long, CandidateRow> destination,
        CancellationToken cancellationToken)
    {
        if (take <= 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                q.Id,
                q.EntityId,
                q.GameMode,
                COALESCE(e.EntityType, ''),
                COALESCE(e.Name, q.Answer),
                q.Prompt,
                q.Answer,
                q.Difficulty,
                q.MediaKind,
                q.MediaUrl,
                q.SourceUrl,
                q.License
            FROM Questions q
            LEFT JOIN Entities e ON e.Id = q.EntityId
            WHERE q.GameMode = $gameMode
              AND q.Enabled = 1
            ORDER BY q.Id
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$gameMode", (int)gameMode);
        command.Parameters.AddWithValue("$take", take);
        await ReadRowsAsync(command, destination, cancellationToken);
    }

    private static async Task ReadRowsAsync(
        SqliteCommand command,
        IDictionary<long, CandidateRow> destination,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new CandidateRow(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                (GameMode)reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7),
                (MediaKind)reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11));
            destination.TryAdd(row.Id, row);
        }
    }

    private static async Task<Dictionary<long, IReadOnlyList<string>>> LoadStringMapAsync(
        SqliteConnection connection,
        string tableName,
        string valueColumn,
        IReadOnlyList<long> questionIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, IReadOnlyList<string>>();
        if (questionIds.Count == 0)
        {
            return result;
        }

        var parameterNames = questionIds
            .Select((_, index) => $"$id{index}")
            .ToArray();

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT QuestionId, {valueColumn}
            FROM {tableName}
            WHERE QuestionId IN ({string.Join(',', parameterNames)})
            ORDER BY QuestionId, Id;
            """;

        for (var index = 0; index < questionIds.Count; index++)
        {
            command.Parameters.AddWithValue(parameterNames[index], questionIds[index]);
        }

        var mutable = new Dictionary<long, List<string>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var questionId = reader.GetInt64(0);
            if (!mutable.TryGetValue(questionId, out var values))
            {
                values = [];
                mutable[questionId] = values;
            }

            values.Add(reader.GetString(1));
        }

        foreach (var pair in mutable)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private sealed record CandidateRow(
        long Id,
        long? EntityId,
        GameMode GameMode,
        string EntityType,
        string EntityName,
        string Prompt,
        string Answer,
        int Difficulty,
        MediaKind MediaKind,
        string? MediaUrl,
        string? SourceUrl,
        string? License);
}
