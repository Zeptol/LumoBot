using Lumo.Domain;

namespace Lumo.Application;

public interface IQuestionCandidateSource
{
    Task<IReadOnlyList<Question>> GetCandidatesAsync(
        GameMode gameMode,
        int limit,
        CancellationToken cancellationToken = default);
}
