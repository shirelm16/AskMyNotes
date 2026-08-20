# AskMyNotes

A retrieval-augmented generation service in .NET 9 — ASP.NET Core, PostgreSQL with `pgvector`,
and OpenAI — that answers questions over a folder of markdown notes and cites the files it
used. Ingest, chunk, embed, retrieve, rerank, answer.

The retrieval quality is measured rather than assumed, which is the part of the project I would
actually want to talk about.

## What's interesting in it

- **Structure-aware chunking** — splitting on markdown structure instead of a fixed character
  window. The single largest improvement in the whole tuning log: hit-rate@5 went from **10/15
  to 13/15** from that one change.
- **Two-stage retrieval** — cheap vector recall over a wide candidate set, then an LLM reranker
  at temperature 0 returning strict JSON, with a per-source cap so one long document cannot
  fill every slot.
- **Latency measured and then fixed: 30.6s → 8.2s**, by batching the reranker calls — and the
  evaluation proved the speed-up cost nothing in accuracy, which is only knowable because the
  accuracy was being measured.
- **An evaluation harness**: fifteen labelled questions, hit-rate@k and MRR@10, one change per
  run, every result written down. The method is in [`eval/PLAYBOOK.md`](eval/PLAYBOOK.md).

## What the evaluation actually taught

The findings were more interesting than the score, and the runs that went backwards were kept
rather than quietly re-run.

- **Aggregate numbers hide compensating changes.** One run scored the same before and after
  and looked like a no-op. Per-question results showed it had fixed a real bug *and* exposed an
  unrelated weakness. Only the headline number was unchanged.
- **More data is not monotonically better retrieval.** Recovering ~131 previously-dropped
  chunks pushed a correct answer from rank 3 to below rank 5. Adding content can evict good
  results from top-k.
- **A falling score meant the measurement was honest**, and one change was kept even though it
  lowered the number, for reasons written down at the time.
- **Fifteen questions means one question is 6.7%**, so single-question swings are leads to
  investigate, not results — recorded as a caveat at the time, not discovered afterwards.

The tuned pipeline reaches **15/15 hit-rate@5 with MRR 0.763**.

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
