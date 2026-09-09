using Lumo.Domain;

namespace Lumo.Application;

public interface ILumoStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<Question?> GetRandomQuestionAsync(
        GameMode gameMode,
        CancellationToken cancellationToken = default);

    Task<long> UpsertQuestionAsync(
        CatalogQuestionInput input,
        CancellationToken cancellationToken = default);

    Task<long> GetQuestionCountAsync(
        GameMode? gameMode = null,
        CancellationToken cancellationToken = default);

    Task<Score> AddCorrectAnswerAsync(
        ChatIdentity player,
        int rewardCoins,
        CancellationToken cancellationToken = default);

    Task<Score?> GetScoreAsync(
        ChatIdentity player,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Score>> GetLeaderboardAsync(
        string platform,
        string chatId,
        int limit = 10,
        CancellationToken cancellationToken = default);
}

public enum AnswerOutcome
{
    NoActiveGame = 0,
    Incorrect = 1,
    Correct = 2,
    Expired = 3,
    AlreadyCompleted = 4
}

public sealed record GameStartResult(
    Question Question,
    DateTimeOffset ExpiresAt);

public sealed record AnswerResult(
    AnswerOutcome Outcome,
    Question? Question = null,
    int RewardCoins = 0,
    Score? UpdatedScore = null);
