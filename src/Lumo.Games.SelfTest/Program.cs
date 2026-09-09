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

Console.WriteLine("Question selection self-test passed.");
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
        throw new InvalidOperationException($"Question selection self-test failed: {name}");
    }
}
