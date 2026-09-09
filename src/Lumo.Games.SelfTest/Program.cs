using Lumo.Application;
using Lumo.Domain;
using Lumo.Games;

var popular = MakeQuestion(1, 100, 1, "热门");
var mainstream = MakeQuestion(2, 200, 2, "普通");
var medium = MakeQuestion(3, 300, 3);
var obscure = MakeQuestion(4, 400, 4, "冷门");
var rare = MakeQuestion(5, 500, 5, "极冷门");
var extreme = MakeQuestion(6, 600, 8, "地狱级");

Assert(QuestionSelector.Classify(popular) == QuestionPoolBucket.Popular, "popular classification");
Assert(QuestionSelector.Classify(mainstream) == QuestionPoolBucket.Mainstream, "mainstream classification");
Assert(QuestionSelector.Classify(medium) == QuestionPoolBucket.Medium, "medium classification");
Assert(QuestionSelector.Classify(obscure) == QuestionPoolBucket.Obscure, "obscure classification");
Assert(QuestionSelector.Classify(rare) == QuestionPoolBucket.Rare, "rare classification");
Assert(QuestionSelector.Classify(extreme) == QuestionPoolBucket.Extreme, "extreme classification");

var popularOnly = new QuestionSelectionOptions(
    Weights: new QuestionSelectionWeights(100, 0, 0, 0, 0, 0));
var selectedPopular = QuestionSelector.Select(
    [popular, mainstream],
    new HashSet<long>(),
    new HashSet<long>(),
    popularOnly,
    new Random(7));
Assert(selectedPopular.Id == popular.Id, "weighted popular selection");

var selectedAfterRecentQuestion = QuestionSelector.Select(
    [popular, mainstream],
    new HashSet<long> { popular.Id },
    new HashSet<long>(),
    popularOnly,
    new Random(7));
Assert(selectedAfterRecentQuestion.Id == mainstream.Id, "recent question exclusion");

var sameEntityOtherQuestion = MakeQuestion(7, 100, 1, "热门");
var selectedAfterRecentEntity = QuestionSelector.Select(
    [popular, sameEntityOtherQuestion, mainstream],
    new HashSet<long>(),
    new HashSet<long> { 100 },
    popularOnly,
    new Random(7));
Assert(selectedAfterRecentEntity.EntityId != 100, "recent entity exclusion");

var defaultWeights = new QuestionSelectionWeights();
Assert(Math.Abs(
    defaultWeights.Popular + defaultWeights.Mainstream + defaultWeights.Medium +
    defaultWeights.Obscure + defaultWeights.Rare + defaultWeights.Extreme - 100) < 0.001,
    "default weights sum to 100");

Assert(IdiomChainEngine.LooksLikeFourCharacterIdiom("画蛇添足"), "four-character idiom shape");
Assert(!IdiomChainEngine.LooksLikeFourCharacterIdiom("hello"), "reject non-Chinese chain input");

var fakeStore = new FakeStore();
var chain = new IdiomChainEngine(
    fakeStore,
    new FakeIdiomSource(["一心一意", "意气风发", "发扬光大", "大功告成"]));
var chainStart = await chain.StartAsync("test", "room");
Assert(chainStart?.BotIdiom == "一心一意" && chainStart.ExpectedStart == "意", "idiom chain start");

var player = new ChatIdentity("test", "room", "user", "Tester");
var continued = await chain.SubmitAsync(player, "意气风发");
Assert(
    continued.Outcome == IdiomChainOutcome.Continued &&
    continued.BotIdiom == "发扬光大" &&
    continued.ExpectedStart == "大" &&
    continued.RewardCoins == 3,
    "idiom chain continuation");

var won = await chain.SubmitAsync(player, "大功告成");
Assert(
    won.Outcome == IdiomChainOutcome.UserWon &&
    won.ExpectedStart == "成" &&
    won.RewardCoins == 18 &&
    won.UpdatedScore?.Coins == 21,
    "idiom chain user win");

Console.WriteLine("Question selection and idiom-chain self-test passed.");
return 0;

static Question MakeQuestion(long id, long entityId, int difficulty, params string[] tags)
    => new(
        id,
        entityId,
        GameMode.GuessImage,
        "Test",
        $"Entity {entityId}",
        "prompt",
        $"answer-{id}",
        difficulty,
        MediaKind.None,
        null,
        null,
        null,
        [],
        tags);

static void Assert(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Self-test failed: {name}");
    }
}

file sealed class FakeIdiomSource(IReadOnlyList<string> idioms) : IIdiomSource
{
    public Task<string?> GetRandomIdiomAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(idioms.FirstOrDefault());

    public Task<bool> ExistsAsync(string idiom, CancellationToken cancellationToken = default)
        => Task.FromResult(idioms.Contains(idiom, StringComparer.Ordinal));

    public Task<IReadOnlyList<string>> GetStartingWithAsync(
        string firstCharacter,
        int limit = 32,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(
            idioms.Where(value => value.StartsWith(firstCharacter, StringComparison.Ordinal)).Take(limit).ToArray());
}

file sealed class FakeStore : ILumoStore
{
    private long _coins;
    private long _correct;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<Question?> GetRandomQuestionAsync(GameMode gameMode, CancellationToken cancellationToken = default)
        => Task.FromResult<Question?>(null);

    public Task<long> UpsertQuestionAsync(CatalogQuestionInput input, CancellationToken cancellationToken = default)
        => Task.FromResult(0L);

    public Task<long> GetQuestionCountAsync(GameMode? gameMode = null, CancellationToken cancellationToken = default)
        => Task.FromResult(0L);

    public Task<Score> AddCorrectAnswerAsync(
        ChatIdentity player,
        int rewardCoins,
        CancellationToken cancellationToken = default)
    {
        _coins += rewardCoins;
        _correct++;
        return Task.FromResult(new Score(
            player.Platform,
            player.ChatId,
            player.UserId,
            player.DisplayName,
            _coins,
            _correct,
            DateTimeOffset.UtcNow));
    }

    public Task<Score?> GetScoreAsync(ChatIdentity player, CancellationToken cancellationToken = default)
        => Task.FromResult<Score?>(new Score(
            player.Platform,
            player.ChatId,
            player.UserId,
            player.DisplayName,
            _coins,
            _correct,
            DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<Score>> GetLeaderboardAsync(
        string platform,
        string chatId,
        int limit = 10,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Score>>([]);
}
