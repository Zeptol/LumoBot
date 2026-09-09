using Lumo.Domain;

namespace Lumo.Games;

public enum QuestionPoolBucket
{
    Popular = 1,
    Mainstream = 2,
    Medium = 3,
    Obscure = 4,
    Rare = 5,
    Extreme = 6
}

public sealed record QuestionSelectionWeights(
    double Popular = 20,
    double Mainstream = 30,
    double Medium = 25,
    double Obscure = 15,
    double Rare = 8,
    double Extreme = 2)
{
    public double Get(QuestionPoolBucket bucket) => bucket switch
    {
        QuestionPoolBucket.Popular => Popular,
        QuestionPoolBucket.Mainstream => Mainstream,
        QuestionPoolBucket.Medium => Medium,
        QuestionPoolBucket.Obscure => Obscure,
        QuestionPoolBucket.Rare => Rare,
        QuestionPoolBucket.Extreme => Extreme,
        _ => 0
    };
}

public sealed record QuestionSelectionOptions(
    int CandidatePoolSize = 96,
    int RecentQuestionLimit = 40,
    int RecentEntityLimit = 12,
    QuestionSelectionWeights? Weights = null)
{
    public QuestionSelectionWeights EffectiveWeights => Weights ?? new QuestionSelectionWeights();

    public static QuestionSelectionOptions FromEnvironment()
    {
        return new QuestionSelectionOptions(
            ReadInt("LUMO_CANDIDATE_POOL", 96, 8, 256),
            ReadInt("LUMO_RECENT_QUESTIONS", 40, 0, 500),
            ReadInt("LUMO_RECENT_ENTITIES", 12, 0, 200),
            new QuestionSelectionWeights(
                ReadDouble("LUMO_WEIGHT_POPULAR", 20),
                ReadDouble("LUMO_WEIGHT_MAINSTREAM", 30),
                ReadDouble("LUMO_WEIGHT_MEDIUM", 25),
                ReadDouble("LUMO_WEIGHT_OBSCURE", 15),
                ReadDouble("LUMO_WEIGHT_RARE", 8),
                ReadDouble("LUMO_WEIGHT_EXTREME", 2)));
    }

    private static int ReadInt(string name, int fallback, int min, int max)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }

    private static double ReadDouble(string name, double fallback)
    {
        return double.TryParse(
            Environment.GetEnvironmentVariable(name),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? Math.Max(0, value)
            : fallback;
    }
}

public static class QuestionSelector
{
    public static Question Select(
        IReadOnlyList<Question> candidates,
        IReadOnlySet<long> recentQuestionIds,
        IReadOnlySet<long> recentEntityIds,
        QuestionSelectionOptions options,
        Random? random = null)
    {
        if (candidates.Count == 0)
        {
            throw new ArgumentException("At least one question candidate is required.", nameof(candidates));
        }

        random ??= Random.Shared;

        var eligible = candidates
            .Where(question =>
                !recentQuestionIds.Contains(question.Id) &&
                (question.EntityId is null || !recentEntityIds.Contains(question.EntityId.Value)))
            .ToArray();

        if (eligible.Length == 0)
        {
            eligible = candidates
                .Where(question => !recentQuestionIds.Contains(question.Id))
                .ToArray();
        }

        if (eligible.Length == 0)
        {
            eligible = candidates.ToArray();
        }

        var groups = eligible
            .GroupBy(Classify)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var weightedGroups = groups
            .Select(pair => new WeightedGroup(
                pair.Key,
                pair.Value,
                options.EffectiveWeights.Get(pair.Key)))
            .Where(group => group.Questions.Length > 0)
            .ToArray();

        var totalWeight = weightedGroups.Sum(group => group.Weight);
        WeightedGroup selectedGroup;

        if (totalWeight <= 0)
        {
            selectedGroup = weightedGroups[random.Next(weightedGroups.Length)];
        }
        else
        {
            var roll = random.NextDouble() * totalWeight;
            selectedGroup = weightedGroups[^1];
            foreach (var group in weightedGroups)
            {
                roll -= group.Weight;
                if (roll < 0)
                {
                    selectedGroup = group;
                    break;
                }
            }
        }

        return selectedGroup.Questions[random.Next(selectedGroup.Questions.Length)];
    }

    public static QuestionPoolBucket Classify(Question question)
    {
        var tags = new HashSet<string>(question.Tags, StringComparer.OrdinalIgnoreCase);

        if (HasAny(tags, "地狱", "地狱级", "extreme", "hell"))
        {
            return QuestionPoolBucket.Extreme;
        }

        if (HasAny(tags, "极冷门", "很冷门", "rare", "very rare", "very-obscure", "very obscure"))
        {
            return QuestionPoolBucket.Rare;
        }

        if (HasAny(tags, "冷门", "obscure"))
        {
            return QuestionPoolBucket.Obscure;
        }

        if (HasAny(tags, "中等", "medium"))
        {
            return QuestionPoolBucket.Medium;
        }

        if (HasAny(tags, "普通", "normal", "mainstream"))
        {
            return QuestionPoolBucket.Mainstream;
        }

        if (HasAny(tags, "热门", "超级热门", "经典", "popular", "classic"))
        {
            return QuestionPoolBucket.Popular;
        }

        return question.Difficulty switch
        {
            <= 1 => QuestionPoolBucket.Popular,
            2 => QuestionPoolBucket.Mainstream,
            3 => QuestionPoolBucket.Medium,
            4 => QuestionPoolBucket.Obscure,
            5 or 6 => QuestionPoolBucket.Rare,
            _ => QuestionPoolBucket.Extreme
        };
    }

    private static bool HasAny(IReadOnlySet<string> tags, params string[] values)
        => values.Any(tags.Contains);

    private sealed record WeightedGroup(
        QuestionPoolBucket Bucket,
        Question[] Questions,
        double Weight);
}
