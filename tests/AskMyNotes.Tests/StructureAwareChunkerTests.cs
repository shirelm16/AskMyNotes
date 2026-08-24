using AskMyNotes.Chunking;
using Xunit;

namespace AskMyNotes.Tests;

public class StructureAwareChunkerTests
{
    private static readonly StructureAwareChunker Chunker = new(minSize: 150, maxSize: 1000);

    /// <summary>Long enough that a unit is emitted on its own rather than packed with the next.</summary>
    private static string Filler(string label) =>
        $"{label} {new string('x', 200)}";

    [Fact]
    public void A_heading_starts_a_new_chunk()
    {
        var chunks = Chunker.Split($"""
            ## First topic
            {Filler("about the first")}

            ## Second topic
            {Filler("about the second")}
            """);

        Assert.Contains(chunks, c => c.Contains("First topic") && !c.Contains("Second topic"));
        Assert.Contains(chunks, c => c.Contains("Second topic") && !c.Contains("First topic"));
    }

    [Fact]
    public void A_list_item_starts_a_new_chunk()
    {
        var chunks = Chunker.Split($"""
            - {Filler("first item")}
            - {Filler("second item")}
            """);

        Assert.Contains(chunks, c => c.Contains("first item") && !c.Contains("second item"));
        Assert.Contains(chunks, c => c.Contains("second item") && !c.Contains("first item"));
    }

    [Fact]
    public void A_bold_lead_in_starts_a_new_chunk()
    {
        // "**Something:**" on its own line is how a lot of notes introduce a topic, and it is
        // the third thing treated as the start of an idea.
        var chunks = Chunker.Split($"""
            **First subject:**
            {Filler("first body")}

            **Second subject:**
            {Filler("second body")}
            """);

        Assert.Contains(chunks, c => c.Contains("First subject") && !c.Contains("Second subject"));
        Assert.Contains(chunks, c => c.Contains("Second subject") && !c.Contains("First subject"));
    }

    [Fact]
    public void A_code_fence_is_never_cut_in_half()
    {
        // Inside a fence, a "#" line or a "- " line is code, not a heading or a list item.
        // Splitting there would leave both halves meaningless.
        var chunks = Chunker.Split($"""
            ## Example
            {Filler("some prose")}

            ```bash
            # this is a comment, not a heading
            - this is a flag, not a list item

            echo "and this blank line above is not a boundary"
            ```
            """);

        // A fence has an opening and a closing marker, so any chunk holding one must contain an
        // even number of markers. An odd count means a chunk ends mid-fence.
        static int FenceMarkers(string chunk) => chunk.Split("```").Length - 1; // Split on N markers gives N+1 parts

        var withFence = chunks.Where(c => FenceMarkers(c) > 0).ToList();
        Assert.NotEmpty(withFence);
        Assert.All(withFence, c => Assert.True(FenceMarkers(c) % 2 == 0,
            $"a chunk holds {FenceMarkers(c)} fence markers, so a code block was cut in half:\n{c}"));

        // And the fence's contents travelled together: a comment line and a command line
        // with a blank line between them, which would otherwise have been a boundary.
        Assert.Contains(withFence, c => c.Contains("# this is a comment") && c.Contains("echo"));
    }

    [Fact]
    public void No_chunk_exceeds_the_maximum()
    {
        var chunks = Chunker.Split(string.Join("\n\n", Enumerable.Range(1, 40).Select(i => Filler($"paragraph {i}")))); //chunk length exceeds 1000 characters

        Assert.All(chunks, c => Assert.True(c.Length <= 1000, $"a chunk was {c.Length} characters"));
    }

    [Fact]
    public void An_oversized_paragraph_is_split_between_sentences_not_mid_word()
    {
        var sentence = "This sentence is here to take up a reasonable amount of room in the paragraph. ";
        var chunks = Chunker.Split(string.Concat(Enumerable.Repeat(sentence, 30)));

        Assert.True(chunks.Count > 1);
        // Every piece should begin at a sentence start, never in the middle of a word.
        Assert.All(chunks, c => Assert.StartsWith("This sentence", c));
    }

    [Fact]
    public void Short_neighbouring_units_are_packed_together_rather_than_left_alone()
    {
        // A chunk of five words retrieves badly — too little context to match against. Units
        // below the minimum are joined until they are worth embedding.
        var chunks = Chunker.Split("""
            - one
            - two
            - three
            - four
            - five
            """);

        Assert.Single(chunks);
        Assert.Contains("one", chunks[0]);
        Assert.Contains("five", chunks[0]);
    }

    [Fact]
    public void Nothing_is_lost_between_the_units()
    {
        // Whatever the boundaries, every word of the note has to survive into some chunk.
        const string note = """
            ## A heading
            The first paragraph says something.

            - a list item
            - another list item

            **A lead-in:**
            The last paragraph says something else.
            """;

        var joined = string.Join(" ", Chunker.Split(note));

        foreach (var word in new[] { "heading", "first", "paragraph", "list", "item", "lead-in", "last", "else" })
            Assert.Contains(word, joined);
    }

    [Fact]
    public void Empty_or_blank_input_produces_no_chunks()
    {
        Assert.Empty(Chunker.Split(""));
        Assert.Empty(Chunker.Split("   \n\n   \n"));
    }
}
