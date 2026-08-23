namespace AskMyNotes.Chunking;

/// <summary>
/// Splits the text of one note into the pieces that get embedded and searched.
///
/// This is an interface because the choice of chunker is the single biggest lever on
/// retrieval quality, and the way to find that out is to swap one for another and re-measure.
/// Moving from fixed-size splitting to structure-aware splitting took hit-rate@5 from 10 of 15
/// questions to 13 — the largest single improvement in the whole tuning log. A seam here is
/// what makes that an experiment rather than a rewrite.
/// </summary>
public interface IChunker
{
    IReadOnlyList<string> Split(string text);
}
