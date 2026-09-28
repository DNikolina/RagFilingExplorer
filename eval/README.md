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
findings in `docs/Decision-Log.md`, "XBRL hybrid (v2)", step 0.

Temperature 0 is deterministic here: the baseline's 40 main answers are word-for-word identical to the
2026-09-25 run on the same code, so a changed answer means a changed input, not sampling.
