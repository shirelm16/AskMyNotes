# How to evaluate an AI feature — the procedure

Written 2026-07-29, after building the retrieval eval. This is the transferable part:
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
against the same index returns the same chunks every time. It still needs a rate, because
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

Five bugs in the scoring code this session. Zero in the retrieval code. Measurement code
gets less scrutiny because there is nothing to compare it against.

**Before looking at a number, work out roughly what it should be. Then be suspicious.**

Both bugs caught from the number alone:
- MRR came back exactly `0.6`. There were 9 rank-1 questions out of 15. `9/15 = 0.6`
  → every non-rank-1 question scored zero → `1 / rank` was integer division.
- Ranks got *worse* after dedupe. Removing entries from a sorted list can only promote
  what remains — worse ranks are impossible → it was not filtering, it was reordering.
  (`SelectMany` over a Dictionary walks file by file and destroys the distance order.)

### How to know roughly what a number should be, before looking

Four moves. All are arithmetic done *before* reading the result.

**1. Work out the floor and the ceiling.**
9 questions were known to be at rank 1, each contributing `1/1 = 1.0`. So MRR was at
least `9/15 = 0.60` before anything else counted. Q5 and Q11 at rank 2 add `0.5 + 0.5`,
lifting the floor to `10/15 = 0.667`. The result came back at **exactly 0.60** — the
absolute floor, reachable only if every question below rank 1 scored zero.
**Landing exactly on a bound is nearly always a bug.** Real numbers land in between.

**2. Work out what random guessing gives.**
132 files; picking 5 at random gives roughly `5/132 ≈ 4%`. So 4% = the system does
nothing, 73% = it works. If the first run had come back at 8%, the problem is not
chunking — it is that the vectors are not being compared at all. Compute this once at
the start; it tells you which kind of problem you have.

**3. Ask which directions are impossible.**
Some changes can only move a number one way — decide that before running. Dedupe removes
entries from a distance-sorted list, and a file's rank comes from its best chunk, which is
never removed. So ranks could only improve. **Ranks getting worse was impossible**, which
proved a bug existed without knowing anything about the code. When the impossible happens,
the operation is not what you think it is: this was reordering, not filtering.

**4. Derive the number a second way, from different data.**
The index held 161 chunks. Separately: one 23KB report at 500-char chunks is ~46 chunks by
itself. Across 132 files, 161 is off by roughly 10x. Two independent routes to one quantity;
when they disagree, one is wrong.

Checklist for the harness itself:
- [ ] Verify 2 items by hand against raw output after every harness change
- [ ] Off-by-one: `@5` means ranks 1–5. Check the boundary explicitly.
- [ ] Integer division in any `1 / n`
- [ ] Any accumulator declared outside the loop it belongs to
- [ ] Path/ID comparison: normalize both sides, check direction (`a.EndsWith(b)` is not `b.EndsWith(a)`)
- [ ] "Not found" encoded as something unmistakable — not `0`, which reads as a position

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
