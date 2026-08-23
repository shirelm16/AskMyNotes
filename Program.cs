using System.Diagnostics;
using System.Text.Json;
using AskMyNotes.Chunking;
using AskMyNotes.Domain;
using AskMyNotes.Evaluation;
using AskMyNotes.Retrieval;
using Npgsql;
using OpenAI.Chat;
using OpenAI.Embeddings;
using Pgvector;

var builder = WebApplication.CreateBuilder(args);

// Machine-specific settings — the folders to index, and a real connection string. Kept out of
// appsettings.json because that file is committed, and out of appsettings.Development.json
// because that one is committed too. Optional: without it the endpoints say what is missing.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

var connString = builder.Configuration.GetConnectionString("Postgres");
var apiKey = builder.Configuration["OpenAI:ApiKey"];

var dsb = new NpgsqlDataSourceBuilder(connString);
dsb.UseVector();   // register the pgvector type with Npgsql

builder.Services.AddOpenApi();
builder.Services.AddSingleton(dsb.Build());
builder.Services.AddSingleton(new ChatClient("gpt-4o-mini", apiKey));
builder.Services.AddSingleton(new EmbeddingClient("text-embedding-3-small", apiKey));
builder.Services.AddSingleton<IChunker>(new StructureAwareChunker(minSize: 150, maxSize: 1000));
builder.Services.AddSingleton<NoteLoader>();
builder.Services.AddSingleton<ChunkStore>();
builder.Services.AddSingleton<IReranker, LlmReranker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();

// How many chunks are stored. The quickest way to tell whether an ingest actually worked.
app.MapGet("/dbcheck", (ChunkStore store) => store.CountAsync());

// Reads every configured folder and replaces the stored chunks with what it finds.
app.MapGet("/ingest", async (IConfiguration config, NoteLoader loader, ChunkStore store,
                             EmbeddingClient embeddings) =>
{
    // Which folders to index. Configured, not hardcoded: these are absolute paths on
    // whoever's machine is running this.
    var roots = config.GetSection("Notes:Roots").Get<string[]>();
    if (roots is null or { Length: 0 })
        return Results.BadRequest(
            "No note folders configured. Set Notes:Roots — in appsettings.Local.json, " +
            "in user-secrets, or as Notes__Roots__0 in the environment.");

    if (roots.Where(r => !Directory.Exists(r)).ToArray() is { Length: > 0 } missing)
        return Results.BadRequest($"These configured folders don't exist: {string.Join(", ", missing)}");

    var chunks = await loader.LoadAsync(roots);

    // Embedded 100 at a time rather than one call per chunk, for the same reason the rows are
    // written with COPY: round-trips dominate.
    const int embedBatch = 100;
    var vectors = new List<Vector>(chunks.Count);
    for (var i = 0; i < chunks.Count; i += embedBatch)
    {
        var slice = chunks[i..Math.Min(i + embedBatch, chunks.Count)];
        var embedded = await embeddings.GenerateEmbeddingsAsync(slice.Select(c => c.Content).ToList());
        vectors.AddRange(embedded.Value.Select(e => new Vector(e.ToFloats())));
    }

    await store.ReplaceAllAsync(chunks, vectors);
    return Results.Ok(new { chunks = chunks.Count });
});

// Answers a question from the stored notes, citing the files it used.
app.MapPost("/ask", async (AskRequest req, ChunkStore store, IReranker reranker,
                           EmbeddingClient embeddingsClient, ChatClient chatClient) =>
{
    // One stopwatch with snapshots taken along the way, so the parts sum exactly to the total.
    var sw = Stopwatch.StartNew();

    var questionVector = new Vector((await embeddingsClient.GenerateEmbeddingAsync(req.Question)).Value.ToFloats());
    var tEmbed = sw.ElapsedMilliseconds;

    var candidates = await store.SearchAsync(questionVector);
    var tQuery = sw.ElapsedMilliseconds;

    var top = await reranker.RerankAsync(req.Question, candidates);
    var tRerank = sw.ElapsedMilliseconds;

    var context = string.Join("\n\n", top.Select(c => $"[source: {Path.GetFileName(c.Source)}]\n{c.Content}"));
    var messages = new List<ChatMessage>
    {
        new SystemChatMessage(
            "Answer ONLY using the context below. If the answer isn't in it, say you don't know. " +
            "Cite the source file(s) you used."),
        new UserChatMessage($"Context:\n{context}\n\nQuestion: {req.Question}")
    };

    var completion = await chatClient.CompleteChatAsync(messages);
    var tGen = sw.ElapsedMilliseconds;

    app.Logger.LogInformation(
        "embed {Embed} | query {Query} | rerank {Rerank} | generate {Generate} | TOTAL {Total} ms",
        tEmbed, tQuery - tEmbed, tRerank - tQuery, tGen - tRerank, tGen);

    return completion.Value.Content[0].Text;
});

// Runs the labelled question set through the same retrieval path /ask uses
app.MapGet("/eval", async (ChunkStore store, IReranker reranker, EmbeddingClient embeddingsClient) =>
{
    var goldenSetPath = Path.Combine(AppContext.BaseDirectory, "eval", "golden_set.json");
    if (!File.Exists(goldenSetPath))
        return Results.BadRequest($"No labelled question set at {goldenSetPath}.");

    var questions = JsonSerializer.Deserialize<GoldenSetQuestion[]>(await File.ReadAllTextAsync(goldenSetPath));
    if (questions is null or { Length: 0 })
        return Results.BadRequest("The labelled question set could not be read, or is empty.");

    const int maxParallel = 4;
    using var gate = new SemaphoreSlim(maxParallel);

    var results = await Task.WhenAll(questions.Select(async question =>
    {
        await gate.WaitAsync();
        try
        {
            var questionVector = new Vector((await embeddingsClient.GenerateEmbeddingAsync(question.Question)).Value.ToFloats());
            var candidates = await store.SearchAsync(questionVector);
            var top = (await reranker.RerankAsync(question.Question, candidates)).Take(10).ToList();

            // Rank of the first result whose FILE is one the question expects. 0 means the correct
            // file was not in the top 10 at all — which is why 0 must never be read as a position.
            var sources = top.Select(c => c.Source.Replace('\\', '/')).ToList();
            var rank = sources.FindIndex(s => question.ExpectedSources.Any(e => s.EndsWith(e))) + 1;

            return (question.Id, Rank: rank);
        }
        finally { gate.Release(); }
    }));

    return Results.Ok(new
    {
        totalQuestions = questions.Length,
        // hit-rate@5: how many questions put a correct source in the top 5.
        hitsAtFive = results.Count(r => r.Rank is >= 1 and <= 5),
        // Mean reciprocal rank: rank 1 scores 1, rank 2 scores 0.5, a miss scores 0.
        mrr = results.Sum(r => r.Rank == 0 ? 0 : 1.0 / r.Rank) / results.Length,
        ranks = string.Join(", ", results.OrderBy(r => r.Id).Select(r => $"Q{r.Id} rank {r.Rank}"))
    });
});

app.Run();
