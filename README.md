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
  down. The method is in [`eval/PLAYBOOK.md`](eval/PLAYBOOK.md).

## What the evaluation actually taught

The findings were more interesting than the score, and the runs that went backwards were kept
rather than quietly re-run.

- **Aggregate numbers hide compensating changes.** One run scored the same before and after
  and looked like a no-op. Per-question results showed it had fixed a real bug *and* exposed an
  unrelated weakness. Only the headline number was unchanged.
- **More data is not automatically better retrieval.** Recovering ~131 previously-dropped
  chunks pushed a correct answer from 3rd place to below 5th, purely because the new chunks
  outranked it. Adding content can push good results out of the top few.
- **A falling score meant the measurement was honest**, and one change was kept even though it
  lowered the number, for reasons written down at the time.
- **With only 15 questions, one question is 6.7% of the score**, so a change that moves a
  single question is inside the noise — a lead to investigate, not a result. Recorded as a
  caveat at the time, not realised afterwards.

The tuned pipeline answers **all 15 questions with a correct source in the top 5**, at a mean
reciprocal rank of 0.763 — meaning the right source usually sits at or near the first result.

The corpus I evaluated against was my own notes, so the labelled golden set and the raw run log
stay private — the questions quote personal content. [`eval/PLAYBOOK.md`](eval/PLAYBOOK.md) is
the procedure they followed, and it is the part that transfers to any other AI feature: what a
dataset, an expected output, a scorer and an aggregate look like, and when an LLM judge is
actually warranted (rarely).

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
