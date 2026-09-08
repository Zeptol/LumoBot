using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Lumo.Application;
using Lumo.Domain;

namespace Lumo.Games;

public sealed class GameEngine
{
    private static readonly TimeSpan DefaultRoundDuration = TimeSpan.FromSeconds(30);

    private readonly ILumoStore _store;
    private readonly ConcurrentDictionary<string, GameSession> _sessions = new(StringComparer.Ordinal);

    public GameEngine(ILumoStore store)
    {
        _store = store;
    }

    public async Task<GameStartResult?> StartAsync(
        string platform,
        string chatId,
        GameMode gameMode,
        CancellationToken cancellationToken = default)
    {
        var question = await _store.GetRandomQuestionAsync(gameMode, cancellationToken);
        if (question is null)
        {
            return null;
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(DefaultRoundDuration);
        var key = BuildSessionKey(platform, chatId);
        _sessions[key] = new GameSession(question, expiresAt);

        return new GameStartResult(question, expiresAt);
    }

    public async Task<AnswerResult> SubmitAnswerAsync(
        ChatIdentity player,
        string answer,
        CancellationToken cancellationToken = default)
    {
        var key = BuildSessionKey(player.Platform, player.ChatId);
        if (!_sessions.TryGetValue(key, out var session))
        {
            return new AnswerResult(AnswerOutcome.NoActiveGame);
        }

        if (DateTimeOffset.UtcNow >= session.ExpiresAt)
        {
            _sessions.TryRemove(key, out _);
            return new AnswerResult(AnswerOutcome.Expired, session.Question);
        }

        if (!AnswerMatcher.IsMatch(answer, session.Question.Answer, session.Question.Aliases))
        {
            return new AnswerResult(AnswerOutcome.Incorrect);
        }

        if (Interlocked.CompareExchange(ref session.Completed, 1, 0) != 0)
        {
            return new AnswerResult(AnswerOutcome.AlreadyCompleted);
        }

        _sessions.TryRemove(key, out _);

        var reward = 10 + Math.Max(0, session.Question.Difficulty - 1) * 5;
        var score = await _store.AddCorrectAnswerAsync(player, reward, cancellationToken);

        return new AnswerResult(
            AnswerOutcome.Correct,
            session.Question,
            reward,
            score);
    }

    public bool Stop(string platform, string chatId)
    {
        return _sessions.TryRemove(BuildSessionKey(platform, chatId), out _);
    }

    private static string BuildSessionKey(string platform, string chatId)
        => $"{platform.Trim().ToLowerInvariant()}:{chatId.Trim()}";

    private sealed class GameSession
    {
        public GameSession(Question question, DateTimeOffset expiresAt)
        {
            Question = question;
            ExpiresAt = expiresAt;
        }

        public Question Question { get; }
        public DateTimeOffset ExpiresAt { get; }
        public int Completed;
    }
}

public static class AnswerMatcher
{
    public static bool IsMatch(
        string candidate,
        string canonicalAnswer,
        IReadOnlyList<string> aliases)
    {
        var normalizedCandidate = Normalize(candidate);
        if (normalizedCandidate.Length == 0)
        {
            return false;
        }

        if (normalizedCandidate == Normalize(canonicalAnswer))
        {
            return true;
        }

        return aliases.Any(alias => normalizedCandidate == Normalize(alias));
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLower(character, CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}
