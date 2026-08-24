using AskMyNotes.Chunking;

namespace AskMyNotes.Tests;

/// <summary>
/// Reading notes off disk, and stripping the metadata header before anything is embedded.
///
/// The header matters more than it looks. Leaving it in meant field names and a generated id
/// were stored and searched as though a person had written them, which is what one of the
/// longest debugging runs in this project was eventually traced back to.
/// </summary>
public class NoteLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "asknotes-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NoteLoader _loader = new(new StructureAwareChunker());

    public NoteLoaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private const string Body =
        "This is the body of the note, long enough to become a chunk on its own rather than "
      + "being packed together with whatever happens to follow it in the file. " + "Padding. ";

    [Fact]
    public async Task Markdown_is_read_from_nested_folders()
    {
        Write("top.md", Body);
        Write(Path.Combine("nested", "deeper", "buried.md"), Body);

        var chunks = await _loader.LoadAsync([_root]);

        Assert.Contains(chunks, c => c.Source.EndsWith("top.md"));
        Assert.Contains(chunks, c => c.Source.EndsWith("buried.md"));
    }

    [Fact]
    public async Task Files_that_are_not_markdown_are_ignored()
    {
        Write("note.md", Body);
        Write("notes.txt", Body);
        Write("data.json", "{}");

        var chunks = await _loader.LoadAsync([_root]);

        Assert.All(chunks, c => Assert.EndsWith(".md", c.Source));
    }

    [Fact]
    public async Task The_metadata_header_is_stripped_before_chunking()
    {
        Write("with-header.md", $"""
            ---
            name: some-note
            description: a description nobody wrote as prose
            originSessionId: 5f2c1e88-0000-0000-0000-000000000000
            ---

            {Body}
            """);

        var chunks = await _loader.LoadAsync([_root]);
        var text = string.Join(" ", chunks.Select(c => c.Content));

        Assert.DoesNotContain("originSessionId", text);
        Assert.DoesNotContain("description:", text);
        Assert.Contains("body of the note", text);
    }

    [Fact]
    public async Task A_horizontal_rule_inside_the_note_is_not_mistaken_for_a_header()
    {
        // The header pattern is anchored to the very start of the file. Markdown uses --- as a
        // horizontal rule too, and cutting at the first one further down would delete real text.
        Write("with-rule.md", $"""
            {Body}

            ---

            More text after a horizontal rule, which must survive.
            """);

        var text = string.Join(" ", (await _loader.LoadAsync([_root])).Select(c => c.Content));

        Assert.Contains("body of the note", text);
        Assert.Contains("must survive", text);
    }

    [Fact]
    public async Task Every_chunk_records_the_file_it_came_from()
    {
        // The answer cites sources, and the eval scores on them, so a chunk without a correct
        // source is worse than a missing chunk.
        Write("named.md", Body);

        var chunks = await _loader.LoadAsync([_root]);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.True(File.Exists(c.Source), $"source '{c.Source}' is not a real file"));
    }

    [Fact]
    public async Task Several_roots_are_all_read()
    {
        var second = Path.Combine(Path.GetTempPath(), "asknotes-tests-second-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(second);
        try
        {
            Write("first.md", Body);
            File.WriteAllText(Path.Combine(second, "second.md"), Body);

            var chunks = await _loader.LoadAsync([_root, second]);

            Assert.Contains(chunks, c => c.Source.EndsWith("first.md"));
            Assert.Contains(chunks, c => c.Source.EndsWith("second.md"));
        }
        finally { Directory.Delete(second, recursive: true); }
    }

    [Fact]
    public async Task An_empty_folder_produces_no_chunks_rather_than_failing()
    {
        Assert.Empty(await _loader.LoadAsync([_root]));
    }
}
