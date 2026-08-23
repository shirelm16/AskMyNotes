# How to evaluate an AI feature — the procedure
This is the transferable part:
the steps, not the specifics of this project.

## Every eval has the same four parts

1. A **dataset** of inputs
2. The **expected** output, or criteria for one
3. A **scoring function**
4. An **aggregate number**

Only #3 changes between tasks. Everything else is boilerplate written once.

| Feature | What the scorer checks | Needs an LLM to score? |
|---|---|---|
| Retrieval / RAG | Is the expected doc in the top k? | no |
| Classification | Does the label match? | no |
| Extraction | Do the fields match? | no |
| Structured output | Does it parse? Match the schema? | no |
| Tool / function calling | Right tool? Right arguments? | no |
| Summarizing, chat, writing | Is it *good*? | yes |

Most of that column is "no". Reach for an LLM judge last, not first.

## Why an eval measures a rate instead of asserting

A unit test asserts: this input must give exactly this output, every time, or the build
fails. That works when there is one correct answer and the code is predictable.

Two separate things break it, and they are worth keeping apart:

- **There is more than one right answer.** Several documents can genuinely answer the
  same question, so "the output must equal this" is the wrong shape to begin with. You
  label a list of acceptable answers and count how often one of them turns up.
- **The system is random.** Above temperature 0, the same input gives different output
  from one run to the next, so a single result proves nothing either way.

Retrieval here has only the first problem. It is entirely predictable — the same question
against the same stored chunks returns the same results every time. It still needs a rate, because
"correct" is a list rather than a single value, and because the useful question is not
"is it right" but "how often is it right, and is that number moving".

Generation has both problems at once, which is why measuring answer quality is a harder
job than measuring retrieval.

## Order of work

1. **Build the eval before tuning anything.** Otherwise every change is a guess.
2. **Write the golden set yourself, without looking at the source material.**
   Questions written *after* reading the notes are easier to find than real ones,
   and the score comes out inflated.
3. Label with a **list** of acceptable answers, not one. Pretending there is exactly
   one right document costs accuracy.
4. **One change. Re-run. Write it down. Then the next change.** Batching changes means
   never knowing which one mattered — and no idea what to revert when the number drops.
5. **Log per-item results, not just the total.** The aggregate hides everything.

## Trust the eval last, not first

Measurement code gets less scrutiny than the code being measured, because there is nothing
to compare it against. When retrieval is wrong you can read the results and see it. When
the scorer is wrong it prints a number, and a number looks like a number.

Building this one, every bug that turned up was in the scoring code. None were in
retrieval — which is the opposite of where the attention naturally goes.

**Work out roughly what the number should be before you look at it. Then be suspicious of
it.** Two things that catches:

- **A number that is too exact.** Mean reciprocal rank came back as precisely 0.6, and 9
  of the 15 questions were at rank 1 — and 9/15 is 0.6. That can only be true if every
  question below rank 1 contributed nothing, which meant `1 / rank` was integer division.
- **A change in an impossible direction.** A step was added that only *removes* results —
  it dropped the extra chunks that came from the same file, keeping at most three of each.

  Removing things can only help whatever survives. If the correct answer sat 7th and two
  results above it were dropped, it becomes 5th. There is no way for it to end up further
  down the list.

  That argument needs one condition, and it is worth checking before relying on it: the
  correct answer has to survive the removal. Here it always did — the cap keeps each file's
  three *nearest* chunks, and the score is measured per file rather than per chunk, so a
  file that was in the results at all was still in them afterwards. Had the cap been able
  to drop the only chunk that mattered, a worse rank would have been perfectly possible and
  this whole line of reasoning would have proved nothing.

  The measured ranks got worse anyway. Since dropping alone cannot do that, the step had
  to be doing something else as well — and it was: it grouped the results by file and then
  flattened them back into a list, so the output came out ordered file by file instead of
  by relevance. The filtering was correct. The order it returned them in was not.

### How to know roughly what a number should be, before looking

Four moves. All are arithmetic done *before* reading the result.

**1. Work out the floor and the ceiling.** From what you already know, what is the lowest
the number could be, and the highest? If nine questions are known to sit at rank 1, each
contributes a full 1.0 to mean reciprocal rank, so the total cannot come out below 9/15 =
0.60 no matter how badly the other six do. Then: **a result landing exactly on a bound is
nearly always a bug.** Real numbers land somewhere between. This one came back at precisely
0.60, which was only reachable if every question below rank 1 had contributed nothing at all
— and that is what a `1 / rank` integer division does.

**2. Work out what guessing would score.** A score means nothing on its own until you know
what a system with *no skill* would get. That number is arithmetic, not an experiment — you
work it out once, on paper, before running anything.

Say each question has one correct file among 130, and the system returns 5 files. Picking 5
at random finds the right one about `5/130`, roughly 4% of the time. That is the score of a
system doing nothing whatsoever.

Then run the eval once, and hold your result up against it:

| result | What it means |
|---|---|
| ~4% | The system is not working. Not badly tuned — **not working.** |
| far above 4% | The system works. What is left is a tuning problem. |

The distinction matters because those two need completely different work. A score close to
guessing is a structural bug: embeddings never stored, the wrong column compared, 
an empty table because nothing was ever ingested, the question vector never reaching the query. 
Chunking and reranking cannot rescue it and weeks can be lost trying. 
A score well clear of guessing means the retrieval path is sound and the tuning is worth doing.

Without the baseline, "7%" looks like a tuning problem. With it, "7%" is a bug report.

**3. Ask which directions are impossible.** Some changes can only move a number one way, and
deciding which before you run turns a surprise into a proof. When something moves in a
direction that cannot happen, the operation is not doing what you think it is — see the
example above, where a step that only removed results somehow made ranks worse.

**4. Derive the number a second way, from different data.** Work the same quantity out by a
route that shares nothing with the first, then see whether the two agree.

The ingest reported that it had stored **161 chunks**. On its own that number looks fine.
There is nothing about "161" that announces itself as wrong, which is exactly the problem
with reading a number the system hands you.

So work it out from the other end, using facts the ingest had no part in. One report in the
folder is 23 KB. Chunks run to a few hundred characters, so call it 500 — that single file
should produce something like **46 chunks on its own**. And there were over a hundred files.
Even if most are far smaller, the total ought to be in the thousands.

161 against thousands is not a rounding difference. It meant most of the text was never
becoming chunks at all. The reported count could never have revealed that, because it was
produced by the very code that was losing the text.

Checklist for the scoring code itself — the bugs that hide behind a plausible number:

- [ ] **Check two results by hand** against the raw output, after every change to the harness.
- [ ] **Boundaries.** If a measure says "the top 5", confirm whether the 5th is inside or
      outside. An off-by-one in a cutoff changes every score without ever looking wrong.
- [ ] **Division.** Where whole numbers divide into whole numbers, anything below 1 becomes 0.
      Check every division that is supposed to produce a fraction.
- [ ] **Totals in the wrong scope.** A counter that should reset for each item, or should not,
      and does the opposite.
- [ ] **Comparing identifiers.** Normalise both sides — case, separators, absolute against
      relative — and check the direction: "a ends with b" is not "b ends with a".
- [ ] **"Not found" must be unmistakable.** Zero is already a position, so it cannot also mean
      absent. Where it does, missing results quietly average in as if they were results.

## Reading the results

- **A score going down can mean the eval got more honest.** It went 12 → 11 here while
  two genuine defects were being fixed.
- **Items sitting near the cutoff flip on any change.** Every question that flipped was
  at rank 3. That is shuffling, not quality. Rank-1 results never moved.
- **Keep correct fixes regardless of the score.** Reverting a real fix because a coarse
  measurement wobbled is how you get a broken system that scores well.
- **If the ruler cannot resolve the changes being made, fix the ruler first.**
  Yes/no metrics (hit-rate) are blunt; rank-based ones (MRR) move gradually.
- **Ask whether the change improved the system or just the metric.** Deduplicating by
  source raises "how many different files are in the top 5" — which is exactly what a
  file-level metric counts. The number rises before any question of better matching.

## Know what the metric does not cover

State the limits out loud; they are not weaknesses, they are the scope.

This eval checks the right **file** came back — not the right **chunk**. A file can score
a hit on a paragraph unrelated to the question. It also never looks at the generated
answer, so it says nothing about whether the output is any good.

Small sets are noisy: 15 questions means one flip = 6.7%. Treat single-item swings as
leads to investigate, not proof.
