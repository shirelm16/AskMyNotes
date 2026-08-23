using System.Text.Json.Serialization;

namespace AskMyNotes.Evaluation;

/// <summary>
/// One labelled question: what to ask, and which source files would count as a correct answer.
///
/// <see cref="ExpectedSources"/> is a list rather than a single file on purpose — several notes
/// can genuinely answer the same question, and pretending there is exactly one right document
/// costs accuracy in the score.
/// </summary>
public sealed class GoldenSetQuestion
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("question")]
    public string Question { get; set; } = "";

    [JsonPropertyName("expected_sources")]
    public string[] ExpectedSources { get; set; } = [];
}
