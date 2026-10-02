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

**`structured-2/`** - plus step 2, hybrid search (`Retrieval:Search = Hybrid`: vector + FTS5 bm25 + vector-within-statement,
fused by reciprocal rank fusion), run 2026-09-30 on 1d's index: main 22/24 (Q10 right, Q21 lost its unit), targeted 7/10 (T5
right, T1 wrong - a lookalike line), routing 2/3 (R1, R2 answered right; R3 wrong - 1d's three were declines), variants 2/3
(V1 right), **held-out 14/15** (H10, H14 right; H8, H15 clean declines). Replay: targeted recall@5 10/10, held-out 13/13,
MRR 0.904; `tools/replay_recall.py` replays these logs from their "(keywords: ...)" lines and reproduces the app's top score
on all 55 questions.

**`structured-2-heldout35/`** - the step 2 code again (no change), on the held-out file after H16-H35 were appended: the
baseline for the fresh questions, run 2026-09-30 before step 5. H1-H15 word for word as in `structured-2/` (repeatability
confirmed). **H16-H35: reliable 16/20**; replay recall@5 15/18, MRR 0.741. Wrong: H25, H29, H34 - each a retrieval miss
(the answer at rank 11, 7, 9); H34 carries the user's decision. Malformed: H20 (prompt v1's "The unit is not stated." as a
decline). All 8 lookalike-line questions, both per-share, both prior-period and the four NFLX thousands passed.

**`structured-5a/`** - plus step 5a, a fixed decline form in the prompt ("The excerpts don't contain <what the question asks
for>."), run 2026-09-30: main 22/24, targeted 7/10, routing 2/3, variants 2/3, held-out 14/15 and **17/20** (H20 fixed).
Every decline clean; T6 and H29 now decline instead of stating a wrong figure; no correct answer lost; retrieval identical
to step 2. **`structured-5a-declined/`** - the first version, which also said "Do not add figures or units to it.": the same
declines, but T10 and H31 dropped "thousand" (targeted 6/10, H16-H35 16/20). Not kept.

**`granite41-3b/`, `granite41-8b/`** - step 0's second-model run: step 5a's code, index and settings with only
`Ollama:ChatModel` changed, run 2026-10-01. Retrieval identical to `structured-5a/`, so every difference is the model.
granite4.1:3b: main 17/24, targeted 5/10, routing 3/3, variants 1/3, held-out 11/15 and 14/20. granite4.1:8b: main 21/24,
**targeted 9/10**, routing 2/3, variants 2/3, held-out 12/15 and 16/20, with 5 `check` answers not yet resolved (Q15, Q16,
R3, H8, H30 - declines that add text). Details in `docs/Decision-Log.md`, "Second-model run". `llama3.1:8b` kept.

**`rerank-2b-spike/`** - step 2b's replay-only spike (`tools/rerank_spike.py` on `structured-5a/`'s logs; no answers
generated): `main.*`/`heldout.*` - MiniLM-L6 and -L12, the chunk with and without the company line, top 25 and 50;
`*-windows.*` - the company line with and without overlapping windows for long chunks, top 25. `.txt` is the report, `.json`
every question's rank per configuration. Chosen: L6 + company line, top 25 - recall@5 64 -> 67 of 68. Details in
`docs/Decision-Log.md`, "Step 2b spike - measured".

**`structured-2b/`** - step 5a's eval settings plus step 2b's reranking (`Retrieval:Rerank`, ms-marco-MiniLM-L6-v2 over each
company's top 25), run 2026-10-01: main **23/24**, targeted **9/10**, routing **3/3**, variants 1/3, held-out **15/15** and
**18/20** - 69/75 against 5a's 64/75; every decline clean. The main set ran in two sessions (`main.log` stopped at 35 of 40
by low memory, `main-rest.log` the other five); `main-merged.log` joins them and is what `main-merged.json` grades. H2 and
T9 carry the user's decisions. `v1_ablation.py` is the excerpt-by-excerpt check behind V1's loss (a knife-edge lookalike:
the answer chunk alone gives the wrong line). Details in `docs/Decision-Log.md`, "Step 2b - full run".

**`answer-side-baseline/`** - A1-A27 (`tools/answer-questions.txt`), their first run, on step 2b's eval build (reranking
on), 2026-10-01: **18/27** - lookalike lines 8/8, per-share 2/2, thousands 4/4, decline 1/1; paid vs declared 0/2, MD&A vs
statement 2/5, arithmetic 3/7. Of the 9 misses only 3 are misreadings; 4 had their answer in hybrid's top 5 and pushed out
by the reranker. `rerank-replay.txt`: the set's retrieval with and without reranking - recall@5 22/26 -> 18/26. Details in
`docs/Decision-Log.md`, "A1-A27 baseline".

**`answer-side-norerank/`** - A1-A27 again on the same eval build with only `Retrieval:Rerank` false (step 5a's hybrid
retrieval), 2026-10-02: **21/27** - +A17, A21, A25, A26; -A8 (answer at rank 5, the quarterly line taken), A27 (wrong
division). A10 and A15 stay wrong with their answers at ranks 3-4 - misreadings. The answer side's baseline from here.

**`rerank-decision/`** - what decided reranking off (user, 2026-10-02), all replay - no answers generated.
`rerank_check.py` / `rerank-check.txt`: the app's logged reranker scores reproduced in Python for A10, A15, A17, A27 (100
pairs, within the log's rounding), with each answer row's place in the 512-token window (A17's is cut at token 510).
`rerank_blend.py` / `blend-{main,heldout,answer}.{txt,json}`: hybrid, reranked, and two RRF blends of the two (k = 60) on all
four sets - recall@5 86, 85, 85, 85 of 94. `bge_screen.py` / `bge-screen.txt`: bge-reranker-v2-m3 on the eight contested
questions - 4/8 in the top 5 like hybrid, ~38 s of CPU per question; its packages and model were removed after the run
(the script's header says how to restore them). Details in `docs/Decision-Log.md`, "A1-A27 with reranking off" onwards.

**`answer-side-screens/`** - two answer-side changes screened before any full run (2026-10-02), on the app's top 5 and
exact prompt sent straight to Ollama, the model unloaded before every call. `step5_prompts.py`: the prompts behind A8, A10,
A14 and A15 and the lines in them that matter. `cash_rule_screen.py` / `cash-rule-screen.*`: one prompt sentence (use the
cash flow statement's line for cash paid/spent questions) - targets A10 and A15 0/2, 10 controls kept. `calculator_screen.py`
/ `calculator-screen.*`: a `calculate` tool through Ollama's tool support - A27 fixed, A24 and A26 lost (figures copied into
the call wrongly). Neither built. Details in `docs/Decision-Log.md`, "Answer side: what the model was given" onwards.

Logs before 2026-10-01 were written in the console's OEM code page (a non-breaking space as a lone 0xFF byte, `’` as
`'`); the app writes UTF-8 since. The tools read both.

Temperature 0 is deterministic here: the baseline's 40 main answers are word-for-word identical to the
2026-09-25 run on the same code, so a changed answer means a changed input, not sampling.

**`v3-retrieval-parity/`** - v3 step 3's oracle: `tools/replay_recall.py`'s output on the three v2 baseline logs
(`structured-5a/` main and held-out, `answer-side-norerank/`), which `RetrievalParityTests` matches rank for rank.

**`v3-runs/`** - v3's evaluation runs (`RagFilingExplorer.Local.Evaluation`, `EvaluationRunTests`): `results/<execution>/`
one stored result per question, `report-<execution>.html` (open it in a browser), `summary-<execution>.txt`; `cache/`, the
model's cached responses, is gitignored. `structured-hybrid-v3-baseline` (2026-10-02): the shipped defaults - main 33/40,
held-out 31/35, answer-side 20/27, every grade the v2 baselines' but A16 (model variation - an unrequested $620M sum).
Details in `docs/Decision-Log.md`, "evaluation in .NET (v3)", step 4.
`report.html` is every execution in the store, the history included: v1's baseline and each kept v2 step, imported from
the folders above (strict grade only - no rank, since their indexes were rebuilt; `HistoricRunImporter`).

