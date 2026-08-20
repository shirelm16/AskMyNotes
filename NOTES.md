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

**Endpoints:** `/ingest`, `/ask` (POST), `/eval`, and `/dbcheck`.

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
      PackUnits(min 150 chars, max 1000 chars)                |
                       |                              LLM reranker, 0-10
             embed in batches of 100                          |
                       |                            top chunks -> context
        TRUNCATE + binary COPY (one txn)                      |
                       |                              gpt-4o-mini answers,
                  chunks table                        citing source files
```

`/eval` runs exactly the same retrieval path as `/ask`, which is the point — otherwise the
numbers would describe a pipeline nobody actually uses.

---

## 2. Design decisions, and why

### Structure-aware chunking — the largest single improvement

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

**Result: 10 of the 15 test questions found a correct source in their top 5 results before this
change; 13 did after.** It was the largest single improvement in the whole tuning log. (The test
set is 15 questions, each labelled with the file that should answer it — section 3 describes it.)

Later tuning took that to 15 of 15. The run log in `eval/RESULTS.md` stops partway through, at
13, so the final figures are stated here rather than there.

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

One run stripped the metadata header off the top of each note before chunking. That was plainly
the right thing to do — the header is filler, not content. The score dropped anyway.

It was kept, but only because the cause was found first. Removing the header freed about 230
characters in one file, which was just enough for all four of its numbered items to pack into a
single chunk instead of getting one each. The item that answered the question lost its own
dedicated chunk and fell from 1st place to 19th.

Finding that meant querying the database directly, seeing the chunk sitting at rank 19, and
asking why the others now outranked it — when the better question was whether *this* one had
fallen. "The number went down but the change was good" is exactly how bad changes survive, so it
only counts when the mechanism can be named.

---

## 3. The evaluation harness

A golden set of 15 questions, each labelled with the source file that should be retrieved. For
each question the harness runs the real retrieval path, finds the rank of the first correct
source, and reports:

- **hit-rate@5** — of the 15 questions, how many had a correct source somewhere in their top 5
  results. A blunt pass/fail per question.
- **mean reciprocal rank** — how *near the top* the correct source landed, averaged across the
  questions. Rank 1 scores 1, rank 2 scores 0.5, rank 4 scores 0.25. It separates "found it
  first" from "found it fourth", which hit-rate@5 treats identically.
- **the rank of every individual question**, not just the totals — the part that turned out to
  matter most.

**The method:** one change per run. Re-ingest, re-measure, write down the result — including the
runs that made things worse and the ones that changed nothing.

### What the log actually taught

**The total can hide two changes cancelling out.** One run scored 12 of 15 both before and
after, so on the headline number it had done nothing. Question by question, it had *fixed* one
question and *broken* another. Judged on the total alone it would have been reverted as
pointless — when it had actually fixed a real bug and exposed a second, unrelated one.

**Adding content can push good results out.** One question got worse without anything about it
changing — the chunk holding its answer was byte-for-byte identical. What changed was everything
around it: newly indexed chunks scored higher and pushed it out of the top 5. More content does
not simply mean better search.

**A falling score can mean the measurement got more honest.** Two separate runs scored lower
while fixing genuine bugs. Chasing down why is what surfaced the metadata problem — the header
block at the top of each note, including a random session id, was being stored and searched as
if it were part of the note's content.

**Only change one thing at a time — including the data.** In one run the notes themselves had
been edited between measurements, so the two numbers described different collections and could
not be compared. Recorded as such, rather than being credited to the change under test.

**A confident diagnosis was wrong twice.** One question kept failing and I decided the cause was
a wording mismatch between the question and the note — and that no change to chunking could fix
it. Structure-aware chunking then put it at **rank 1**. The words had been there the whole time,
diluted inside a chunk that also covered three other topics.

**Know how precise your own measurement is.** With 15 questions, one question is 6.7% of the
score, so a change that moves a single question is noise rather than evidence. Written into the
log at the time, so later runs weren't over-read.

---

## 4. Known limitations, and future improvements

1. **No vector index.** `ORDER BY embedding <=> $1 LIMIT 100` scans every row. Fine at this
   size, useless at scale — it needs one of pgvector's approximate-search indexes (HNSW or
   IVFFlat), which trade exact results for speed, so the effect would have to be re-measured
   against the test set rather than assumed to be free.
2. **Ingest is full-rebuild only.** Every run truncates and re-embeds everything. No change
   detection, no incremental update, and re-embedding unchanged files costs money.
3. **The golden set measures retrieval, not answers.** hit@5 says the right file was found. It
   says nothing about whether the generated answer was correct or whether the model used the
   context it was given. Measuring answer quality is a harder, separate problem.
4. **15 questions is small**, and they were written by the same person who tuned against them —
   there's a real risk of fitting the pipeline to this particular set.
5. **Passages are truncated to 1200 characters before reranking**, so an answer sitting late in
   a long chunk can be invisible to the reranker even though it survived retrieval.
6. **A failed score parse silently demotes a chunk** — a missing id defaults to `-1`, which sorts
   to the bottom. It should be distinguishable from a genuine zero.
7. **Reranking still costs an LLM call per batch** and is 48% of latency even after batching.
   A purpose-built reranking model (a cross-encoder) is cheaper per passage and worth measuring
   against, rather than assuming a general-purpose LLM is the right tool for scoring.
8. **The latency numbers have no noise floor yet.** They're single calls, and `generate` was
   measured at both 2,434ms and 6,715ms — it tracks answer length, not a fixed cost. One early
   sample summed to 12.9s against a warm total of 7.9s, so cold start is real. Proper method:
   run `/ask` 5–10 times, discard the first, report the median — the same discipline already
   applied to the eval set.