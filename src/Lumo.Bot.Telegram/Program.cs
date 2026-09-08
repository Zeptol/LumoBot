using System.Globalization;
using System.Text;
using Lumo.Application;
using Lumo.Bot.Telegram;
using Lumo.Domain;
using Lumo.Games;
using Lumo.Infrastructure;

const string Platform = "telegram";

var token = Environment.GetEnvironmentVariable("LUMO_TELEGRAM_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("Missing LUMO_TELEGRAM_TOKEN environment variable.");
    return 2;
}

var databasePath = Environment.GetEnvironmentVariable("LUMO_DATABASE_PATH");
if (string.IsNullOrWhiteSpace(databasePath))
{
    databasePath = Path.Combine(Directory.GetCurrentDirectory(), "data", "lumo.db");
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

ILumoStore store = new SqliteLumoStore(databasePath);
await store.InitializeAsync(cancellation.Token);
var gameEngine = new GameEngine(store);

using var httpClient = new HttpClient();
var telegram = new TelegramApiClient(token, httpClient);

Console.WriteLine($"Lumo Telegram bot started. Database: {Path.GetFullPath(databasePath)}");

long offset = 0;
while (!cancellation.IsCancellationRequested)
{
    try
    {
        var updates = await telegram.GetUpdatesAsync(offset, cancellation.Token);
        foreach (var update in updates)
        {
            offset = Math.Max(offset, update.UpdateId + 1);
            if (update.Message is { } message)
            {
                await HandleMessageAsync(
                    telegram,
                    gameEngine,
                    store,
                    message,
                    cancellation.Token);
            }
        }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        break;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] {exception}");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            break;
        }
    }
}

Console.WriteLine("Lumo Telegram bot stopped.");
return 0;

static async Task HandleMessageAsync(
    TelegramApiClient telegram,
    GameEngine gameEngine,
    ILumoStore store,
    TelegramMessage message,
    CancellationToken cancellationToken)
{
    if (message.From is null || message.From.IsBot || string.IsNullOrWhiteSpace(message.Text))
    {
        return;
    }

    var text = message.Text.Trim();
    var chatId = message.Chat.Id;
    var chatIdText = chatId.ToString(CultureInfo.InvariantCulture);
    var userId = message.From.Id.ToString(CultureInfo.InvariantCulture);
    var player = new ChatIdentity(Platform, chatIdText, userId, message.From.DisplayName);

    var command = ParseCommand(text);
    if (TryGetGameMode(command, text, out var gameMode))
    {
        var started = await gameEngine.StartAsync(Platform, chatIdText, gameMode, cancellationToken);
        if (started is null)
        {
            await telegram.SendTextAsync(
                chatId,
                $"Lumo 里暂时没有 {GetGameName(gameMode)} 的可用题目。可以先导入题库素材。",
                cancellationToken);
            return;
        }

        var caption = $"{started.Question.Prompt}\n\n⏱️ 30 秒内直接发送答案，第一位答对者得分。";
        if (started.Question.MediaKind == MediaKind.Image &&
            !string.IsNullOrWhiteSpace(started.Question.MediaUrl))
        {
            await telegram.SendPhotoAsync(chatId, started.Question.MediaUrl, caption, cancellationToken);
        }
        else if (started.Question.MediaKind == MediaKind.Audio &&
                 !string.IsNullOrWhiteSpace(started.Question.MediaUrl))
        {
            await telegram.SendAudioAsync(chatId, started.Question.MediaUrl, caption, cancellationToken);
        }
        else
        {
            await telegram.SendTextAsync(chatId, caption, cancellationToken);
        }

        return;
    }

    switch (command)
    {
        case "/start":
        case "/help":
        case "/menu":
            await telegram.SendTextAsync(chatId, BuildHelpText(), cancellationToken);
            return;

        case "/coins":
        case "/score":
            await SendScoreAsync(telegram, store, chatId, player, cancellationToken);
            return;

        case "/rank":
        case "/leaderboard":
            await SendLeaderboardAsync(telegram, store, chatId, cancellationToken);
            return;

        case "/stop":
        case "/end":
            await telegram.SendTextAsync(
                chatId,
                gameEngine.Stop(Platform, chatIdText) ? "本轮游戏已结束。" : "当前没有正在进行的游戏。",
                cancellationToken);
            return;
    }

    if (text is "菜单" or "帮助")
    {
        await telegram.SendTextAsync(chatId, BuildHelpText(), cancellationToken);
        return;
    }

    if (text is "金币" or "积分")
    {
        await SendScoreAsync(telegram, store, chatId, player, cancellationToken);
        return;
    }

    if (text is "排行" or "排行榜")
    {
        await SendLeaderboardAsync(telegram, store, chatId, cancellationToken);
        return;
    }

    if (text is "结束" or "结束游戏")
    {
        await telegram.SendTextAsync(
            chatId,
            gameEngine.Stop(Platform, chatIdText) ? "本轮游戏已结束。" : "当前没有正在进行的游戏。",
            cancellationToken);
        return;
    }

    var answer = await gameEngine.SubmitAnswerAsync(player, text, cancellationToken);
    switch (answer.Outcome)
    {
        case AnswerOutcome.Correct:
            await telegram.SendTextAsync(
                chatId,
                $"🎉 {player.DisplayName} 答对了！\n答案：{answer.Question!.Answer}\n🪙 +{answer.RewardCoins}，当前金币 {answer.UpdatedScore!.Coins}",
                cancellationToken);
            break;

        case AnswerOutcome.Expired:
            await telegram.SendTextAsync(
                chatId,
                $"⏰ 时间到。答案是：{answer.Question!.Answer}",
                cancellationToken);
            break;

        case AnswerOutcome.NoActiveGame:
        case AnswerOutcome.Incorrect:
        case AnswerOutcome.AlreadyCompleted:
        default:
            break;
    }
}

static bool TryGetGameMode(string command, string originalText, out GameMode gameMode)
{
    gameMode = command switch
    {
        "/guesssong" or "/guess_song" => GameMode.GuessSong,
        "/guessmovie" or "/guess_movie" => GameMode.GuessMovie,
        "/guessimage" or "/guess_image" => GameMode.GuessImage,
        "/guessidiom" or "/guess_idiom" => GameMode.GuessIdiom,
        _ => default
    };

    if (gameMode != default)
    {
        return true;
    }

    gameMode = originalText.Trim() switch
    {
        "猜歌" or "猜歌曲" => GameMode.GuessSong,
        "猜电影" => GameMode.GuessMovie,
        "猜图" or "猜图片" => GameMode.GuessImage,
        "猜成语" => GameMode.GuessIdiom,
        _ => default
    };

    return gameMode != default;
}

static string ParseCommand(string text)
{
    if (!text.StartsWith('/'))
    {
        return string.Empty;
    }

    var command = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    var mentionIndex = command.IndexOf('@');
    if (mentionIndex >= 0)
    {
        command = command[..mentionIndex];
    }

    return command.ToLowerInvariant();
}

static string GetGameName(GameMode gameMode)
{
    return gameMode switch
    {
        GameMode.GuessSong => "猜歌曲",
        GameMode.GuessMovie => "猜电影",
        GameMode.GuessImage => "猜图",
        GameMode.GuessIdiom => "猜成语",
        _ => "游戏"
    };
}

static string BuildHelpText()
{
    return """
        ✨ Lumo 游戏菜单

        🎵 /guesssong  猜歌曲
        🎬 /guessmovie 猜电影
        🖼️ /guessimage 猜图（混合题库）
        🀄 /guessidiom 猜成语

        🪙 /coins 查看金币
        🏆 /rank 查看本群排行
        ⏹️ /stop 结束当前回合

        也可以直接发送：猜歌、猜电影、猜图、猜成语、金币、排行榜。
        开始题目后直接在群里发送答案即可抢答。
        """;
}

static async Task SendScoreAsync(
    TelegramApiClient telegram,
    ILumoStore store,
    long chatId,
    ChatIdentity player,
    CancellationToken cancellationToken)
{
    var score = await store.GetScoreAsync(player, cancellationToken);
    var text = score is null
        ? $"🪙 {player.DisplayName} 还没有积分，先来玩一局吧。"
        : $"🪙 {score.DisplayName}\n金币：{score.Coins}\n答对：{score.CorrectAnswers} 题";

    await telegram.SendTextAsync(chatId, text, cancellationToken);
}

static async Task SendLeaderboardAsync(
    TelegramApiClient telegram,
    ILumoStore store,
    long chatId,
    CancellationToken cancellationToken)
{
    var chatIdText = chatId.ToString(CultureInfo.InvariantCulture);
    var scores = await store.GetLeaderboardAsync(Platform, chatIdText, 10, cancellationToken);
    if (scores.Count == 0)
    {
        await telegram.SendTextAsync(chatId, "🏆 本群排行榜还是空的。", cancellationToken);
        return;
    }

    var builder = new StringBuilder("🏆 Lumo 本群排行榜\n\n");
    for (var index = 0; index < scores.Count; index++)
    {
        var prefix = index switch
        {
            0 => "🥇",
            1 => "🥈",
            2 => "🥉",
            _ => $"{index + 1}."
        };

        var score = scores[index];
        builder.AppendLine($"{prefix} {score.DisplayName}  {score.Coins} 🪙  ·  {score.CorrectAnswers} 题");
    }

    await telegram.SendTextAsync(chatId, builder.ToString().TrimEnd(), cancellationToken);
}
