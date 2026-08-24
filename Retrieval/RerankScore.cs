namespace AskMyNotes.Retrieval;

/// <summary>
/// The shape the model is required to return, enforced by a strict JSON schema so the reply is
/// deserialised directly rather than parsed out of prose.
/// </summary>
public sealed record RerankScore(int Id, int Score);

public sealed record RerankResult(List<RerankScore> Scores);
