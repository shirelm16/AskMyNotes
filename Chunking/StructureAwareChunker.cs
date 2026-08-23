using System.Text;
using System.Text.RegularExpressions;

namespace AskMyNotes.Chunking;

/// <summary>
/// Splits a note where a new idea begins — a heading, a list item, a bold lead-in or a blank
/// line — instead of every N characters. Fixed-size splitting cuts sentences and ideas in half;
/// this keeps a thought whole, which is what a search has to match against.
///
/// Units are then packed to between <see cref="MinSize"/> and <see cref="MaxSize"/> characters:
/// the maximum is a ceiling rather than a target, and a chunk is emitted as soon as it passes
/// the minimum so that it can stand on its own. Anything oversized is split on sentence
/// boundaries rather than mid-word. Code fences are tracked so a chunk never cuts inside one.
/// </summary>
public sealed class StructureAwareChunker(int minSize = 150, int maxSize = 1000) : IChunker
{
    public int MinSize { get; } = minSize;
    public int MaxSize { get; } = maxSize;

    public IReadOnlyList<string> Split(string text) => PackUnits(SplitIntoUnits(text), MinSize, MaxSize);

    private static List<string> SplitIntoUnits(string text)
    {
        var units = new List<string>();
        var current = new StringBuilder();
        var inFence = false;

        void Flush()
        {
            var s = current.ToString().Trim();
            if (s.Length > 0) units.Add(s);
            current.Clear();
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.TrimStart().StartsWith("```"))   // code fence — never split inside one
            {
                inFence = !inFence;
                current.AppendLine(line);
                continue;
            }
            if (inFence) { current.AppendLine(line); continue; }

            if (line.Trim().Length == 0) { Flush(); continue; }
            if (IsUnitStart(line)) Flush();

            current.AppendLine(line);
        }

        Flush();
        return units;
    }

    // where a new idea begins
    private static bool IsUnitStart(string line) =>
           Regex.IsMatch(line, @"^#{1,6}\s")           // ## Heading
        || Regex.IsMatch(line, @"^(\d+\.|[-*+])\s")    // "1. " or "- ", column 0 only
        || Regex.IsMatch(line, @"^\*\*.+:\*\*\s*$");   // **Bold lead-in:**

    private static List<string> PackUnits(List<string> units, int minSize, int maxSize)
    {
        var chunks = new List<string>();
        var sb = new StringBuilder();

        foreach (var unit in units)
        {
            var pieces = unit.Length > maxSize
                ? SplitOversized(unit, maxSize)
                : new List<string> { unit };

            foreach (var piece in pieces)
            {
                if (sb.Length > 0 && sb.Length + 2 + piece.Length > maxSize) // check if appenfing curret piece will exceed max size. 2 -> \n\n
                {
                    chunks.Add(sb.ToString());
                    sb.Clear();
                }
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(piece);

                if (sb.Length >= minSize)   // big enough to stand on its own
                {
                    chunks.Add(sb.ToString());
                    sb.Clear();
                }
            }
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }

    private static List<string> SplitOversized(string unit, int maxSize)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();

        foreach (var sentence in Regex.Split(unit, @"(?<=[.!?])\s+")) //sentence is a text ending with .|!|? - splitting by .|!|? and a white space
        {
            if (sb.Length > 0 && sb.Length + 1 + sentence.Length > maxSize)// 1 -> ' '
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(sentence);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
    }
}
