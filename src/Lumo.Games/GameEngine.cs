using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Lumo.Application;
using Lumo.Domain;

namespace Lumo.Games;

public sealed class GameEngine
{
    private static readonly TimeSpan DefaultRoundDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HistoryIdleLifetime = TimeSpan.FromHours(24);

    private readonly ILumoStore _store;
    private readonly IQuestionCandidateSource? _questionSource;
    private readonly QuestionSelectionOptions _selectionOptions;
    private readonly ConcurrentDictionary<string, GameSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RecentSelectionHistory> _histories = new(StringComparer.Ordinal);
    private long _startCount;

    public GameEngine(
        ILumoStore store,
        IQuestionCandidateSource? questionSource = null,
        QuestionSelectionOptions? selectionOptions = null)
    {
        _store = store;
        _questionSource = questionSource;
        _selectionOptions = selectionOptions ?? new QuestionSelectionOptions();
    }

    public async Task<GameStartResult?> StartAsync(
        string platform,
        string chatId,
        GameMode gameMode,
        CancellationToken cancellationToken = default)
    {
        Question? question;
        var key = BuildSessionKey(platform, chatId);

        if (_questionSource is null)
        {
            question = await _store.GetRandomQuestionAsync(gameMode, cancellationToken);
        }
        else
        {
            var candidates = await _questionSource.GetCandidatesAsync(
                gameMode,
                _selectionOptions.CandidatePoolSize,
                cancellationToken);
            if (candidates.Count == 0)
            {
                return null;
            }

            var history = _histories.GetOrAdd(key, static _ => new RecentSelectionHistory());
            question = history.SelectAndRemember(candidates, _selectionOptions);
            PruneHistoriesPeriodically();
        }

        if (question is null)
        {
            return null;
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(DefaultRoundDuration);
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

    private void PruneHistoriesPeriodically()
    {
        var startCount = Interlocked.Increment(ref _startCount);
        if (startCount % 128 != 0 || _histories.Count < 128)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - HistoryIdleLifetime;
        foreach (var pair in _histories)
        {
            if (pair.Value.LastUsedAt < cutoff)
            {
                _histories.TryRemove(pair.Key, out _);
            }
        }
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

    private sealed class RecentSelectionHistory
    {
        private readonly object _gate = new();
        private readonly BoundedRecentSet<long> _questions = new();
        private readonly BoundedRecentSet<long> _entities = new();
        private DateTimeOffset _lastUsedAt = DateTimeOffset.UtcNow;

        public DateTimeOffset LastUsedAt
        {
            get
            {
                lock (_gate)
                {
                    return _lastUsedAt;
                }
            }
        }

        public Question SelectAndRemember(
            IReadOnlyList<Question> candidates,
            QuestionSelectionOptions options)
        {
            lock (_gate)
            {
                var selected = QuestionSelector.Select(
                    candidates,
                    _questions.Snapshot(),
                    _entities.Snapshot(),
                    options);

                _questions.Add(selected.Id, options.RecentQuestionLimit);
                if (selected.EntityId is { } entityId)
                {
                    _entities.Add(entityId, options.RecentEntityLimit);
                }

                _lastUsedAt = DateTimeOffset.UtcNow;
                return selected;
            }
        }
    }

    private sealed class BoundedRecentSet<T> where T : notnull
    {
        private readonly Queue<T> _queue = new();
        private readonly Dictionary<T, int> _counts = new();

        public IReadOnlySet<T> Snapshot() => new HashSet<T>(_counts.Keys);

        public void Add(T value, int limit)
        {
            if (limit <= 0)
            {
                _queue.Clear();
                _counts.Clear();
                return;
            }

            _queue.Enqueue(value);
            _counts[value] = _counts.GetValueOrDefault(value) + 1;

            while (_queue.Count > limit)
            {
                var expired = _queue.Dequeue();
                var remaining = _counts[expired] - 1;
                if (remaining <= 0)
                {
                    _counts.Remove(expired);
                }
                else
                {
                    _counts[expired] = remaining;
                }
            }
        }
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
