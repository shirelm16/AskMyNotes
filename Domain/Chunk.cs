namespace AskMyNotes.Domain;

/// <summary>A piece of a note, and the file it came from.</summary>
public sealed record Chunk(string Source, string Content);

/// <summary>
/// A chunk retrieved for a question, with how far its embedding sat from the question's.
/// Smaller distance is closer.
/// </summary>
public sealed record ScoredChunk(string Source, string Content, double Distance);
