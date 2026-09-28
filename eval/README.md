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

Temperature 0 is deterministic here: the baseline's 40 main answers are word-for-word identical to the
2026-09-25 run on the same code, so a changed answer means a changed input, not sampling.
