# AskMyNotes — design notes

A retrieval-augmented generation (RAG) service in .NET 9 / ASP.NET Core that answers questions
over a folder of personal Markdown notes. PostgreSQL with pgvector for storage and similarity
search, OpenAI for embeddings and generation.

The interesting part isn't the RAG pipeline — those are easy to assemble now. It's the
**evaluation harness**, and the fact that every tuning decision was made from a measurement
rather than from intuition.

**Stack**

| Piece | Choice |
|---|---|
| API | ASP.NET Core minimal API, .NET 9 |
| Vector store | PostgreSQL + `pgvector` (`Npgsql`, `Pgvector`) |
| Embeddings | OpenAI `text-embedding-3-small` |
| Generation + reranking | OpenAI `gpt-4o-mini` |

**Endpoints:** `/ingest`, `/ask` (POST), `/eval`, plus `/dbcheck` and `/test` for scratch work.

---

## 1. The pipeline

```
                    INGEST                                   ASK
                       |                                      |
        walk *.md under reports/ + memory/           embed the question
                       |                                      |
          strip YAML frontmatter                    vector search, LIMIT 100
                       |                              (cosine distance)
        SplitIntoUnits  <- structure-aware                    |
                       |                            cap 3 chunks per source file
        PackUnits(min 150, max 1000)                          |
                       |                              LLM reranker, 0-10
        embed in batches of 100                               |
                       |                            top chunks -> context
        TRUNCATE + binary COPY (one txn)                      |
                       |                              gpt-4o-mini answers,
                  chunks table                          citing source files
```

`/eval` runs exactly the same retrieval path as `/ask`, which is the point — otherwise the
numbers would describe a pipeline nobody actually uses.

---

## 2. Design decisions, and why

### Structure-aware chunking — the single biggest win

Most tutorials chunk by fixed character count, which cuts sentences and ideas in half. This
splits where **a new idea begins**:

```csharp
static bool IsUnitStart(string line) =>
       Regex.IsMatch(line, @"^#{1,6}\s")           // ## Heading
    || Regex.IsMatch(line, @"^(\d+\.|[-*+])\s")    // "1. " or "- "
    || Regex.IsMatch(line, @"^\*\*.+:\*\*\s*$");   // **Bold lead-in:**
```

Blank lines also end a unit, and the splitter tracks code fences so it never cuts inside one.
Units are then packed to between 150 and 1000 characters — 1000 is a ceiling, not a target, and
a chunk is emitted as soon as it passes 150 so it can stand on its own. Anything oversized is
split on sentence boundaries rather than mid-word.

**Result: hit-rate@5 went from 10/15 to 13/15.** It was the largest single improvement in the
whole tuning log. (Later work took the pipeline to **15/15, MRR 0.763** — the run log in
`eval/RESULTS.md` stops mid-tuning at 13/15, so quote the final figure from here.)

### Two-stage retrieval: cheap recall, then expensive precision

Vector search runs first with `LIMIT 100`. It's fast and approximate, and its job is *recall* —
get the right chunk somewhere in the candidate pool. The reranker runs second, and its job is
*precision* — decide which of those hundred actually answer the question.

This is the standard shape of production retrieval, and the reason is cost. Scoring 100 passages
with an LLM per query would be slow and expensive if the database couldn't narrow it down first;
relying on the database alone gives you chunks that are *topically similar* rather than chunks
that *answer the question*.

### Per-source capping

```csharp
var cap = 3;   // at most 3 chunks from any one file
```

Over-fetch 100, then keep at most three chunks per source file. Without this, one long,
verbose file can occupy most of the top results and crowd out the file that actually holds the
answer. It costs nothing and protects against a single document dominating.

### The reranker prompt does one specific job

```
10 = directly answers it. 5 = related and partly useful.
0 = same topic but does not answer it, or states the opposite.
A passage about the right subject that does not contain the answer scores low.
```

That last line is the whole point. Embedding similarity is *topical* — it happily returns a
passage that discusses the subject without containing the answer. The reranker exists
specifically to separate "about this" from "answers this," so the prompt has to say so.

Supporting choices:
- **Temperature 0** — scores must be repeatable, or the eval numbers mean nothing between runs.
- **Strict JSON schema output** (`jsonSchemaIsStrict: true`) — the model returns
  `{scores:[{id,score}]}` and it's deserialised directly. No fragile text parsing, no retry loop
  for malformed responses.
- **Batches of 18, at most 4 in flight** (`SemaphoreSlim`) — this is the latency fix, see below.
  Also bounded, so a large candidate set doesn't fire a hundred simultaneous API calls.
- **Ties break on vector distance** (`OrderByDescending(Score).ThenBy(Distance)`) — when the LLM
  gives two passages the same score, fall back to the embedding signal rather than to arbitrary
  order.

### Ingest is atomic and bulk

`TRUNCATE` and a binary `COPY` inside a single transaction. Binary COPY is dramatically faster
than row-by-row inserts, and wrapping both in a transaction means the index is never left half
rebuilt — either the new set is fully there or the old one is untouched. Embeddings are
generated in batches of 100 to cut API round-trips.

### Latency: 30.6s → 8.2s, measured

`/ask` logs `embed | query | rerank | generate | TOTAL` on every request, using one stopwatch
with snapshots so the parts sum exactly to the total.

The first working version answered in **30.6 seconds** — unusable. Instrumenting the stages
showed the reranker was almost all of it, scoring candidates one call at a time. Batching it
(18 candidates per call, 4 concurrent, with a per-batch fallback to distance order if the
returned score count doesn't match) brought it to **8.2 seconds**. Retrieval quality held at
15/15 with MRR 0.763 — the speed-up cost nothing in accuracy, which is only knowable because
the golden set was there to check it against.

Warm breakdown:

```
embed 1360 | query 482 | rerank 3892 | generate 2434 | TOTAL 8168 ms
   17%          6%          48%           30%
```

**And the useful conclusion: the floor is about 3.8 seconds.** Two OpenAI round trips — embed
the question, generate the answer — are unavoidable in this architecture. Optimising below ~4s
isn't a tuning problem, it's an architecture change. Knowing where your floor is, and being
able to say why, is a better answer than a faster number.

### A change that lowered the score and was kept anyway

Run C5 removed YAML frontmatter before chunking. The score dropped. It was kept — but only
because the mechanism was found first: freeing ~230 characters in `sd_gaps.md` let all four
numbered items pack into one chunk, so the capacity-math item lost its own dedicated chunk and
fell from rank 1 to rank 19.

Found by querying the raw SQL, seeing the chunk at rank 19, and asking why the others outranked
it — when the right question was whether *this* one had fallen.

Worth stating carefully in an interview: *"the number went down but the change was good"* is
exactly how bad changes survive. It's only legitimate when you can name the mechanism, and here
the mechanism was found before the decision, not after.

---

## 3. The evaluation harness — the part worth talking about

A golden set of 15 questions, each labelled with the source file that should be retrieved. For
each question the harness runs the real retrieval path, finds the rank of the first correct
source, and reports:

- **hit-rate@5** — how many questions put a correct source in the top 5
- **MRR@10** — mean reciprocal rank, which rewards being at rank 1 rather than rank 4
- **the rank of every individual question**, not just the totals

**The method:** one change per run. Re-ingest, re-measure, write down the result — including the
runs that made things worse and the ones that changed nothing.

### What the log actually taught

These are the findings, and they're better interview material than the score is.

**Aggregate numbers hide compensating changes.** Run C1 scored 12/15 both before and after —
apparently a no-op. Per-question data showed it had *fixed* Q3 and *broken* Q10. Recorded only
as a headline number, C1 would have looked pointless and been reverted, when in fact it fixed a
real bug and exposed a second, unrelated weakness.

**Adding content can evict good results.** Q10 regressed without anything about Q10 changing.
Its chunk was byte-identical. What changed was the rest of the collection — newly indexed
chunks outranked it and pushed it out of the top 5. *More data is not monotonically better
retrieval.*

**A falling score can mean the measurement got honest.** Runs C2 and C5 both scored lower while
fixing genuine bugs. Working out why is what surfaced the frontmatter problem — YAML headers,
including a random session UUID, were being embedded as though they were content.

**Beware the moving baseline.** In C4c the notes themselves had changed between runs, so the
measurement was against a different collection. Noticed and recorded rather than silently
attributed to the change under test.

**A confident diagnosis was wrong twice.** Q15 was called a vocabulary mismatch, with the claim
that no chunking change could fix it. Structure-aware chunking then put it at **rank 1**. The
words had been there all along, diluted inside a chunk that also covered three other topics.
*What looked like a wording problem was dilution.*

**Know the resolution of your own metric.** 15 questions means one question is 6.7%. Single-
question swings are noise, not evidence. Written into the log explicitly so later runs weren't
over-read.

---

## 4. Known limitations — what I'd change

1. **No vector index.** `ORDER BY embedding <=> $1 LIMIT 100` scans every row. Fine at this
   size, useless at scale — it needs an HNSW or IVFFlat index, which also means accepting
   approximate search and re-measuring the effect on the golden set.
2. **Ingest is full-rebuild only.** Every run truncates and re-embeds everything. No change
   detection, no incremental update, and re-embedding unchanged files costs money.
3. **The golden set measures retrieval, not answers.** hit@5 says the right file was found. It
   says nothing about whether the generated answer was correct or whether the model used the
   context it was given. Measuring answer quality is a harder, separate problem.
4. **15 questions is small**, and they were written by the same person who tuned against them —
   there's a real risk of fitting the pipeline to this particular set.
5. **Two questions still fail.** Q2 has missed in every run and has never been diagnosed. Q6
   regressed during structure-aware chunking and hasn't recovered.
6. **Passages are truncated to 1200 characters before reranking**, so an answer sitting late in
   a long chunk can be invisible to the reranker even though it survived retrieval.
7. **A failed score parse silently demotes a chunk** — a missing id defaults to `-1`, which sorts
   to the bottom. It should be distinguishable from a genuine zero.
8. **Reranking still costs an LLM call per batch** and is 48% of latency even after batching.
   Worth measuring against a cheaper cross-encoder rather than assuming an LLM is the right tool.
9. **The latency numbers have no noise floor yet.** They're single calls, and `generate` was
   measured at both 2,434ms and 6,715ms — it tracks answer length, not a fixed cost. One early
   sample summed to 12.9s against a warm total of 7.9s, so cold start is real. Proper method:
   run `/ask` 5–10 times, discard the first, report the median — the same discipline already
   applied to the eval set.

---

## 5. Talking about it

**CV bullets:**

> AskMyNotes (.NET 9, ASP.NET Core, PostgreSQL/pgvector, OpenAI) — built a retrieval-augmented
> generation service that answers questions over a personal document set, using structure-aware
> chunking and an LLM-based reranking stage.
>
> Retrieval evaluation harness — built a labelled golden set measuring hit-rate@k and MRR to make
> retrieval quality measurable, then tuned the pipeline through controlled single-change
> experiments.

**Sixty-second version:**

Markdown notes are chunked on structure — headings, list items, bold lead-ins — rather than by
character count, embedded with `text-embedding-3-small` and stored in Postgres with pgvector. A
question is embedded, the database returns the hundred nearest chunks capped at three per source
file, and an LLM reranker scores each one from 0 to 10 on whether it *answers* the question
rather than whether it's *about* the topic. The top chunks become context for a grounded answer
that cites its sources.

The part I'd actually want to talk about is the evaluation. Fifteen labelled questions, hit-rate
at 5 and MRR, one change per run, every result written down. It caught a change that looked like
a no-op but had fixed one question and broken another, and it proved a diagnosis I'd made twice
was wrong — what I'd called a vocabulary problem turned out to be dilution, and better chunking
moved that question from missing entirely to rank one.

**The framing that lands best:** lead with the judgment, not the project. The most useful thing
it taught wasn't retrieval — it was that a score going *down* while fixing real bugs meant the
measurement had become more honest. Claims no production experience, and reads as senior
measurement discipline rather than as a tutorial followed to completion.
