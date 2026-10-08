---
name: run-evaluation
description: Run, read and extend this repo's .NET evaluation (RagFilingExplorer.Local.Evaluation, Microsoft.Extensions.AI.Evaluation) - asking the question sets through the real app and Ollama, grading answers strictly, ranking retrieval, tracing figures, and writing the HTML report under eval/v3-runs/. Use it whenever the user wants to evaluate, test or measure answer quality, run the questions or a subset of them (a smoke run), check whether a code, prompt, setting, model or Ollama change made answers better or worse, compare two runs, measure variance, try the local judge, regenerate the report, read a summary or report, or add a new evaluation question - even if they don't say "evaluation" (e.g. "did my change break anything?", "how does llama do on the held-out set?", "re-run A10").
---

# Running the evaluation

The evaluation asks every question in `tools/*-questions.txt` through the app exactly as it answers at the console
(`AppComposition`), then scores each answer with three deterministic evaluators - no model judges by default:

- **Strict grade** - the expected figure, with its unit, from the exact line; a clean decline where the filings don't say.
  Statuses: `reliable`, `decline-ok` (both pass), `no-unit`, `wrong-unit`, `declined`, `wrong`, `malformed`, `check`
  (a reader decides).
- **Answer rank** - where the expected figure first appears among the retrieved chunks. Ranks 1..GenerationTopK (5) are
  in the model's prompt; a wrong answer with a good rank is a misreading, a missing or late rank a retrieval miss.
- **Figure source** - each figure the answer states, traced to the excerpt and line it came from: `traced`,
  `calculated` (an asked-for sum or ratio), `untraced` (in no excerpt - a possible invention), `no figure stated`.

Each run has a name - the library calls it an *execution*, hence the `Execution` setting. Results are stored per
question under `eval/v3-runs/results/<run>/`, model responses are cached in `eval/v3-runs/cache/` (gitignored), and each
run writes `summary-<run>.txt` and `report-<run>.html`; `report.html` holds every stored run, newest first.

## A full run, step by step

Copy this checklist and work through it:

```
- [ ] Prerequisites checked (Before a run, below)
- [ ] Smoke run on two or three questions passes; its results deleted
- [ ] Full run started detached, progress followed in its log
- [ ] Summary read: grades, ranks, untraced figures, differences from the stored baselines
- [ ] Each difference explained from the report before drawing conclusions
```

## Before a run

Check these first - each one otherwise costs a long run or a confusing failure:

1. **The .NET SDK** pinned in `global.json` is installed (`dotnet --version`).
2. **Ollama is running with the configured models** (`RagFilingExplorer.Local/appsettings.json`: `nomic-embed-text`,
   `llama3.1:8b` by default). Note the Ollama version (`ollama --version`): every result is tagged `ollama:<version>`,
   and an Ollama update can change answers at temperature 0 even with identical code.
3. **The index is built and current.** The evaluation never builds one. If it's missing or stale the run stops with the
   fix; build it by running the app once: `dotnet run --project RagFilingExplorer.Local` (then `exit`).
4. **Nothing else is building or running the app** - a locked DLL fails the build.
5. **Know whether answers will come from the cache.** A fresh clone has no cache, so the first full run asks the model
   all 102 questions - about two hours on a CPU-only machine. Later runs replay cached answers in minutes; only new or
   changed questions are asked.

## Running

Options live in `RagFilingExplorer.Local.Evaluation/evalsettings.json`; override one key for one run with an
environment variable of the same name, `Evaluation__<Key>`. Don't edit the file for a one-off run - the overrides keep
the defaults clean.

| Key | Meaning |
|---|---|
| `ChatModel` | `Local` (default, the app's Ollama model) or `Claude` (`RagFilingExplorer.Claude/claudesettings.json`; needs `ANTHROPIC_API_KEY`, every uncached answer billed; strict grade only) |
| `Graders` | `strict` (default), `judge`, or `both` - the judge is a model; answer rank and figure source always run |
| `Judges` | `equivalence`, `groundedness` (used when `Graders` includes the judge) |
| `Execution` | the run's name; empty = `structured-hybrid-<yyyyMMddTHHmm>`. Reusing a name overwrites the questions asked again and keeps the rest (the summary lists them) - use a new name to keep runs apart |
| `Sets` / `Only` | which questions: sets `Main`, `HeldOut`, `AnswerSide`; or ids like `Q1,A16` |
| `NoCache` | `true` = ask the model afresh and cache nothing (a variance pass) |
| `UnloadEachQuestion` | unload the model before each question; needs `NoCache` |

**Always smoke-test first** with two or three questions and a throwaway name, then run in full:

```bash
# bash
Evaluation__Only=Q1,A16 Evaluation__Execution=smoke dotnet test RagFilingExplorer.Local.Evaluation --filter "FullyQualifiedName~EvaluationRunTests"
```

```powershell
# PowerShell - set, run, then clear, so the override doesn't leak into later runs in the same shell
$env:Evaluation__Only='Q1,A16'; $env:Evaluation__Execution='smoke'
dotnet test RagFilingExplorer.Local.Evaluation --filter "FullyQualifiedName~EvaluationRunTests"
Remove-Item Env:Evaluation__Only, Env:Evaluation__Execution
```

The evaluation tests are `[Explicit]`, so the `--filter` is what runs them; plain `dotnet test` runs only the offline
checks. A smoke run's results stay in the store and in `report.html` - delete `eval/v3-runs/results/smoke/` afterwards.

**A full uncached run takes about two hours.** In Claude Code a single tool command stops long before that, and a
sleeping machine pauses the run - so start it detached and check its progress instead of waiting on it. On Windows:

```powershell
New-Item -ItemType Directory -Force eval/v3-runs/logs | Out-Null
Start-Process cmd -WindowStyle Minimized -ArgumentList '/c dotnet test RagFilingExplorer.Local.Evaluation --filter FullyQualifiedName~EvaluationRunTests --logger "console;verbosity=detailed" > eval/v3-runs/logs/run.log 2>&1'
```

The redirect goes through `cmd`: Windows PowerShell 5.1's `*>` writes the log as UTF-16 and turns stderr lines into
errors. `eval/v3-runs/logs/` is gitignored. On macOS/Linux use `nohup ... > eval/v3-runs/logs/run.log 2>&1 &`. Each
question writes one progress line (grade, rank, figure source, seconds); environment overrides set in the shell that
starts the run are inherited by it.

Two scripts wrap the longer measurements (Windows PowerShell; read their headers for parameters):
- `tools/run-variance.ps1` - the questions asked afresh N times, then compared question by question with a reference run you name.
- `tools/run-judge.ps1` - the local judge over cached answers, then its agreement with the strict grade.

## Reading the results

Start with `eval/v3-runs/summary-<run>.txt`: per set, reliable count and statuses, answers with the figure in the
top 5, figure sources; every untraced figure; and every grade that differs from the stored baselines - the graded v2
runs each question set is compared with (`eval/structured-5a/`, `eval/answer-side-norerank/`).

A difference from the stored baselines is a **warning, not a failure** - read it before concluding anything:
- Different Ollama build, hardware or model quantization than the stored baselines? Answers can change with no code
  change. The stored baselines were answered on a CPU-only machine; compare like with like. To measure your own change, run once
  **before** it and once after, on the same machine and Ollama build, and compare those two.
- Same build, same machine? Then a changed grade is a changed input - look at the code, prompt or setting you changed.

To see why an answer failed, open `report-<run>.html` in a browser. Each case shows the strict grade's reason in
words (what it stated vs. what was expected, and what any trap figure is), where the expected figure was (a line in an
excerpt = misreading; derived from given figures = calculation; in no excerpt = retrieval miss), the prompt the model
was given, and tags (kind, company, statement routed to, Ollama build) to filter by.

To compare two runs question by question (wording, figures, grade), set `Evaluation__Compare=<run1>,<run2>` and
`Evaluation__CompareName=<name>` and run `--filter "FullyQualifiedName~VarianceComparisonTests.Compare_Executions"`;
the comparison is written to `eval/v3-runs/<name>.txt`.

## The local judge

`Graders=both` adds Microsoft's Equivalence evaluator, scored by the chat model itself. On this project's answers it
rates similarity, not the exact figure - it passed most of the strict grade's wrong figures - so treat it as a second
view, never as the grade. It needs a larger context window, which the evaluation sets for the judge's calls only.
Groundedness is far slower (~100 s a call on CPU); screen it on a few questions (`Only`) before a full run.
`JudgeAgreementTests.Report_Execution` (with `Evaluation__JudgeExecution=<run>`) writes the agreement report.

## Regenerating reports

After changing how results are written, or after deleting a run, rewrite the HTML from the stored results without
asking the model: `dotnet test RagFilingExplorer.Local.Evaluation --filter "FullyQualifiedName~ReportWriteTests"`.

A run replayed from the cache is re-tagged with the Ollama build that's running now, though the answers came from the
build that first produced them. If you refresh a stored run from the cache, put its original `ollama:<version>`
tag back in its `results/*.json` afterwards, or later comparisons will name the wrong build.

## Adding a question

1. Add its text, on one line, to its set's file in `tools/` (`manual-questions.txt`, `heldout-questions.txt`,
   `answer-questions.txt`), and its source in `docs/Manual-Test-Questions.md`.
2. Add its entry to `tools/expected-answers.json`, matched by the exact question text: `id`, `kind` (`figure`, `fact`,
   `negative` - the filings don't say, `routing`), `expect` and `unit` (what's graded); `chunk_expect` - the answer as
   the chunk prints it, e.g. `"(26,445)"`, or every input of an asked-for calculation and not its result; `traps`, each
   with a `trap_why`; and `conflicts` / `accept` for a lookalike line. A negative has no `chunk_expect`.
3. Run `dotnet test` - `ExpectedAnswersTests` checks the entry is complete; a gap would make the report quietly wrong.
4. Run just the new question (`Evaluation__Only=<id>`); every other answer comes from the cache.

## When something goes wrong

- **"... doesn't exist - build it by running the app once"** or a stale-index message: build or `--rebuild` the index.
- **Ollama connection or model errors**: start Ollama, `ollama pull` the model the message names.
- **evalsettings.json validation error**: it names the bad key - an unknown grader, judge or set, `UnloadEachQuestion`
  without `NoCache`, a judge with `NoCache` (the judge's larger context window would reload the model around every
  answer - judge cached answers), or a judge asked for with no `Judges` listed.
- **"Only names ..., which isn't a question of the sets run"**: a mistyped id, or an id from a set the run excludes.
- **The run stopped part-way**: questions already answered are cached; re-running the same questions with the same
  `Execution` resumes cheaply and overwrites the partial run.
