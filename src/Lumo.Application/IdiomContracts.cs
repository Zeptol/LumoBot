namespace Lumo.Application;

public interface IIdiomSource
{
    Task<string?> GetRandomIdiomAsync(CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        string idiom,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetStartingWithAsync(
        string firstCharacter,
        int limit = 32,
        CancellationToken cancellationToken = default);
}
