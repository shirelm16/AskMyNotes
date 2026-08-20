# AskMyNotes

A retrieval-augmented generation service in .NET 9 — ASP.NET Core, PostgreSQL with `pgvector`,
and OpenAI — that answers questions over a folder of markdown notes and cites the files it
used. Ingest, chunk, embed, retrieve, rerank, answer.

The retrieval quality is measured rather than assumed.

## What's interesting in it

- **Structure-aware chunking** — splitting notes on their markdown structure instead of at a
  fixed character count. The single largest improvement in the whole tuning log: of the **15
  labelled test questions**, the number that retrieved a correct source in the top 5 results
  went from **10 to 13** on that one change.
- **Two-stage retrieval** — a cheap vector search pulls the nearest **100** chunks, of which at
  most **3 may come from any one source file**, so a single long note cannot crowd every other
  document out of the shortlist. An LLM reranker then reads that shortlist and picks the final
  **10**, at temperature 0 and returning strict JSON so the output is parseable rather than
  chatty.
- **Latency measured and then fixed: 30.6s → 8.2s**, by batching the reranker calls — and the
  evaluation proved the speed-up cost nothing in accuracy, which is only knowable because the
  accuracy was being measured.
- **An evaluation harness**: 15 questions, each labelled with the source file that should
  answer it, scored on two measures — **hit-rate@5** (did a correct source make the top 5?) and
  **mean reciprocal rank** (how near the top was it?). One change per run, every result written
  down.

## What the evaluation taught

Measuring it turned out to be more useful than the final score, and the runs that made things
worse were kept rather than quietly re-run.

- **An average can hide two problems cancelling out.** One change scored the same before and
  after, so it looked pointless. Checking question by question showed it had fixed one thing
  and broken another.
- **Adding more content made the search worse, not better.** Restoring text that had been
  dropped by mistake pushed a correct answer from 3rd place out of the top 5 — the new text
  simply outranked it.
- **One change lowered the score and was kept anyway**, because by then I understood why it
  had, and the reason was a good one.
- **Fifteen questions is a small test.** One question is nearly 7% of the score, so a change
  that moves a single question is a hint worth chasing, not a result.

After tuning, all 15 questions return a correct source in the top 5, usually as the first
result.

I ran it against my own notes, so the questions and the run log stay private.
[`eval/PLAYBOOK.md`](eval/PLAYBOOK.md) is the method, and that is the part that transfers to
any other AI feature.

## Running it

```bash
docker run -d --name pgvector -e POSTGRES_PASSWORD=... -p 5432:5432 pgvector/pgvector:pg16
dotnet user-secrets set "OpenAI:ApiKey" "sk-..."
dotnet run
```

Then point the ingest endpoint at a folder of markdown and ask it something —
[`AskMyNotes.http`](AskMyNotes.http) has the requests ready to send.

## Design notes

**[NOTES.md](NOTES.md)** carries the pipeline diagram, every design decision with its
reasoning, the reranker prompt and why it is shaped that way, and eight known limitations with
what I would change. [`eval/PLAYBOOK.md`](eval/PLAYBOOK.md) is the method the tuning followed.
