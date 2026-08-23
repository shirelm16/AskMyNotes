namespace AskMyNotes.Domain;

/// <summary>A piece of a note, and the file it came from.</summary>
public sealed record Chunk(string Source, string Content);

/// <summary>
/// A chunk retrieved for a question, with how far its embedding sat from the question's.
/// Smaller distance is closer.
///
/// This replaced a `(string Source, string Content, double Distance)` tuple that appeared in
/// six signatures. The tuple worked; it just meant every method that touched retrieval carried
/// its shape around in its own declaration.
/// </summary>
public sealed record ScoredChunk(string Source, string Content, double Distance);
