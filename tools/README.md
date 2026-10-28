# tools/

What each file here is for. How to run one is in its own header comment (`<# .SYNOPSIS #>` for PowerShell), kept
next to the code it describes; run everything from the repo root.

## The evaluation's inputs

Read by `RagFilingExplorer.Evaluation` - changing one changes what the evaluation asks or how it grades.

| File | What it is |
|---|---|
| `manual-questions.txt` | The `Main` set: the manual pass's questions and the targeted, routing and variant ones (Q, T, R, V) |
| `heldout-questions.txt` | The `HeldOut` set (H): never tuned against - no change is made because of one of its answers |
| `answer-questions.txt` | The `AnswerSide` set (A): lookalike lines, per-share vs total, rounding, units, calculations - a change may be chosen on these |
| `expected-answers.json` | Every question's expected figure or fact, keyed by its exact text; sourced in [docs/Manual-Test-Questions.md](../docs/Manual-Test-Questions.md) |

Adding a question: the run-evaluation skill (`.claude/skills/`) has the steps.

## To run

| File | What it does |
|---|---|
| `run-variance.ps1` | Asks the questions afresh several times, no response cache, and compares the passes with a reference run |
| `run-judge.ps1` | The local judge (Microsoft's Quality evaluators) over the cached answers, and its agreement with the strict grade |
| `eval-common.ps1` | Helpers the two scripts above share - not run on its own |
| `vector_map.py` | An index's chunk vectors drawn in 2D as an HTML page, close chunks close together ([Decision-Log.md](../docs/Decision-Log.md), "a map of the chunk vectors") |

## Kept as the record, not maintained

The Python originals the .NET evaluation was ported from. The evaluation is the source of truth: a grading rule
changes there, not here, and `replay_recall.py` mirrors v2's retrieval, so it no longer reproduces the app.

| File | Ported to |
|---|---|
| `grade_answers.py` | The strict grade (`StrictGrader`) |
| `replay_recall.py` | The answer rank (`RetrievalRankEvaluator`) |

## Spikes

Measured once to make a decision; the results are in the Decision-Log.

| File | Measured | Decision-Log.md |
|---|---|---|
| `rerank_spike.py` | Cross-encoder reranking, replayed over the app's hybrid candidates | "XBRL hybrid (v2)", Step 2b spike |
| `LinearizeSpike/` + `xbrl_column_check.py` | Tables linearized to rows, checked column by column against the filings' inline XBRL | "linearized tables as a second chunking strategy" |
