using AskMyNotes.Domain;

namespace AskMyNotes.Retrieval;

/// <summary>
/// Orders candidate chunks by how well they *answer* a question, rather than by how similar
/// they look to it.
///
/// An interface because this stage is the expensive one — 48% of a request — and the obvious
/// next experiment is replacing a general-purpose language model with a model built for
/// scoring passages, which is cheaper per passage.
/// </summary>
public interface IReranker
{
    Task<List<ScoredChunk>> RerankAsync(string question, List<ScoredChunk> candidates,
                                        CancellationToken ct = default);
}
