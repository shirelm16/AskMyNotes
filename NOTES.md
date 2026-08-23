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
     walk *.md under the configured folders          embed the question
                       |                                      |
        strip the metadata header                   vector search, LIMIT 100
                       |                              (cosine distance)
        SplitIntoUnits  <- structure-aware                    |
                       |                            cap 3 chunks per source file
      PackUnits(min 150 chars, max 1000 chars)                |
                       |                              LLM reranker, 0-10
             embed in batches of 100                          |
                       |                            top chunks -> context
      replace all rows in one transaction                     |
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

Later tuning took it to 15 of 15, with the correct source usually landing first.

### Two-stage retrieval: cheap recall, then expensive precision

Vector search runs first, `LIMIT 100`. Its job is *recall*: out of every chunk in the table, get
the right one somewhere into a pool of a hundred. It is the cheap stage — 482ms in the measured
breakdown, and that is without an index today (limitation 1).

The reranker runs second, over those candidates — up to 100 of them, fewer once the per-source
cap below has dropped some. Its job is *precision*: decide which of the survivors actually
answer the question. It scores them in batches of 18, four batches in flight at once.

Both stages are needed, for opposite reasons:

- **The reranker alone doesn't scale.** It reads each passage with an LLM, so its cost grows
  with the number of passages. Scoring a hundred already takes ~3.9 seconds — 48% of a request.
  Scoring the whole collection per question is not an option, and gets worse as notes are added.
- **The vector search alone isn't precise enough.** Similarity is *topical*. It happily returns
  passages that discuss the subject without containing the answer, and it cannot tell the
  difference.

So the database discards almost everything, cheaply and imprecisely. The reranker then orders
what is left, carefully and expensively. Neither could do the other's job.

### Per-source capping

```csharp
var cap = 3;   // at most 3 chunks from any one file
```

Over-fetch 100, then keep at most three chunks per source file. Without this, one long,
verbose file can occupy most of the top results and leave no room for the file that actually
holds the answer. It costs nothing and protects against a single document dominating.

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

### Ingest replaces everything, atomically

`TRUNCATE` then `COPY`, both inside one transaction.

**Why a transaction.** `TRUNCATE` empties the table, so between it and the reload there is a
moment with no searchable notes at all. If the load failed halfway — a dropped connection, one
bad row — that is where it would stay. Wrapping both means nothing is visible until the commit:
other connections keep reading the old rows throughout, and a failure anywhere leaves the old
set untouched. Either the new set is fully there or the old one still is; never neither.

**Why `COPY` rather than `INSERT`.** An insert per row is a network round-trip and a statement
parse per row. `COPY` streams every row through one operation instead.

**Why binary rather than text.** Each embedding is 1536 floats. Written as text, one float is
about 12 characters, so a single row's vector is roughly 18 KB that Postgres then has to parse
back into 1536 numbers. In binary it is 4 bytes per float — about 6 KB — and no parsing at
either end. Text is a wasteful way to move numbers.

Embeddings themselves are generated 100 at a time, for the same reason: fewer round-trips.

### Latency: 30.6s → 8.2s, measured

`/ask` logs `embed | query | rerank | generate | TOTAL` on every request, using one stopwatch
with snapshots so the parts sum exactly to the total.

The first working version answered in **30.6 seconds** — unusable. Instrumenting the stages
showed the reranker was almost all of it, scoring candidates one call at a time. Batching it
(18 candidates per call, 4 concurrent, with a per-batch fallback to distance order if the
returned score count doesn't match) brought it to **8.2 seconds**. Retrieval quality held at
15 of 15 questions, with the correct source usually first — the speed-up cost nothing in
accuracy, which is only knowable because the test set was there to check it against.

A breakdown, measured after the first request — the first one is slower, because the connection
pool, the JIT and the TLS session all start cold (limitation 8):

```
embed 1360 | query 482 | rerank 3892 | generate 2434 | TOTAL 8168 ms
   17%          6%          48%           30%
```

**The useful conclusion: the floor is about 3.8 seconds.** Two OpenAI round trips — embedding
the question, generating the answer — are unavoidable in this design, and together they are
`1360 + 2434 ms`. No amount of tuning the parts in between gets under that.

Going lower means changing the design rather than the settings: embed locally instead of calling
out, cache answers for repeated questions, or stream the answer so the reader sees the first
words while the rest is still being generated — which changes perceived latency without changing
the total at all.

### A change that lowered the score and was kept anyway

One run stripped the metadata header off the top of each note before chunking. That was plainly
the right thing to do — the header is filler, not content. The score dropped anyway.

It was kept, but only because the cause was found first. Removing the header freed about 230
characters in one file, which was just enough for all four of its numbered items to pack into a
single chunk instead of getting one each. The item that answered the question lost its own
dedicated chunk and fell from 1st place to 19th.

Finding it meant querying the database directly and seeing that chunk sitting at rank 19. The
instinct at that point is to ask what the chunks above it have that it doesn't — to treat the
ones that beat it as the thing that changed. They hadn't changed at all.

**A result can lose its place two ways, and they look identical in the ranking: its rivals got
better, or it got worse.** Here it was the second. The chunk had stopped being about one thing
and become a chunk about four, only one of which the question was asking about — and that
dilution is what dropped it. Looking upward at the winners would never have shown that.

"The number went down but the change was good" is exactly how bad changes survive, so it only
counts when the mechanism can be named.

---

## 3. The evaluation harness

A golden set of 15 questions, each labelled with the source file that should be retrieved. For
each question the harness runs the real retrieval path, finds the rank of the first correct
source, and reports:

- **hit-rate@5** — of the 15 questions, how many had a correct source somewhere in their top 5
  results. Pass or fail per question, with nothing in between.
- **mean reciprocal rank** — how *near the top* the correct source landed, averaged across the
  questions. Rank 1 scores 1, rank 2 scores 0.5, rank 4 scores 0.25. It separates "found it
  first" from "found it fourth", which hit-rate@5 treats identically.
- **the rank of every individual question**, not just the totals — the part that turned out to
  matter most.

**The method:** one change per run. Re-ingest, re-measure, write down the result — including the
runs that made things worse and the ones that changed nothing.

The questions and the run log are not in this repository. The set was labelled against my own
notes, so the questions quote personal content; [`eval/PLAYBOOK.md`](eval/PLAYBOOK.md) is the
procedure, which is the part that transfers anyway.

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
block at the top of each note was being stored and searched as if it were part of the note's content.

**Only change one thing at a time — including the data.** In one run the notes themselves had
been edited between measurements, so the two numbers described different collections and could
not be compared. Recorded as such, rather than being credited to the change under test.

**Know how precise your own measurement is.** With 15 questions, one question is 6.7% of the
score — and that is the smallest step the measurement can take. There is no finer reading than
"one more question got it right".

The reranker returns a whole number from 0 to 10, so across a hundred passages there are ties everywhere — 
many passages end up sharing a score. Those ties are settled by vector distance, where the gaps run to the 
third or fourth decimal place. So whether a passage finishes 5th or 6th is sometimes decided by a
difference far too small to mean anything. Only 5th counts.

That is why one question moving is a lead rather than a result. It might be a real improvement,
or it might be two passages that were nearly identical simply landing in the other order — and
15 questions cannot tell the two apart.

More questions would not fix that case. Every question still sits on the same edge. What more
questions change is what the *total* can tell you: an accidental flip is as likely to fall one
way as the other, so across a large set they largely cancel out, while a change that genuinely
helps pushes many questions in the same direction. At 15 there is nothing to average, so one
real improvement and one accident look the same. At 150 a real effect shows up as a pattern and
an accident stays a single stray.

For one particular question, though, no size of set settles it. Only the mechanism does — which
is exactly what the metadata header case above needed.

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