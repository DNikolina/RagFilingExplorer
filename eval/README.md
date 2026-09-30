# Evaluation runs

Each folder is one measured run of the app, kept so later versions are compared against real answers rather
than remembered scores.

- `*.log` - the app's `--verbose` output for one question file (`tools/manual-questions.txt` -> `main.log`,
  `tools/heldout-questions.txt` -> `heldout.log`). `tools/replay_recall.py` reads the filters from it.
- `*.json` - `tools/grade_answers.py --json` on that log, per question: status, note, answer. Answers the
  grader marked `check` carry the reader's decision (`status`, `resolution`, `resolved_by`) and the grader's
  own call (`graded`).

**`baseline-v1/`** - tag `v1.0` code (Markdown strategy, prompt v1, `llama3.1:8b`, temperature 0), run
2026-09-28. Main set reliable 22/24, targeted 4/10, routing 3/3, variants 1/3; held-out 8/15. Details and
findings in `docs/Decision-Log.md`, "XBRL hybrid (v2)", step 0. `linearized-heldout.*` is the same code on the
Linearized strategy, held-out only: 11/15 - the reference for the Structured strategy's steps (its main-set
reference is the 2026-09-25 prompt-v1 Linearized run: 22/24, targeted 5/10).

**`structured-1c-early/`** - the Structured strategy after step 1a and 1c's two early table changes (fallback
tables as text rows, text-table column names on every piece), run 2026-09-28: main 22/24, targeted 5/10,
routing 3/3, variants 1/3 - the same as Linearized; held-out 10/15 against Linearized's 11/15 (H11). H10 carries
the user's decision on the identical Linearized answer.

**`structured-1b-ii/`** - plus step 1b-ii's filing-profile chunk ("Cover Page", from the tagged cover facts), run
2026-09-28: main 22/24 (unchanged), held-out **11/15** (H11 fixed). H10 carries the earlier decision on the same answer.

**`structured-1b-iii-a/`** - plus the block model and step 1b-iii-a (statement type from the filer's Statement roles,
not title patterns), run 2026-09-29: main 22/24, held-out 11/15 - 39 of 40 main and 15 of 15 held-out answers
word for word the same as 1b-ii (Q21 moved a full stop). H10 carries the earlier decision on the same answer.

**`structured-1b-iii-b/`** - plus step 1b-iii-b (each note to the financial statements a section of its own, headed
by its topic from the filer's Disclosure roles), run 2026-09-29: main 22/24, **targeted 7/10** (T3 and T6 fixed, T5 a
decline in a different form), routing 3/3, variants 1/3, held-out 11/15. H10 declines again, now as "The unit is not
stated." - graded declined by the grader, no reader decision needed.

**`structured-1b-iii-c/`** - plus step 1b-iii-c (roll-forward rows labelled with their period from the XBRL contexts),
run 2026-09-29: main 22/24, targeted 7/10 - T9 still wrong ("$0"; the labelled row was in the model's context).
Held-out 11/15, every answer word for word as in 1b-iii-b.

**`structured-1c-a/`** - plus step 1c-a (fiscal-year names on period labels, "fiscal 2025, year ended May 31, 2025"),
run 2026-09-29: scores unchanged (main 22/24, targeted 7/10, held-out 11/15); T9 now answers from the right row but
gives its per-share figure ($1.70) instead of the $4,743M total. Every other answer as in 1b-iii-c.

**`structured-1c-b-declined/`** - step 1c-b (one table of figures per chunk), run 2026-09-29 and **not kept** (code
reverted to 1c-a): scores unchanged; H6 wrong -> declined, T4's table rank >8 -> 6, but replay MRR fell (targeted
0.492 -> 0.425, held-out 0.498 -> 0.408) and T2 took the wrong period from MD&A prose that outranked its table.
Kept as the record of the decision (docs/Decision-Log.md, "Step 1c-b measured and declined").

**`structured-1d/`** - plus step 1d (each chunk's embedding text opens with "Oracle Corporation (ORCL), Form 10-K for
fiscal year 2026." from the cover facts), run 2026-09-29 on 1c-a: main 22/24 (Q15 carries the user's decision - a
correct decline the grader's pattern misses), targeted 7/10 (T4 right, T6 declined), held-out 10/15 (H6 right; H8,
H15 became prompt v1's "The unit is not stated."; H10 declined, user's decision). Replay: targeted recall@5 9/10, held-out
11/13 - the experiment's `co` variant exactly.

Temperature 0 is deterministic here: the baseline's 40 main answers are word-for-word identical to the
2026-09-25 run on the same code, so a changed answer means a changed input, not sampling.
