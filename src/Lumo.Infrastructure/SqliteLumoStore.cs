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

        await InsertQuestionAsync(
            connection,
            "Idiom",
            "画蛇添足",
            GameMode.GuessIdiom,
            "🀄 猜成语：比喻做了多余的事，反而不恰当。",
            "画蛇添足",
            1,
            MediaKind.None,
            null,
            null,
            null,
            ["画蛇添足"],
            ["成语", "文学"],
            cancellationToken);

        await InsertQuestionAsync(
            connection,
            "Idiom",
            "守株待兔",
            GameMode.GuessIdiom,
            "🀄 猜成语：比喻不主动努力，却存侥幸心理，希望得到意外收获。",
            "守株待兔",
            1,
            MediaKind.None,
            null,
            null,
            null,
            ["守株待兔"],
            ["成语", "文学"],
            cancellationToken);

        await InsertQuestionAsync(
            connection,
            "Movie",
            "让子弹飞",
            GameMode.GuessMovie,
            "🎬 猜电影：导演姜文，主演姜文、葛优、周润发，2010 年上映。",
            "让子弹飞",
            1,
            MediaKind.None,
            null,
            null,
            null,
            ["Let the Bullets Fly"],
            ["电影", "华语", "2010s"],
            cancellationToken);

        await InsertQuestionAsync(
            connection,
            "Movie",
            "星际穿越",
            GameMode.GuessMovie,
            "🎬 猜电影：克里斯托弗·诺兰执导，故事涉及虫洞、黑洞和跨越时间的亲情。",
            "星际穿越",
            1,
            MediaKind.None,
            null,
            null,
            null,
            ["Interstellar", "星际启示录"],
            ["电影", "科幻", "欧美", "2010s"],
            cancellationToken);

        await InsertQuestionAsync(
            connection,
            "Landmark",
            "埃菲尔铁塔",
            GameMode.GuessImage,
            "🖼️ 猜图：这是哪个著名地标？",
            "埃菲尔铁塔",
            1,
            MediaKind.Image,
            "https://upload.wikimedia.org/wikipedia/commons/a/a8/Tour_Eiffel_Wikimedia_Commons.jpg",
            "https://commons.wikimedia.org/wiki/File:Tour_Eiffel_Wikimedia_Commons.jpg",
            "Wikimedia Commons; verify file license before redistribution",
            ["Eiffel Tower", "巴黎铁塔"],
            ["地标", "法国", "巴黎", "欧洲"],
            cancellationToken);
    }

    private static async Task InsertQuestionAsync(
        SqliteConnection connection,
        string entityType,
        string entityName,
        GameMode gameMode,
        string prompt,
        string answer,
        int difficulty,
        MediaKind mediaKind,
        string? mediaUrl,
        string? sourceUrl,
        string? license,
        IReadOnlyCollection<string> aliases,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        long entityId;
        await using (var entityCommand = connection.CreateCommand())
        {
            entityCommand.Transaction = transaction;
            entityCommand.CommandText = """
                INSERT INTO Entities (EntityType, Name)
                VALUES ($entityType, $name)
                ON CONFLICT(EntityType, Name) DO UPDATE SET Name = excluded.Name
                RETURNING Id;
                """;
            entityCommand.Parameters.AddWithValue("$entityType", entityType);
            entityCommand.Parameters.AddWithValue("$name", entityName);
            entityId = Convert.ToInt64(
                await entityCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        long questionId;
        await using (var questionCommand = connection.CreateCommand())
        {
            questionCommand.Transaction = transaction;
            questionCommand.CommandText = """
                INSERT INTO Questions (
                    EntityId, GameMode, Prompt, Answer, Difficulty,
                    MediaKind, MediaUrl, SourceUrl, License, Enabled)
                VALUES (
                    $entityId, $gameMode, $prompt, $answer, $difficulty,
                    $mediaKind, $mediaUrl, $sourceUrl, $license, 1)
                RETURNING Id;
                """;
            questionCommand.Parameters.AddWithValue("$entityId", entityId);
            questionCommand.Parameters.AddWithValue("$gameMode", (int)gameMode);
            questionCommand.Parameters.AddWithValue("$prompt", prompt);
            questionCommand.Parameters.AddWithValue("$answer", answer);
            questionCommand.Parameters.AddWithValue("$difficulty", difficulty);
            questionCommand.Parameters.AddWithValue("$mediaKind", (int)mediaKind);
            questionCommand.Parameters.AddWithValue("$mediaUrl", (object?)mediaUrl ?? DBNull.Value);
            questionCommand.Parameters.AddWithValue("$sourceUrl", (object?)sourceUrl ?? DBNull.Value);
            questionCommand.Parameters.AddWithValue("$license", (object?)license ?? DBNull.Value);
            questionId = Convert.ToInt64(
                await questionCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        foreach (var alias in aliases.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            await using var aliasCommand = connection.CreateCommand();
            aliasCommand.Transaction = transaction;
            aliasCommand.CommandText = """
                INSERT OR IGNORE INTO QuestionAliases (QuestionId, Alias)
                VALUES ($questionId, $alias);
                """;
            aliasCommand.Parameters.AddWithValue("$questionId", questionId);
            aliasCommand.Parameters.AddWithValue("$alias", alias.Trim());
            await aliasCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var tag in tags.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            await using var tagCommand = connection.CreateCommand();
            tagCommand.Transaction = transaction;
            tagCommand.CommandText = """
                INSERT OR IGNORE INTO QuestionTags (QuestionId, Tag)
                VALUES ($questionId, $tag);
                """;
            tagCommand.Parameters.AddWithValue("$questionId", questionId);
            tagCommand.Parameters.AddWithValue("$tag", tag.Trim());
            await tagCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
