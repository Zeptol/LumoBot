namespace Lumo.Domain;

public enum GameMode
{
    GuessSong = 1,
    GuessMovie = 2,
    GuessImage = 3,
    GuessIdiom = 4
}

public enum MediaKind
{
    None = 0,
    Image = 1,
    Audio = 2
}

public sealed record Question(
    long Id,
    long? EntityId,
    GameMode GameMode,
    string EntityType,
    string EntityName,
    string Prompt,
    string Answer,
    int Difficulty,
    MediaKind MediaKind,
    string? MediaUrl,
    string? SourceUrl,
    string? License,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Tags);

public sealed record ChatIdentity(
    string Platform,
    string ChatId,
    string UserId,
    string DisplayName);

public sealed record Score(
    string Platform,
    string ChatId,
    string UserId,
    string DisplayName,
    long Coins,
    long CorrectAnswers,
    DateTimeOffset UpdatedAt);
