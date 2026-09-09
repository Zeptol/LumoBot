using System.Globalization;
using Lumo.Application;
using Lumo.Domain;
using Microsoft.Data.Sqlite;

namespace Lumo.Infrastructure;

public sealed class SqliteLumoStore : ILumoStore
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteLumoStore(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS Entities (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EntityType TEXT NOT NULL,
                Name TEXT NOT NULL,
                MetadataJson TEXT NULL,
                UNIQUE(EntityType, Name)
            );

            CREATE TABLE IF NOT EXISTS Questions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EntityId INTEGER NULL,
                ExternalKey TEXT NULL,
                GameMode INTEGER NOT NULL,
                Prompt TEXT NOT NULL,
                Answer TEXT NOT NULL,
                Difficulty INTEGER NOT NULL DEFAULT 1,
                MediaKind INTEGER NOT NULL DEFAULT 0,
                MediaUrl TEXT NULL,
                SourceUrl TEXT NULL,
                License TEXT NULL,
                Enabled INTEGER NOT NULL DEFAULT 1,
                FOREIGN KEY(EntityId) REFERENCES Entities(Id)
            );

            CREATE TABLE IF NOT EXISTS QuestionAliases (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                QuestionId INTEGER NOT NULL,
                Alias TEXT NOT NULL,
                UNIQUE(QuestionId, Alias),
                FOREIGN KEY(QuestionId) REFERENCES Questions(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS QuestionTags (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                QuestionId INTEGER NOT NULL,
                Tag TEXT NOT NULL,
                UNIQUE(QuestionId, Tag),
                FOREIGN KEY(QuestionId) REFERENCES Questions(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Scores (
                Platform TEXT NOT NULL,
                ChatId TEXT NOT NULL,
                UserId TEXT NOT NULL,
                DisplayName TEXT NOT NULL,
                Coins INTEGER NOT NULL DEFAULT 0,
                CorrectAnswers INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY(Platform, ChatId, UserId)
            );

            CREATE INDEX IF NOT EXISTS IX_Questions_GameMode_Enabled
                ON Questions(GameMode, Enabled);

            CREATE INDEX IF NOT EXISTS IX_Scores_Leaderboard
                ON Scores(Platform, ChatId, Coins DESC, CorrectAnswers DESC);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "Questions", "ExternalKey", "TEXT NULL", cancellationToken);

        await using (var indexCommand = connection.CreateCommand())
        {
            indexCommand.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS UX_Questions_GameMode_ExternalKey
                    ON Questions(GameMode, ExternalKey)
                    WHERE ExternalKey IS NOT NULL;
                """;
            await indexCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await SeedAsync(connection, cancellationToken);
    }

    public async Task<Question?> GetRandomQuestionAsync(
        GameMode gameMode,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
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
            ORDER BY RANDOM()
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$gameMode", (int)gameMode);

        long id;
        long? entityId;
        GameMode storedMode;
        string entityType;
        string entityName;
        string prompt;
        string answer;
        int difficulty;
        MediaKind mediaKind;
        string? mediaUrl;
        string? sourceUrl;
        string? license;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            id = reader.GetInt64(0);
            entityId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            storedMode = (GameMode)reader.GetInt32(2);
            entityType = reader.GetString(3);
            entityName = reader.GetString(4);
            prompt = reader.GetString(5);
            answer = reader.GetString(6);
            difficulty = reader.GetInt32(7);
            mediaKind = (MediaKind)reader.GetInt32(8);
            mediaUrl = reader.IsDBNull(9) ? null : reader.GetString(9);
            sourceUrl = reader.IsDBNull(10) ? null : reader.GetString(10);
            license = reader.IsDBNull(11) ? null : reader.GetString(11);
        }

        var aliases = await GetStringsAsync(
            connection,
            "SELECT Alias FROM QuestionAliases WHERE QuestionId = $questionId ORDER BY Id;",
            id,
            cancellationToken);

        var tags = await GetStringsAsync(
            connection,
            "SELECT Tag FROM QuestionTags WHERE QuestionId = $questionId ORDER BY Id;",
            id,
            cancellationToken);

        return new Question(
            id,
            entityId,
            storedMode,
            entityType,
            entityName,
            prompt,
            answer,
            difficulty,
            mediaKind,
            mediaUrl,
            sourceUrl,
            license,
            aliases,
            tags);
    }

    public async Task<long> UpsertQuestionAsync(
        CatalogQuestionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.ExternalKey))
        {
            throw new ArgumentException("ExternalKey is required for imported questions.", nameof(input));
        }

        if (string.IsNullOrWhiteSpace(input.EntityType) ||
            string.IsNullOrWhiteSpace(input.EntityName) ||
            string.IsNullOrWhiteSpace(input.Answer))
        {
            throw new ArgumentException("EntityType, EntityName and Answer are required.", nameof(input));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await UpsertQuestionAsync(connection, input, cancellationToken);
    }

    public async Task<long> GetQuestionCountAsync(
        GameMode? gameMode = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = gameMode is null
            ? "SELECT COUNT(*) FROM Questions WHERE Enabled = 1;"
            : "SELECT COUNT(*) FROM Questions WHERE Enabled = 1 AND GameMode = $gameMode;";

        if (gameMode is not null)
        {
            command.Parameters.AddWithValue("$gameMode", (int)gameMode.Value);
        }

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    public async Task<Score> AddCorrectAnswerAsync(
        ChatIdentity player,
        int rewardCoins,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Scores (
                Platform, ChatId, UserId, DisplayName, Coins, CorrectAnswers, UpdatedAt)
            VALUES (
                $platform, $chatId, $userId, $displayName, $coins, 1, $updatedAt)
            ON CONFLICT(Platform, ChatId, UserId) DO UPDATE SET
                DisplayName = excluded.DisplayName,
                Coins = Scores.Coins + excluded.Coins,
                CorrectAnswers = Scores.CorrectAnswers + 1,
                UpdatedAt = excluded.UpdatedAt;
            """;
        command.Parameters.AddWithValue("$platform", player.Platform);
        command.Parameters.AddWithValue("$chatId", player.ChatId);
        command.Parameters.AddWithValue("$userId", player.UserId);
        command.Parameters.AddWithValue("$displayName", player.DisplayName);
        command.Parameters.AddWithValue("$coins", rewardCoins);
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return (await GetScoreAsync(player, cancellationToken))
            ?? throw new InvalidOperationException("Score row was not created.");
    }

    public async Task<Score?> GetScoreAsync(
        ChatIdentity player,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Platform, ChatId, UserId, DisplayName, Coins, CorrectAnswers, UpdatedAt
            FROM Scores
            WHERE Platform = $platform
              AND ChatId = $chatId
              AND UserId = $userId;
            """;
        command.Parameters.AddWithValue("$platform", player.Platform);
        command.Parameters.AddWithValue("$chatId", player.ChatId);
        command.Parameters.AddWithValue("$userId", player.UserId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadScore(reader) : null;
    }

    public async Task<IReadOnlyList<Score>> GetLeaderboardAsync(
        string platform,
        string chatId,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 50);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Platform, ChatId, UserId, DisplayName, Coins, CorrectAnswers, UpdatedAt
            FROM Scores
            WHERE Platform = $platform
              AND ChatId = $chatId
            ORDER BY Coins DESC, CorrectAnswers DESC, UpdatedAt ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$platform", platform);
        command.Parameters.AddWithValue("$chatId", chatId);
        command.Parameters.AddWithValue("$limit", limit);

        var scores = new List<Score>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            scores.Add(ReadScore(reader));
        }

        return scores;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static Score ReadScore(SqliteDataReader reader)
    {
        return new Score(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            DateTimeOffset.Parse(
                reader.GetString(6),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
    }

    private static async Task<IReadOnlyList<string>> GetStringsAsync(
        SqliteConnection connection,
        string sql,
        long questionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$questionId", questionId);

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({tableName});";

        var exists = false;
        await using (var reader = await pragma.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM Questions;";
        var count = Convert.ToInt64(
            await countCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        if (count > 0)
        {
            return;
        }

        var seedQuestions = new CatalogQuestionInput[]
        {
            new(
                "seed:idiom:画蛇添足",
                GameMode.GuessIdiom,
                "Idiom",
                "画蛇添足",
                "🀄 猜成语：比喻做了多余的事，反而不恰当。",
                "画蛇添足",
                1,
                MediaKind.None,
                null,
                null,
                null,
                ["画蛇添足"],
                ["成语", "文学"]),
            new(
                "seed:idiom:守株待兔",
                GameMode.GuessIdiom,
                "Idiom",
                "守株待兔",
                "🀄 猜成语：比喻不主动努力，却存侥幸心理，希望得到意外收获。",
                "守株待兔",
                1,
                MediaKind.None,
                null,
                null,
                null,
                ["守株待兔"],
                ["成语", "文学"]),
            new(
                "seed:movie:让子弹飞",
                GameMode.GuessMovie,
                "Movie",
                "让子弹飞",
                "🎬 猜电影：导演姜文，主演姜文、葛优、周润发，2010 年上映。",
                "让子弹飞",
                1,
                MediaKind.None,
                null,
                null,
                null,
                ["Let the Bullets Fly"],
                ["电影", "华语", "2010s"]),
            new(
                "seed:movie:星际穿越",
                GameMode.GuessMovie,
                "Movie",
                "星际穿越",
                "🎬 猜电影：克里斯托弗·诺兰执导，故事涉及虫洞、黑洞和跨越时间的亲情。",
                "星际穿越",
                1,
                MediaKind.None,
                null,
                null,
                null,
                ["Interstellar", "星际启示录"],
                ["电影", "科幻", "欧美", "2010s"]),
            new(
                "seed:image:eiffel-tower",
                GameMode.GuessImage,
                "Landmark",
                "埃菲尔铁塔",
                "🖼️ 猜图：这是哪个著名地标？",
                "埃菲尔铁塔",
                1,
                MediaKind.Image,
                "https://upload.wikimedia.org/wikipedia/commons/a/a8/Tour_Eiffel_Wikimedia_Commons.jpg",
                "https://commons.wikimedia.org/wiki/File:Tour_Eiffel_Wikimedia_Commons.jpg",
                "Wikimedia Commons; verify file license before redistribution",
                ["Eiffel Tower", "巴黎铁塔"],
                ["地标", "法国", "巴黎", "欧洲"])
        };

        foreach (var question in seedQuestions)
        {
            await UpsertQuestionAsync(connection, question, cancellationToken);
        }
    }

    private static async Task<long> UpsertQuestionAsync(
        SqliteConnection connection,
        CatalogQuestionInput input,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();

        long entityId;
        await using (var entityCommand = connection.CreateCommand())
        {
            entityCommand.Transaction = transaction;
            entityCommand.CommandText = """
                INSERT INTO Entities (EntityType, Name, MetadataJson)
                VALUES ($entityType, $name, $metadataJson)
                ON CONFLICT(EntityType, Name) DO UPDATE SET
                    MetadataJson = COALESCE(excluded.MetadataJson, Entities.MetadataJson)
                RETURNING Id;
                """;
            entityCommand.Parameters.AddWithValue("$entityType", input.EntityType.Trim());
            entityCommand.Parameters.AddWithValue("$name", input.EntityName.Trim());
            entityCommand.Parameters.AddWithValue(
                "$metadataJson",
                (object?)input.EntityMetadataJson ?? DBNull.Value);
            entityId = Convert.ToInt64(
                await entityCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        long? questionId = null;
        await using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.Transaction = transaction;
            existingCommand.CommandText = """
                SELECT Id
                FROM Questions
                WHERE GameMode = $gameMode
                  AND ExternalKey = $externalKey
                LIMIT 1;
                """;
            existingCommand.Parameters.AddWithValue("$gameMode", (int)input.GameMode);
            existingCommand.Parameters.AddWithValue("$externalKey", input.ExternalKey.Trim());
            var existing = await existingCommand.ExecuteScalarAsync(cancellationToken);
            if (existing is not null && existing is not DBNull)
            {
                questionId = Convert.ToInt64(existing, CultureInfo.InvariantCulture);
            }
        }

        if (questionId is null)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO Questions (
                    EntityId, ExternalKey, GameMode, Prompt, Answer, Difficulty,
                    MediaKind, MediaUrl, SourceUrl, License, Enabled)
                VALUES (
                    $entityId, $externalKey, $gameMode, $prompt, $answer, $difficulty,
                    $mediaKind, $mediaUrl, $sourceUrl, $license, 1)
                RETURNING Id;
                """;
            AddQuestionParameters(insertCommand, entityId, input);
            questionId = Convert.ToInt64(
                await insertCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }
        else
        {
            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                UPDATE Questions SET
                    EntityId = $entityId,
                    Prompt = $prompt,
                    Answer = $answer,
                    Difficulty = $difficulty,
                    MediaKind = $mediaKind,
                    MediaUrl = $mediaUrl,
                    SourceUrl = $sourceUrl,
                    License = $license,
                    Enabled = 1
                WHERE Id = $questionId;
                """;
            AddQuestionParameters(updateCommand, entityId, input);
            updateCommand.Parameters.AddWithValue("$questionId", questionId.Value);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await DeleteQuestionMetadataAsync(connection, transaction, questionId.Value, cancellationToken);

        foreach (var alias in input.Aliases
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(value => value.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var aliasCommand = connection.CreateCommand();
            aliasCommand.Transaction = transaction;
            aliasCommand.CommandText = """
                INSERT OR IGNORE INTO QuestionAliases (QuestionId, Alias)
                VALUES ($questionId, $alias);
                """;
            aliasCommand.Parameters.AddWithValue("$questionId", questionId.Value);
            aliasCommand.Parameters.AddWithValue("$alias", alias);
            await aliasCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var tag in input.Tags
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(value => value.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var tagCommand = connection.CreateCommand();
            tagCommand.Transaction = transaction;
            tagCommand.CommandText = """
                INSERT OR IGNORE INTO QuestionTags (QuestionId, Tag)
                VALUES ($questionId, $tag);
                """;
            tagCommand.Parameters.AddWithValue("$questionId", questionId.Value);
            tagCommand.Parameters.AddWithValue("$tag", tag);
            await tagCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return questionId.Value;
    }

    private static void AddQuestionParameters(
        SqliteCommand command,
        long entityId,
        CatalogQuestionInput input)
    {
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue("$externalKey", input.ExternalKey.Trim());
        command.Parameters.AddWithValue("$gameMode", (int)input.GameMode);
        command.Parameters.AddWithValue("$prompt", input.Prompt.Trim());
        command.Parameters.AddWithValue("$answer", input.Answer.Trim());
        command.Parameters.AddWithValue("$difficulty", Math.Clamp(input.Difficulty, 1, 10));
        command.Parameters.AddWithValue("$mediaKind", (int)input.MediaKind);
        command.Parameters.AddWithValue("$mediaUrl", (object?)input.MediaUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceUrl", (object?)input.SourceUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$license", (object?)input.License ?? DBNull.Value);
    }

    private static async Task DeleteQuestionMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long questionId,
        CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "QuestionAliases", "QuestionTags" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE QuestionId = $questionId;";
            command.Parameters.AddWithValue("$questionId", questionId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
