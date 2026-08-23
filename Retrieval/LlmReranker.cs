using System.Text;
using System.Text.Json;
using AskMyNotes.Domain;
using OpenAI.Chat;

namespace AskMyNotes.Retrieval;

/// <summary>
/// Scores each candidate passage 0-10 for how well it answers the question, using a language
/// model, then orders by that score.
///
/// The prompt's job is one specific distinction: embedding similarity is *topical*, so it
/// happily returns a passage that discusses the subject without containing the answer. The
/// prompt has to say that a passage about the right subject which does not answer the question
/// scores low, because that is the whole reason this stage exists.
///
/// Passages are scored in batches of 18, four batches at a time. Scoring them one call at a
/// time was almost the whole of a 30.6-second request; batching brought it to 8.2 seconds.
/// </summary>
public sealed class LlmReranker(ChatClient chat) : IReranker
{

    private const int BatchSize = 18;
    private const int MaxParallel = 4;

    static readonly JsonSerializerOptions RerankJson = new() { PropertyNameCaseInsensitive = true };

    static readonly ChatCompletionOptions RerankOptions = new()
    {
        Temperature = 0,
        ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
        jsonSchemaFormatName: "rerank",
        jsonSchema: BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "scores": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id":    { "type": "integer" },
                  "score": { "type": "integer" }
                },
                "required": ["id", "score"],
                "additionalProperties": false
              }
            }
          },
          "required": ["scores"],
          "additionalProperties": false
        }
        """),
        jsonSchemaIsStrict: true)
    };

    public async Task<List<ScoredChunk>> RerankAsync(string question, List<ScoredChunk> candidates,
                                                      CancellationToken ct = default)
    {
        var batches = new List<List<ScoredChunk>>();
        for (int i = 0; i < candidates.Count; i += BatchSize)
            batches.Add(candidates[i..Math.Min(i + BatchSize, candidates.Count)]);

        using var gate = new SemaphoreSlim(MaxParallel);

        var tasks = batches.Select(async batch =>
        {
            await gate.WaitAsync(ct);
            try { return await ScoreBatchAsync(question, batch, ct); }
            finally { gate.Release(); }
        });

        return (await Task.WhenAll(tasks))
            .SelectMany(x => x)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Chunk.Distance)
            .Select(x => x.Chunk)
            .ToList();
    }

    private async Task<List<(ScoredChunk Chunk, int Score)>> ScoreBatchAsync(
        string question, List<ScoredChunk> batch, CancellationToken ct)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < batch.Count; i++)
        {
            var text = batch[i].Content;
            if (text.Length > 1200) text = text[..1200];
            sb.AppendLine($"[{i}] {text.Replace('\n', ' ')}");
            sb.AppendLine();
        }

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                "You score passages for how well they ANSWER a question.\n" +
                "Each score should be from 0 to 10 (whole numbers only)." +
                "10 = directly answers it. 5 = related and partly useful. " +
                "0 = same topic but does not answer it, or states the opposite.\n" +
                "A passage about the right subject that does not contain the answer scores low.\n" +
                "Score every passage independently. Return a score for every id."),
            new UserChatMessage($"Question: {question}\n\nPassages:\n{sb}")
        };

        var completion = await chat.CompleteChatAsync(messages, RerankOptions, ct);
        var parsed = JsonSerializer.Deserialize<RerankResult>(
            completion.Value.Content[0].Text, RerankJson)!;

        var scoreById = parsed.Scores.ToDictionary(s => s.Id, s => s.Score);
        return batch.Select((c, i) => (chunk: c, score: scoreById.GetValueOrDefault(i, -1))).ToList();
    }
}
