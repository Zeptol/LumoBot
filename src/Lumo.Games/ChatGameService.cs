using System.Text;
using Lumo.Application;
using Lumo.Domain;

namespace Lumo.Games;

public sealed record ChatMessageContext(
    string Platform,
    string ChatId,
    string UserId,
    string DisplayName,
    string Text);

public enum ChatActionKind
{
    Text = 1,
    Image = 2,
    Audio = 3
}

public sealed record ChatAction(
    ChatActionKind Kind,
    string Text,
    string? MediaUrl = null);

public sealed class ChatGameService
{
    private readonly GameEngine _gameEngine;
    private readonly ILumoStore _store;

    public ChatGameService(GameEngine gameEngine, ILumoStore store)
    {
        _gameEngine = gameEngine;
        _store = store;
    }

    public async Task<IReadOnlyList<ChatAction>> HandleAsync(
        ChatMessageContext message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message.Text))
        {
            return [];
        }

        var text = message.Text.Trim();
        var command = ParseCommand(text);
        var player = new ChatIdentity(
            message.Platform,
            message.ChatId,
            message.UserId,
            message.DisplayName);

        if (TryGetGameMode(command, text, out var gameMode))
        {
            var started = await _gameEngine.StartAsync(
                message.Platform,
                message.ChatId,
                gameMode,
                cancellationToken);

            if (started is null)
            {
                return [new ChatAction(
                    ChatActionKind.Text,
                    $"Lumo 里暂时没有 {GetGameName(gameMode)} 的可用题目。可以先导入题库素材。")];
            }

            var caption = $"{started.Question.Prompt}\n\n⏱️ 30 秒内直接发送答案，第一位答对者得分。";
            return started.Question.MediaKind switch
            {
                MediaKind.Image when !string.IsNullOrWhiteSpace(started.Question.MediaUrl)
                    => [new ChatAction(ChatActionKind.Image, caption, started.Question.MediaUrl)],
                MediaKind.Audio when !string.IsNullOrWhiteSpace(started.Question.MediaUrl)
                    => [new ChatAction(ChatActionKind.Audio, caption, started.Question.MediaUrl)],
                _ => [new ChatAction(ChatActionKind.Text, caption)]
            };
        }

        if (command is "/start" or "/help" or "/menu" || text is "菜单" or "帮助")
        {
            return [new ChatAction(ChatActionKind.Text, BuildHelpText())];
        }

        if (command is "/coins" or "/score" || text is "金币" or "积分")
        {
            return [new ChatAction(
                ChatActionKind.Text,
                await BuildScoreTextAsync(player, cancellationToken))];
        }

        if (command is "/rank" or "/leaderboard" || text is "排行" or "排行榜")
        {
            return [new ChatAction(
                ChatActionKind.Text,
                await BuildLeaderboardTextAsync(
                    message.Platform,
                    message.ChatId,
                    cancellationToken))];
        }

        if (command is "/stop" or "/end" || text is "结束" or "结束游戏")
        {
            var stopped = _gameEngine.Stop(message.Platform, message.ChatId);
            return [new ChatAction(
                ChatActionKind.Text,
                stopped ? "本轮游戏已结束。" : "当前没有正在进行的游戏。")];
        }

        var answer = await _gameEngine.SubmitAnswerAsync(player, text, cancellationToken);
        return answer.Outcome switch
        {
            AnswerOutcome.Correct => [new ChatAction(
                ChatActionKind.Text,
                $"🎉 {player.DisplayName} 答对了！\n答案：{answer.Question!.Answer}\n🪙 +{answer.RewardCoins}，当前金币 {answer.UpdatedScore!.Coins}")],
            AnswerOutcome.Expired => [new ChatAction(
                ChatActionKind.Text,
                $"⏰ 时间到。答案是：{answer.Question!.Answer}")],
            _ => []
        };
    }

    private async Task<string> BuildScoreTextAsync(
        ChatIdentity player,
        CancellationToken cancellationToken)
    {
        var score = await _store.GetScoreAsync(player, cancellationToken);
        return score is null
            ? $"🪙 {player.DisplayName} 还没有积分，先来玩一局吧。"
            : $"🪙 {score.DisplayName}\n金币：{score.Coins}\n答对：{score.CorrectAnswers} 题";
    }

    private async Task<string> BuildLeaderboardTextAsync(
        string platform,
        string chatId,
        CancellationToken cancellationToken)
    {
        var scores = await _store.GetLeaderboardAsync(platform, chatId, 10, cancellationToken);
        if (scores.Count == 0)
        {
            return "🏆 本群排行榜还是空的。";
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

        return builder.ToString().TrimEnd();
    }

    public static string BuildHelpText()
    {
        return """
            ✨ Lumo 游戏菜单

            🎵 猜歌 / /guesssong
            🎬 猜电影 / /guessmovie
            🖼️ 猜图 / /guessimage
            🀄 猜成语 / /guessidiom

            🪙 金币 / /coins
            🏆 排行榜 / /rank
            ⏹️ 结束游戏 / /stop

            开始题目后直接在群里发送答案即可抢答。
            """;
    }

    private static bool TryGetGameMode(string command, string originalText, out GameMode gameMode)
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

    private static string ParseCommand(string text)
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

    private static string GetGameName(GameMode gameMode)
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
}
