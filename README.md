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

Measuring turned out to be more useful than the final score. When a change made the score
worse, that result was written down and kept, rather than re-run until it looked better.

- **The overall score can hide two problems cancelling out.** One change came out with exactly  
  the same score before and after, so it looked like it had done nothing. Looking at each of
  the 15 questions on its own showed it had fixed one and broken another.
- **Adding more content made the search worse, not better.** Restoring text that had been
  dropped by mistake pushed a correct answer from 3rd place out of the top 5 — the new text
  simply outranked it.
- **One change lowered the score and was kept anyway.** Stripping the metadata header off the
  top of each note was clearly right — it was filler being stored as if it were content. The
  score dropped all the same, and the cause turned out to be something else it had exposed:
  with the header gone, one note's four topics packed into a single block instead of getting a
  block each, and the answer that used to have its own block fell from 1st place to 19th. The
  change stayed. The packing was the real bug. "The number went down but the change was good"
  is exactly how bad changes survive, so it only counts when you can point at the mechanism.
- **Fifteen questions is a small test.** One question is nearly 7% of the score, so a change
  that moves a single question is a hint worth chasing, not a result.

After tuning, all 15 questions return a correct source in the top 5, usually as the first
result.

I ran it against my own notes, so the questions and the run log stay private.
[`eval/PLAYBOOK.md`](eval/PLAYBOOK.md) is the method, and that is the part that transfers to
any other AI feature.

## Running it

```bash
# 1. a Postgres with pgvector, and the schema this expects to find
docker run -d --name pgvec -e POSTGRES_PASSWORD=dev -p 5432:5432 pgvector/pgvector:pg16
docker exec -i pgvec psql -U postgres -c "CREATE DATABASE asknotes;"
docker exec -i pgvec psql -U postgres -d asknotes < db/schema.sql

# 2. your OpenAI key, kept out of the repo
dotnet user-secrets set "OpenAI:ApiKey" "sk-..."

# 3. your machine's settings: which folders to index, and the connection string
cp appsettings.Local.example.json appsettings.Local.json

dotnet run
```

Then `GET /ingest` to read and store the notes, and `POST /ask` to ask something —
[`AskMyNotes.http`](AskMyNotes.http) has the requests ready to send, and `GET /dbcheck` says
how many chunks are stored if you want to confirm the ingest worked.

Two things have no defaults and cannot: **`Notes:Roots`**, because your notes are wherever
they are, and the **OpenAI key**. Both endpoints say so plainly rather than failing oddly if
either is missing.

## Tests

```bash
dotnet test
```

23 tests, no network and no database: the chunker (where a note gets cut, which is the biggest
lever on retrieval quality), the loader (reading files, stripping the metadata header), and the
step that pairs passages with the scores the model returned.

One of them pins a known limitation rather than a feature — a passage the model skipped
currently sorts below one it scored zero — so that changing it has to be a decision rather than
an accident.

## Design notes

**[NOTES.md](NOTES.md)** carries the pipeline diagram, every design decision with its
reasoning, the reranker prompt and why it is shaped that way, and eight known limitations with
what I would change.
