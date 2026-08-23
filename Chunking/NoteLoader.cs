using System.Text.RegularExpressions;
using AskMyNotes.Domain;

namespace AskMyNotes.Chunking;

/// <summary>
/// Reads every markdown file under the configured folders and turns each one into chunks.
///
/// Kept separate from <see cref="IChunker"/> because walking a folder and deciding where a note
/// should be cut are different jobs: this one deals with files, the chunker deals with text and
/// can be tested without touching a disk.
/// </summary>
public sealed partial class NoteLoader(IChunker chunker)
{
    /// <summary>
    /// The metadata header some notes carry at the top. It is filler rather than content, and
    /// leaving it in means embedding field names and an id as though a reader had written them.
    /// </summary>
    [GeneratedRegex(@"\A---\r?\n.*?\r?\n---(\r?\n|\z)", RegexOptions.Singleline)]
    private static partial Regex MetadataHeader();

    public async Task<List<Chunk>> LoadAsync(IEnumerable<string> roots, CancellationToken ct = default)
    {
        var chunks = new List<Chunk>();

        foreach (var root in roots)
        {
            // The eval folder is skipped: it holds the questions and the run log, and indexing
            // the questions would let the system retrieve its own answer key.
            var files = Directory.GetFiles(root, "*.md", SearchOption.AllDirectories)
                .Where(f => !f.Replace(Path.DirectorySeparatorChar, '/').Contains("/eval/"));

            foreach (var file in files)
            {
                var text = MetadataHeader().Replace(await File.ReadAllTextAsync(file, ct), "");

                foreach (var content in chunker.Split(text))
                    chunks.Add(new Chunk(file, content));
            }
        }

        return chunks;
    }
}
