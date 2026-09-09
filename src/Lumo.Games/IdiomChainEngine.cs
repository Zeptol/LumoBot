using System.Collections.Concurrent;
using System.Text;
using Lumo.Application;
using Lumo.Domain;

namespace Lumo.Games;

public enum IdiomChainOutcome
{
    NoActiveGame = 0,
    InvalidIdiom = 1,
    WrongStart = 2,
    Duplicate = 3,
    Continued = 4,
    UserWon = 5,
    Expired = 6
}

public sealed record IdiomChainStartResult(string BotIdiom, string ExpectedStart);

public sealed record IdiomChainResult(
    IdiomChainOutcome Outcome,
    string? CurrentIdiom = null,
    string? ExpectedStart = null,
    string? BotIdiom = null,
    int RewardCoins = 0,
    Score? UpdatedScore = null);

public sealed class IdiomChainEngine
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private const int ContinueReward = 3;
    private const int WinBonus = 15;

    private readonly ILumoStore _store;
    private readonly IIdiomSource _idioms;
    private readonly ConcurrentDictionary<string, ChainSession> _sessions = new(StringComparer.Ordinal);

    public IdiomChainEngine(ILumoStore store, IIdiomSource idioms)
    {
        _store = store;
        _idioms = idioms;
    }

    public async Task<IdiomChainStartResult?> StartAsync(
        string platform,
        string chatId,
        CancellationToken cancellationToken = default)
    {
        var first = await _idioms.GetRandomIdiomAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(first))
        {
            return null;
        }

        var session = new ChainSession(first);
        _sessions[BuildKey(platform, chatId)] = session;
        return new IdiomChainStartResult(first, LastTextElement(first));
    }

    public bool IsActive(string platform, string chatId)
        => _sessions.ContainsKey(BuildKey(platform, chatId));

    public bool Stop(string platform, string chatId)
        => _sessions.TryRemove(BuildKey(platform, chatId), out _);

    public async Task<IdiomChainResult> SubmitAsync(
        ChatIdentity player,
        string idiom,
        CancellationToken cancellationToken = default)
    {
        var key = BuildKey(player.Platform, player.ChatId);
        if (!_sessions.TryGetValue(key, out var session))
        {
            return new IdiomChainResult(IdiomChainOutcome.NoActiveGame);
        }

        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow - session.LastActivityAt >= IdleTimeout)
            {
                _sessions.TryRemove(key, out _);
                return new IdiomChainResult(IdiomChainOutcome.Expired, session.CurrentIdiom);
            }

            var candidate = idiom.Trim();
            if (!LooksLikeFourCharacterIdiom(candidate) ||
                !await _idioms.ExistsAsync(candidate, cancellationToken))
            {
                return new IdiomChainResult(
                    IdiomChainOutcome.InvalidIdiom,
                    session.CurrentIdiom,
                    LastTextElement(session.CurrentIdiom));
            }

            var expectedStart = LastTextElement(session.CurrentIdiom);
            if (!string.Equals(FirstTextElement(candidate), expectedStart, StringComparison.Ordinal))
            {
                return new IdiomChainResult(
                    IdiomChainOutcome.WrongStart,
                    session.CurrentIdiom,
                    expectedStart);
            }

            if (!session.Used.Add(candidate))
            {
                return new IdiomChainResult(
                    IdiomChainOutcome.Duplicate,
                    session.CurrentIdiom,
                    expectedStart);
            }

            session.LastActivityAt = DateTimeOffset.UtcNow;
            var nextStart = LastTextElement(candidate);
            var nextCandidates = await _idioms.GetStartingWithAsync(nextStart, 64, cancellationToken);
            var botIdiom = nextCandidates.FirstOrDefault(next => session.Used.Add(next));

            var reward = botIdiom is null ? ContinueReward + WinBonus : ContinueReward;
            var score = await _store.AddCorrectAnswerAsync(player, reward, cancellationToken);

            if (botIdiom is null)
            {
                _sessions.TryRemove(key, out _);
                return new IdiomChainResult(
                    IdiomChainOutcome.UserWon,
                    candidate,
                    nextStart,
                    null,
                    reward,
                    score);
            }

            session.CurrentIdiom = botIdiom;
            session.LastActivityAt = DateTimeOffset.UtcNow;

            return new IdiomChainResult(
                IdiomChainOutcome.Continued,
                candidate,
                LastTextElement(botIdiom),
                botIdiom,
                reward,
                score);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public static bool LooksLikeFourCharacterIdiom(string value)
        => value.Trim().EnumerateRunes().Count() == 4 &&
           value.Trim().EnumerateRunes().All(IsCjkRune);

    private static bool IsCjkRune(Rune rune)
    {
        var value = rune.Value;
        return value is >= 0x3400 and <= 0x4DBF or
               >= 0x4E00 and <= 0x9FFF or
               >= 0xF900 and <= 0xFAFF or
               >= 0x20000 and <= 0x2FA1F;
    }

    private static string FirstTextElement(string value)
        => value.EnumerateRunes().First().ToString();

    private static string LastTextElement(string value)
        => value.EnumerateRunes().Last().ToString();

    private static string BuildKey(string platform, string chatId)
        => $"{platform.Trim().ToLowerInvariant()}:{chatId.Trim()}";

    private sealed class ChainSession
    {
        public ChainSession(string firstIdiom)
        {
            CurrentIdiom = firstIdiom;
            Used.Add(firstIdiom);
        }

        public SemaphoreSlim Gate { get; } = new(1, 1);
        public HashSet<string> Used { get; } = new(StringComparer.Ordinal);
        public string CurrentIdiom { get; set; }
        public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
