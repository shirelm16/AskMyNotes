using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Npgsql;
using NpgsqlTypes;
using OpenAI.Chat;
using OpenAI.Embeddings;
using Pgvector;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();
var app = builder.Build();
var connString = builder.Configuration.GetConnectionString("Postgres");
var apiKey = builder.Configuration["OpenAI:ApiKey"];

var dsb = new NpgsqlDataSourceBuilder(connString);
dsb.UseVector(); //register vector type
var dataSource = dsb.Build();
var chatClient = new ChatClient("gpt-4o-mini", apiKey);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
var embeddingClient = new EmbeddingClient("text-embedding-3-small", apiKey);

app.MapGet("/dbcheck", async () =>
{
    await using var conn = await dataSource.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("SELECT count(*) FROM chunks", conn);
    var count = (long)(await cmd.ExecuteScalarAsync())!;

    return count;
});


app.MapGet("/ingest", async () =>
{
    // Which folders to index. Configured, not hardcoded: these are absolute paths on
    // whoever's machine is running this.
    var roots = app.Configuration.GetSection("Notes:Roots").Get<string[]>();
    if (roots is null or { Length: 0 })
        return Results.BadRequest(
            "No note folders configured. Set Notes:Roots — in appsettings.Local.json, " +
            "in user-secrets, or as Notes__Roots__0 in the environment.");

    var missing = roots.Where(r => !Directory.Exists(r)).ToArray();
    if (missing.Length > 0)
        return Results.BadRequest($"These configured folders don't exist: {string.Join(", ", missing)}");
    var chunks = await Chunker(roots);
    var batchSize = 100;

    var vectors = new List<Vector>(chunks.Count);
    for (int i = 0; i < chunks.Count; i += batchSize)
    {
        var slice = chunks[i..Math.Min(i + batchSize, chunks.Count)];
        var embeddings = await embeddingClient.GenerateEmbeddingsAsync(
            slice.Select(c => c.Content).ToList());
        vectors.AddRange(embeddings.Value.Select(e => new Vector(e.ToFloats())));
    }

    await using var conn = await dataSource.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    await using (var truncate = new NpgsqlCommand("TRUNCATE chunks", conn, tx))
        await truncate.ExecuteNonQueryAsync();

    await using (var writer = await conn.BeginBinaryImportAsync(
    "COPY chunks (source, content, embedding) FROM STDIN (FORMAT BINARY)"))
    {
        for (int i = 0; i < chunks.Count; i++)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(chunks[i].Source, NpgsqlDbType.Text);
            await writer.WriteAsync(chunks[i].Content, NpgsqlDbType.Text);
            await writer.WriteAsync(vectors[i]);
        }
        await writer.CompleteAsync();
    }

    await tx.CommitAsync();
    return Results.Ok(new { chunks = chunks.Count });
});

app.MapPost("/ask", async (AskRequest req) => {
    var sw = Stopwatch.StartNew();
    
    Vector questionVector = await EmbedQuestion(req.Question, embeddingClient);
    var tEmbed = sw.ElapsedMilliseconds;
    
    await using var conn = await dataSource.OpenConnectionAsync();
    
    var topChunksByDistance = await GetTopChunksWithDedup(conn, questionVector);
    var tQuery = sw.ElapsedMilliseconds;
    
    var rerankedTopChunks = await Reranker.RerankAsync(chatClient, req.Question, topChunksByDistance);
    var tRerank = sw.ElapsedMilliseconds;
    
    var contextString = string.Join("\n\n", rerankedTopChunks.Select(x => $"[source: {Path.GetFileName(x.Source)}]\n{x.Content}"));
    var messages = new List<ChatMessage>
    {
        new SystemChatMessage(
            "Answer ONLY using the context below. If the answer isn't in it, say you don't know. Cite the source file(s) you used."),
        new UserChatMessage($"Context:\n{contextString}\n\nQuestion: {req.Question}")
    };
    ChatCompletion completion = await chatClient.CompleteChatAsync(messages);
    
    var tGen = sw.ElapsedMilliseconds;
    Console.WriteLine($"embed {tEmbed} | query {tQuery - tEmbed} | rerank {tRerank - tQuery} | " +
    $"generate {tGen - tRerank} | TOTAL {tGen}");
    
    return completion.Content[0].Text;
});

app.MapGet("/eval", async () =>
{
    var golden_set = await File.ReadAllTextAsync("eval/golden_set.json");
    var questions = JsonSerializer.Deserialize<GoldenSetQuestion[]>(golden_set);

    if (questions == null)
        throw new Exception("JSON parsing failed for golder_set.json");

    var questionsRank = new List<(int Q, int Rank)>();
    const int maxParallel = 4;
    using var gate = new SemaphoreSlim(maxParallel);

    var tasks = questions.Select(async question =>
    {
        await gate.WaitAsync();
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync();   // per task
            
            Vector questionVector = await EmbedQuestion(question.Question, embeddingClient);
            var topChunksByDistance = await GetTopChunksWithDedup(conn, questionVector);
            var sw = Stopwatch.StartNew();
            var topChunks = (await Reranker.RerankAsync(chatClient, question.Question, topChunksByDistance)).Take(10);
            Console.WriteLine($"Q{question.Id}: reranking took: {sw.ElapsedMilliseconds}");
            var actualSources = topChunks.Select(c => c.Source.Replace('\\', '/')).ToList();
            var hitRank = actualSources.FindIndex(a => question.ExpectedSources.Any(e => a.EndsWith(e))) + 1;
            return (question.Id, hitRank);
        }
        finally { gate.Release(); }
    });

    var results = await Task.WhenAll(tasks);
    var totalHits = results.Count(r => r.hitRank >= 1 && r.hitRank <= 5);
    var mrr10 = results.Sum(r => r.hitRank == 0 ? 0 : 1.0 / r.hitRank) / results.Length;
    var ranks = string.Join(", ", results.Select(r => $"Q{r.Id} rank {r.hitRank}"));
    
    return new { totalHits, 
            totalQuestions = questions.Length,  
            mrr10, 
            questionsRank = ranks};
});

app.Run();

static async Task<List<Chunk>> Chunker(string[] roots)
{
    var chunks = new List<Chunk>();
    foreach (var root in roots)
    {
        var files = Directory.GetFiles(root, "*.md", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/eval/"));

        foreach (var file in files)
        {
            var text = await File.ReadAllTextAsync(file);
            text = Regex.Replace(text, @"\A---\r?\n.*?\r?\n---(\r?\n|\z)", "",
                                 RegexOptions.Singleline); //remove YAML frontmatter

            foreach (var chunk in PackUnits(SplitIntoUnits(text), minSize: 150, maxSize: 1000))
                chunks.Add(new Chunk(file, chunk));
        }
    }
    return chunks;
}

static List<string> SplitIntoUnits(string text)
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
static bool IsUnitStart(string line) =>
       Regex.IsMatch(line, @"^#{1,6}\s")           // ## Heading
    || Regex.IsMatch(line, @"^(\d+\.|[-*+])\s")    // "1. " or "- ", column 0 only
    || Regex.IsMatch(line, @"^\*\*.+:\*\*\s*$");   // **Bold lead-in:**

static List<string> PackUnits(List<string> units, int minSize, int maxSize)
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
            if (sb.Length > 0 && sb.Length + 2 + piece.Length > maxSize)
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

static List<string> SplitOversized(string unit, int maxSize)
{
    var parts = new List<string>();
    var sb = new StringBuilder();

    foreach (var sentence in Regex.Split(unit, @"(?<=[.!?])\s+"))
    {
        if (sb.Length > 0 && sb.Length + 1 + sentence.Length > maxSize)
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

static async Task<Vector> EmbedQuestion(string question, EmbeddingClient embeddingClient)
{
    OpenAIEmbedding questionEmbedding = await embeddingClient.GenerateEmbeddingAsync(question);
    var questionVector = new Vector(questionEmbedding.ToFloats());
    return questionVector;
}

static async Task<List<(string Source, string Content, double Distance)>> GetTopChunksWithDedup(NpgsqlConnection conn, Vector questionVector)
{
    var cap = 3;
    var cmd = new NpgsqlCommand("SELECT source, content, embedding <=> $1 AS distance\r\nFROM chunks\r\nORDER BY embedding <=> $1\r\nLIMIT 100;", conn);
    cmd.Parameters.AddWithValue(questionVector);
    NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
    var sourceToCunks = new Dictionary<string, List<(string Source, string Content, double Distance)>>();
    while (await reader.ReadAsync())
    {
        var source = reader.GetString(0);
        var content = reader.GetString(1);
        var distance = reader.GetDouble(2);
        if (!sourceToCunks.ContainsKey(source))
            sourceToCunks[source] = [];

        if (sourceToCunks[source].Count < cap)
            sourceToCunks[source].Add((source, content, distance));
    }
    return sourceToCunks.Values.SelectMany(x => x).ToList();
}

record Chunk(string Source, string Content);

record AskRequest(string Question);

record RerankScore(int Id, int Score);

record RerankResult(List<RerankScore> Scores);

public class GoldenSetQuestion
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("question")]
    public string Question { get; set; }

    [JsonPropertyName("expected_sources")]
    public string[] ExpectedSources { get; set; }
}

public static class Reranker
{
    const int batchSize = 18;
    const int maxParallel = 4;

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

    public static async Task<List<(string Source, string Content, double Distance)>> RerankAsync(ChatClient chatClient, string question, 
        List<(string Source, string Content, double Distance)> candidates)
    {
        var batches = new List<List<(string Source, string Content, double Distance)>>();
        for (int i = 0; i < candidates.Count; i += batchSize)
            batches.Add(candidates[i..Math.Min(i + batchSize, candidates.Count)]);

        using var gate = new SemaphoreSlim(maxParallel);

        var tasks = batches.Select(async batch =>
        {
            await gate.WaitAsync();
            try { return await ScoreBatchAsync(chatClient, question, batch); }
            finally { gate.Release(); }
        });

        return (await Task.WhenAll(tasks))
            .SelectMany(x => x)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Chunk.Distance)
            .Select(x => x.Chunk)
            .ToList();
    }

    private static async Task<List<((string Source, string Content, double Distance) Chunk, int Score)>> ScoreBatchAsync(ChatClient chatClient, string question, List<(string Source, string Content, double Distance)> batch)
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

        var completion = await chatClient.CompleteChatAsync(messages, RerankOptions);
        var parsed = JsonSerializer.Deserialize<RerankResult>(
            completion.Value.Content[0].Text, RerankJson)!;

        var scoreById = parsed.Scores.ToDictionary(s => s.Id, s => s.Score);
        return batch.Select((c, i) => (chunk: c, score: scoreById.GetValueOrDefault(i, -1))).ToList();
    }
}