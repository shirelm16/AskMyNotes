using AskMyNotes.Domain;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace AskMyNotes.Retrieval;

/// <summary>
/// Everything that touches the chunks table: replacing the whole set, and finding the
/// candidates for a question.
/// </summary>
public sealed class ChunkStore(NpgsqlDataSource dataSource)
{
    /// <summary>How many chunks the vector search returns before reranking.</summary>
    public const int CandidateLimit = 100;

    /// <summary>
    /// At most this many chunks from any one file may reach the reranker, so a single long
    /// note cannot leave no room for the file that actually holds the answer.
    /// </summary>
    public const int PerSourceCap = 3;

    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM chunks", conn);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Replaces every stored chunk, in one transaction.
    ///
    /// TRUNCATE empties the table, so between it and the reload there is a moment with nothing
    /// searchable. Inside a transaction that moment is never visible: other connections keep
    /// reading the old rows until the commit, and a failure anywhere leaves the old set intact.
    ///
    /// The rows go in through a binary COPY rather than inserts. An insert per row is a network
    /// round-trip and a statement parse per row; COPY streams them through one operation. Binary
    /// rather than text because each embedding is 1536 floats — about 18 KB written as text and
    /// parsed back into numbers at the far end, against about 6 KB as bytes.
    /// </summary>
    public async Task ReplaceAllAsync(IReadOnlyList<Chunk> chunks, IReadOnlyList<Vector> vectors,
                                      CancellationToken ct = default)
    {
        if (chunks.Count != vectors.Count)
            throw new ArgumentException($"{chunks.Count} chunks against {vectors.Count} vectors — they must pair up.");

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var truncate = new NpgsqlCommand("TRUNCATE chunks", conn, tx))
            await truncate.ExecuteNonQueryAsync(ct);

        await using (var writer = await conn.BeginBinaryImportAsync(
            "COPY chunks (source, content, embedding) FROM STDIN (FORMAT BINARY)", ct))
        {
            for (var i = 0; i < chunks.Count; i++)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(chunks[i].Source, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(chunks[i].Content, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(vectors[i], ct);
            }
            await writer.CompleteAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// The nearest chunks to a question, closest first, with no more than
    /// <see cref="PerSourceCap"/> from any single file.
    ///
    /// The cap is applied while reading, in distance order, so the chunks kept for a file are
    /// its nearest ones — a file that appears at all keeps its best representative.
    /// </summary>
    public async Task<List<ScoredChunk>> SearchAsync(Vector question, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
             SELECT source, content, embedding <=> $1 AS distance
             FROM chunks
             ORDER BY embedding <=> $1
             LIMIT {CandidateLimit};
             """, conn);
        cmd.Parameters.AddWithValue(question);

        var keptPerSource = new Dictionary<string, int>();
        var results = new List<ScoredChunk>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var source = reader.GetString(0);
            var kept = keptPerSource.GetValueOrDefault(source);
            if (kept >= PerSourceCap) continue;

            keptPerSource[source] = kept + 1;
            results.Add(new ScoredChunk(source, reader.GetString(1), reader.GetDouble(2)));
        }

        return results;
    }
}
