# Implementation Plan: SEC 10-K RAG Tool (Phase 1 — Local/Ollama)

A fully local, zero-cost RAG tool over public SEC 10-K filings. **Phase 1 only** — a complete,
shippable project on its own; there is no assumed follow-up phase.

This file is the **current-state reference**: ground rules, what the pipeline is, the constraints that
would cause a silent regression if forgotten, and the facts already validated. The full history — every
step's outcome, every dead end, every bug and how it was found — is in
[Decision-Log.md](Decision-Log.md), verbatim and in order. Read the relevant section there before
revisiting any decision summarized here.

---

## Ground rules

1. **Correctness over speed.** This is explicitly not a race. Do not skip steps or rush chunking/
   retrieval quality to "finish faster." If something needs more time to do right, take it.
2. **Preview packages are expected, not a bug.** Don't treat a preview package's rough edges as
   something to silently work around — try it, and if it's genuinely not working, fall back and say so
   (as was done with `Microsoft.Extensions.DataIngestion` in Step 3).
3. **Report at decision points** (e.g. falling back from a planned library, changing chunking)
   rather than silently switching approach — a one-line note is enough, this isn't a permission gate.
4. **Verify against real output, not summary stats.** Nearly every real bug in this project (the
   statement-type false positives, the lost row-group labels, the NFLX encoding corruption) was found by
   reading actual chunk output or querying the index (`rag.<strategy>.db`) directly, not by trusting that code looked right.

---

## Prerequisites (already complete)

- .NET SDK `10.0.400`, pinned via `global.json`
- Ollama installed and running, with `nomic-embed-text` (274MB) and `llama3.1:8b` (4.9GB, the shipped
  default chat model) pulled. `qwen3.5:2b` is also pulled as a reference reasoning model — not shipped.
- Python 3.12 + `pip install markitdown` — the app shells out to the `markitdown` CLI for HTML→text
  (see Decision-Log.md, Step 3, for why). This makes the project "zero cost", not "zero setup".
- Hardware: 12th Gen Intel i7-12800H, 32GB RAM, Intel UHD integrated graphics — **CPU-only inference,
  no GPU acceleration available.** This is why `llama3.1:8b` was chosen over larger local models.

---

## Status

| Step | Status |
|---|---|
| 1. Source data | Done — MSFT, ORCL, NDAQ 10-Ks from EDGAR; NFLX added later |
| 2. Project setup | Done |
| 3. Chunking | Done — hand-written pipeline; MEDI tried and abandoned |
| 4. Vector storage | Done — SqliteVec-persisted, one index per chunking strategy (`rag.<strategy>.db` + build manifest) |
| 5. Retrieval | Done — with company + statement-type metadata filtering |
| 6. Answer generation | Done — citation-grounded prompt, reasoning-model support |
| 7. Testing | Done — 6/6 on the Step 7 questions; manual pass graded strictly (unit + exact line): 22/24 reliable on both strategies; targeted T1-T10: Markdown 4, Linearized 5; 406 offline unit tests (176 at v1.0) + 172 offline evaluation tests |
| 8. Publish | Done — pushed and tagged `v1.0` (2026-09-28) |

**Completion checkpoint:** once the manual pass is done and the repo is pushed, **v1** is complete -
report that clearly. The only further work is v2, the XBRL hybrid the user asked for (2026-09-25), built on
a branch in measured phases - nothing else is assumed or owed. Decision-Log.md, "XBRL hybrid (v2)".
**v2** is complete too (tag `v2.0`, 2026-10-02). **v3 (user, 2026-10-02)** moves the evaluation into .NET with
`Microsoft.Extensions.AI.Evaluation` - the strict grader and the retrieval rank as custom evaluators, reporting and
response caching - on a branch `v3`, each step reproducing the Python tools exactly before the next. The app and its
defaults don't change. Plan and bars: Decision-Log.md, "evaluation in .NET (v3)". **v3 status (2026-10-02, branch `v3`):** steps 1-4 done - the strict grader (1,029/1,029 graded answers) and retrieval
rank (102/102) ported and held exact by parity tests; a full evaluation run on the defaults reproduced v2 (101/102 grades;
A16 model variation, user's call); `eval/v3-runs/report.html` holds v1, every kept v2 step and v3, newest first (it opens on the first). **Step 5
done (2026-10-05):** `FigureSourceEvaluator` traces each stated figure to the prompt excerpt and line holding it - on the
v3 baseline no untraced figure, A10 traced to the equity statement's line, every wrong answer a misreading of a figure in
its context. **Variance measured (2026-10-05):** two fresh passes identical to each
other (102/102); against the baseline 100/102 grades - the baseline was answered on Ollama 0.35.0, the passes on 0.35.1
(llama.cpp bumped). Compare runs only on the same Ollama build. **Step 6 (local judge) measured, kept alongside:** Equivalence
by `llama3.1:8b` passes 14 of 18 wrong answers, so the strict grade stays the grade; `Graders` in `evalsettings.json`
chooses strict, judge or both. Groundedness screened (18 answers): fails 0 of 11 wrong ones - not run in full. Decision-Log, "Variance measurement - done", "Step 6 - the local judge".
**Closing v3 (2026-10-06):** code comments reviewed against the code and rewritten to the new comment rule (CLAUDE.md);
the .NET evaluators made the source of truth, the Python tools kept as the record; README trimmed; MIT license added.
**v3 complete: merged to `main`, tag `v3.0` (2026-10-06).** Nothing further is assumed or owed - a later version (e.g.
comparing paid services with the local stack) starts only when the user asks.
**v4 planned (user, 2026-10-08):** paid services against the local stack, starting with the Claude API as the chat
model only (same embeddings, index and retrieval), in a separate `RagFilingExplorer.Claude` project - Local stays
zero-cost. Steps 1-4 done (branch `v4`): the chat model's options passed in, the Claude app, and the evaluation's
`ChatModel` (Local or Claude; the cache keyed by model), the docs. No paid call yet. Decision-Log.md, "paid services (v4)".

**Latest full run (2026-09-24, temperature 0):** 24/24 on `tools/manual-questions.txt`, every filter as
expected, on both chunking strategies; `tools/replay_recall.py` 22/22 answerable questions with the
figure in the top-5 context on both (deterministic, no LLM - see its header); 159 unit tests. `Retrieval.ChatTemperature` is 0 so a changed
answer can be attributed to a code change rather than sampling. Details in Decision-Log.md, "trailing
remainders, per-company search, table-piece headers".

**Pre-manual-pass review (2026-09-25):** fixed the silently dropped first chunk (MSFT's cover page; int key
0), NFLX/NDAQ statements headed "Item 16", and `MaxOutputTokens` exceeding the context window (4096 -> 768).
Both indexes rebuilt; replay top-5 unchanged; 170 unit tests. Soft filter measured, not built. Details in
Decision-Log.md, "pre-manual-pass review".

**Chunking strategies - done, recorded (2026-09-24):** `Markdown` (default) and `Linearized` both ship
and are switchable. Compared on 10 targeted questions plus 2 routing tests: Linearized puts the answer in
the model's context for 7/10 vs 4/10, answers 5 vs 4 right, 2 wrong each. The user chose to record this
and stop; `Markdown` stays the default. Follow-ups (roll-forward periods, table mixing, soft filter /
hybrid search, embedding-model comparison) are listed, not planned - Decision-Log.md, "targeted
questions and a rank metric". Hybrid search was checked for feasibility only, never measured (corrected
in "pre-manual-pass review").

**Manual pass (2026-09-25/28):** strict grading exposed missing units (7/24) and near-identical-line swaps
(Q10); a new `SystemPrompt` fixed the units - 22/24 reliable on both strategies. Two further prompt revisions
moved failures around rather than removing them and weren't kept; `Markdown` stays the default (Linearized
regressed on T4 and Q2). Largest prompt 3,103 + 768 output of 4,096. Details in Decision-Log.md, "manual pass (v1)".

**v1 is complete (2026-09-28, tag `v1.0`). Next: v2 - a new ingestion built on the form structure and
inline XBRL (Decision-Log.md, "XBRL hybrid (v2)") - on a branch, each step measured before the next,
starting with step 0: a held-out question set, an automatic answer grader and v1's baseline results. Storage
stays SQLite (PostgreSQL considered and declined); the no-regression bar is the strict 22/24 reliable.**

**v2 status (2026-09-29, branch `v2`):** a third strategy, `Structured`, built step by step - 0 done
(held-out H1-H15, `tools/grade_answers.py`, `eval/baseline-v1/`), 1a done (DOM parse, no markitdown), two 1c
table fixes done early, 1b-i done (XBRL reader + taxonomy reader, matching EDGAR's extraction fact for fact on
all four filings), 1b-ii done and measured (filing-profile chunk: main 22/24, held-out 11/15 - see
`eval/structured-1b-ii/`). **Block model done (2026-09-29):** v2 ingestion has its own objects (`Structured/`:
blocks -> sections -> chunks that know their blocks) instead of text parsed back; v1's heading and packing rules
shared; all 948 chunks reproduced byte for byte. **1b-iii, structure labels, in three measured sub-steps:**
a) statement type from the filer's Statement roles - done and measured (`eval/structured-1b-iii-a/`: 947 of 948
tags as before, the one change a data-free footer; every score and 54 of 55 answers unchanged); b) note topics -
done and measured (`eval/structured-1b-iii-b/`: each note a section headed by its topic; targeted 5/10 -> 7/10, T6
by retrieval, the rest unchanged); c) period labels on roll-forward rows - done and measured
(`eval/structured-1b-iii-c/`: 123 rows labelled; T9 still wrong - the labelled row reaches the model, which misses
it on wording). **1b-iii closed (user, 2026-09-29).** Registration from the cover facts done (`CompanyRegistry`
replaces the hand-written table; all 55 questions route as before). **1c in two measured sub-steps:** a) fiscal-year
names on period labels - done (`eval/structured-1c-a/`: scores unchanged; T9 now reads the right row but gives its
per-share figure); b) one table of figures per chunk - measured and **declined** (`eval/structured-1c-b-declined/`:
no score gained, retrieval MRR down 14-18%, T2's period worse; T4's miss is now ranking, for step 2). **1c closed.**
**1d done (`eval/structured-1d/`):** every chunk's embedding text opens with the company and filing from the cover
facts - chosen by a replay-only experiment of five embedding texts; targeted recall@5 8 -> 9/10, held-out 9 -> 11/13;
T4 and H6 answered right for the first time; totals held back by prompt v1's units rule leaking into declines.
**Step 1 (ingestion) complete.** Order kept (user, 2026-09-29). **Step 2, hybrid search, done and kept (2026-09-30,
`eval/structured-2/`):** `Retrieval:Search = Hybrid` - FTS5 `bm25()` + sqlite-vec fused by reciprocal rank fusion, the
statement type a boosting third list instead of a hard filter; chosen by a replay-only spike of six variants. Main 22/24,
targeted 7/10, **held-out 10 -> 14/15**; R1, R2, H14 (routing misses) and T5 answered right; Q10/V1's lookalike line right
for the first time; T1 lost (a lookalike line, now among 5 chunks - the hard filter had silently sent 1-4 to most statement
questions). **Step 2b (reranking) researched and deferred (user, 2026-09-30)** - Ollama has no rerank endpoint; the path
is an ONNX cross-encoder, spike planned but not run; R3 stays wrong. **Step 3 (calculator) scoped and deferred (user)** - only V3 would change on
today's questions. **Step 3b (answer verification) measured and deferred (user)** - replayed on all 55 answers, it flags
0 of 7 wrong answers: each states a figure that is in its context (misreadings, not inventions). **Fresh held-out set H16-H35
written and baselined** on the step 2 code (`eval/structured-2-heldout35/`): 16/20; 3 of 4 failures are retrieval misses
(rank 7-11), 1 a malformed decline; all 8 lookalike-line questions passed. **Step 5a done and kept (`eval/structured-5a/`):** a fixed decline form
("The excerpts don't contain <what the question asks for>.") - every decline clean, H20 fixed, T6/H29 wrong figures now
declines, nothing lost; held-out H16-H35 16 -> 17/20, all else unchanged. A first version that also said "no units"
dropped two NFLX units and was not kept. **2b spike measured (2026-10-01, `eval/rerank-2b-spike/`):** reranking the top 25 hybrid
candidates with `ms-marco-MiniLM-L6-v2` (ONNX, local), the chunk opened by the company line, puts R3, H25 and H34's answers
in the top 5 and drops none - recall@5 64 -> 67 of 68, ~1-3 s CPU per question; H29 doesn't move; overlapping windows for
long chunks measured and declined. **Built and kept (user, 2026-10-01, `eval/structured-2b/`):** `Retrieval:Rerank` - main
23/24, targeted 9/10, routing 3/3, held-out 15/15 and 18/20 (69/75 vs 64/75), every decline clean; V1 lost on a knife-edge
lookalike (the answer chunk alone gives the wrong line). .NET's `BertTokenizer` needed Hugging Face's normalisation
reimplemented (all 73,125 pairs differed). **Answer-side set A1-A27 added and baselined (2026-10-01,
`eval/answer-side-baseline/`): 18/27** - lookalike lines, per-share and thousands all pass (14/14); only 3 of 9 misses are
misreadings (arithmetic, MD&A rounding); **4 are the reranker's** (answer in hybrid's top 5, pushed out - replay recall@5
22/26 -> 18/26 on this set). **Reranking decided (user, 2026-10-02): off** - A1-A27 without it
21/27 (`eval/answer-side-norerank/`); replay recall@5 over all 94 answerable questions hybrid 86, reranked 85, RRF blends of
the two 85 and 85; a second family (bge-reranker-v2-m3) screened on the 8 contested questions, 4/8 like hybrid at ~38 s/question
(`eval/rerank-decision/`). The code stays as the opt-in `Retrieval:Rerank`. **Answer side screened, nothing built (user,
2026-10-02, `eval/answer-side-screens/`):** a cash-flow prompt rule fixed 0 of 2 targets (A10, A15); a calculator tool
(step 3) fixed A27 but lost A24 and A26 - llama copies figures into the call wrongly. The remaining misses are recorded as
llama3.1:8b misreadings. **Closing v2 (2026-10-02):** shipped defaults now `Structured` + `Hybrid`, reranking off -
the configuration of v2's final baselines (`structured-5a/`, `answer-side-norerank/`); code comments reviewed against the code; CompanyRegistryTests check every filing in `data/`; README and this plan's pipeline section rewritten for v2. **v2 complete: merged to `main`, tag `v2.0` (2026-10-02).** **Second-model run done (2026-10-01,
`eval/granite41-*`):** granite4.1:8b targeted 9/10 vs llama's 7/10 (reads lookalike lines better) but main 21/24, held-out
12/15 and 16/20, breaks the decline form and invented one figure (H25); granite4.1:3b lower everywhere but routing.
**`llama3.1:8b` kept (user).** `Console.OutputEncoding` fixed (2026-10-01): the app reads and writes UTF-8, so
logs no longer carry OEM code-page bytes. Details: Decision-Log.md, "XBRL hybrid (v2)".

**Known, not planned:** statement routing is keyword *substring* matching - "deferred revenues" routes to the income
statement (R1); colliding keywords drop the route ("cash flow hedge", T10). Under `Vector` search it's a hard filter, so
R1 can't reach the balance sheet; under `Hybrid` it only boosts, and R1 is answered. Chunks routinely exceed the
500-token budget (up to ~800 for NFLX's widest tables) - rows and blocks are counted separately, without the separators
joining them. Well inside nomic-embed-text's context, so harmless in practice; the budget is approximate by design.

---

## Pipeline as it ships

Default (v2): `Structured` chunking, `Hybrid` search, no reranking.

```
data/*.html + taxonomy (.xsd, linkbases)
  → CompanyRegistry       at startup, every filing: registrant name + common-stock ticker from the cover
                          facts → the company filter and each filing's company line
  → StructuredChunkingStrategy (Structured/, Xbrl/)
      InlineXbrlReader    contexts, units, every fact (continuations followed); TaxonomyReader: roles,
                          presented concepts, labels - matches EDGAR's extraction fact for fact
      NoteTopics          each note's extent and topic, from text-block tags + Disclosure roles
      FilingBlockReader   the DOM (AngleSharp, no markitdown) → typed blocks in reading order: paragraphs
                          in markitdown's text shape; each top-level table → TableBlock (HtmlTableLinearizer
                          row block, or text rows on fallback; roll-forward rows get their period, named by
                          the filer's fiscal calendar - PeriodLabels)
      StatementLabels     the five primary statements' tables, from the filer's Statement roles
      StructuredSections  v1's heading rules (SectionSplitter.HeadingTracker) over blocks; a note is its
                          own section, headed "... > <topic>"; FilingProfile's "Cover Page" section first
      StructuredChunker   v1's packing rules (TokenChunker.Pack) over blocks; a chunk's statement type is
                          its table's; embedding context = the company line (step 1d)
  → [shared from here, below]
```

v1's strategies (`Markdown`, `Linearized`), still selectable:

```
data/*.html
  → [Linearized only]     HtmlTableLinearizer: each table it can linearize → <pre> RowBlock of
                          self-contained row lines (content guard: any lost cell text → keep table)
  → MarkItDownConverter   detect encoding, strip <ix:header>, shell out to `markitdown`
  → SectionSplitter       "PART I > Item 1. Business" heading paths from plain-text patterns;
                          a new section (same heading) at every statement title / Notes boundary;
                          Part IV back matter gets its own heading ("Financial Statements",
                          "Signatures", "Exhibit Index"), not the last Item's;
                          fenced row blocks copied through, never read as headings
  → TokenChunker          ~500-token chunks (cl100k), 50 overlap; tables atomic, headers +
                          fiscal-period row + in-force row-group label repeated across
                          splits; a short lead-in (title, "(in millions)") rides on an
                          oversized table's first piece, a short footer on its last; a row
                          block splits between rows, every piece repeating title/units/context
  → [shared from here]
```

Shared by all three:

```
  → FilingChunkRecords    keys from 1; StatementType: the strategy's (Structured), else the last statement
                          title carried forward, reset at "Notes to Financial Statements" and on filing change
  → SqliteVec index       rag.<strategy>.db; nomic-embed-text, "search_document:" prefix + company line
                          (Structured) + EmbeddingTextBuilder text
  → RagAnswerService      company filter (CompanyRegistry) + statement type (QueryIntentResolver) → top-K
                          search (one per company, interleaved, when 2+ are named) - Hybrid (default):
                          vector + FTS5 bm25 (chunks_fts, created at startup) + vector-within-statement,
                          50 deep each, fused by RRF (k = 60); Vector (v1): statement type a hard filter →
                          [Rerank, opt-in] each company's top 25 reordered by a local cross-encoder
                          (ms-marco-MiniLM-L6-v2, ONNX; company line + excerpt header + chunk) →
                          citation prompt → llama3.1:8b (reasoning only for synthesis questions
                          on a model that reports the "thinking" capability)
```

v4 (branch `v4`), optional: `RagFilingExplorer.Claude` runs the same flow over the same index - Local's
`appsettings.json` read unchanged, embeddings still Ollama's - with Claude (`Anthropic` SDK, `AsIChatClient`) in place
of llama3.1:8b. `ChatModelOptions` carries what differs: no temperature (Claude rejects a non-default one), reasoning
effort for lookups as well as synthesis (never None - the SDK sends it as thinking disabled, a 400), output ceiling
16,000 (thinking counts toward it). Settings in `RagFilingExplorer.Claude/claudesettings.json`; the key from .NET user
secrets (`Claude:ApiKey`) or `ANTHROPIC_API_KEY`, never that file (refused at load). The evaluation picks the model with `evalsettings.json`'s `ChatModel`.

Packages: `AngleSharp` (the DOM - v2's reader and the linearizer), `Microsoft.Extensions.AI`, `Microsoft.Extensions.VectorData.Abstractions`,
`CommunityToolkit.VectorData.SqliteVec` (1.0.1-preview), `OllamaSharp`,
`Microsoft.ML.Tokenizers.Data.Cl100kBase`, `Microsoft.Extensions.Configuration.Json` and `.Binder` (the base package comes with `.Json`),
`Microsoft.Bcl.Memory` (pinned — see below), `Microsoft.Data.Sqlite` (hybrid search's FTS5 queries; already
transitive via SqliteVec, referenced at the same version), `Microsoft.ML.OnnxRuntime` 1.30.0 and
`Microsoft.ML.Tokenizers` 2.0.0 (the reranker; its model is fetched separately). The Claude app (v4) only: `Anthropic`
12.54.1 and `Microsoft.Extensions.Configuration.EnvironmentVariables`. Tests: NUnit + Moq. Tunables live in
`RagFilingExplorer.Local/appsettings.json`; domain logic (keyword lists, regexes) stays in code.

---

## Live constraints — each one is a silent regression if forgotten

- **`VectorStore.UpsertBatchSize` must stay `1`** while on SqliteVec `1.0.1-preview`: multi-record
  upserts throw `UNIQUE constraint failed on vec_chunks primary key` (an upstream `sqlite-vec` bug fixed
  in a native build NuGet hasn't picked up). Don't hand-swap `vec0.dll` — it wouldn't survive a clone.
- **A new filing registers its company from its cover facts** (`CompanyRegistry`: registrant name without
  its legal form + the common stock's ticker; replaced the hand-written `CompanyToFiling` table, 2026-09-29).
  An unregistered company runs its questions unfiltered across every filing (this produced a hallucinated
  figure for NFLX), so check the "Company filter:" line startup prints for it - a name questions won't use
  (a brand unlike the legal name) still needs attention. Also spot-check its
  `chunk-review/<strategy>/*.chunks.txt` for `�` and a complete Item outline.
- **A new filing needs its XBRL taxonomy in `data/`** under the `Structured` strategy (the shipped default from v2):
  the `.xsd`, plus its `_pre`/`_lab`/`_cal`/`_def.xml` linkbases when the filer ships them separately (NDAQ and NFLX
  do), from the filing's EDGAR folder. Without it chunking stops ("no taxonomy schema (.xsd)"); statement types come
  from its Statement roles, and a role that can't be mapped stops it too - loudly, never a silent mislabel.
- **A new filer's statement titles must actually be detected** (v1's `Markdown` and `Linearized` strategies). After onboarding, check the index for
  one `StatementType` transition per primary statement plus the Notes reset
  (`SELECT Key, StatementType FROM chunks WHERE SourceFiling = ... ORDER BY Key`). NDAQ's "Statements of
  Changes in Stockholders' Equity" went undetected until the second review: 0 `equity_statement`
  chunks, so every NDAQ equity question returned "(no results)".
- **Period-end equity questions route to the balance sheet**, not the equity statement - the
  balance sheet has one clean total row; the equity statement is a wide roll-forward whose closing row
  gets lost among near-identical fragments (ORCL: rank 8-9 of 17). "Changes in ... equity" questions
  still go to the equity statement via `ResolveStatementType`'s longest-match rule.
- **Chunking/embedding *code* changes need `--rebuild`.** Settings and filing changes are detected
  automatically via `rag.<strategy>.db.manifest.json` - under `Structured`, the filers' taxonomy files in `data/` too
  (not EDGAR's `_htm.xml` test files); code changes can't be.
- **Statement-type regexes must require "STATEMENTS"** (except balance sheet) — without it, bare
  headings like "OPERATIONS" or "Cash Flows" mistagged up to 96 consecutive chunks.
- **Never send a reasoning ("think") request to a model without the `thinking` capability** — Ollama
  hard-errors rather than ignoring it. The capability comes from `/api/show` at startup, never the name.
- **Prompt + `Retrieval.MaxOutputTokens` must fit Ollama's `num_ctx`** (4096 by default; the app doesn't
  set it). Past it, Ollama silently drops the oldest tokens - the system prompt and the top-ranked chunks.
  Prompts reach ~3,000 tokens, so raising `MaxOutputTokens` or `GenerationTopK` needs `num_ctx` raised too.
- **The .NET evaluators are the source of truth, not the Python tools** (user, 2026-10-06). `tools/grade_answers.py` and
  `tools/replay_recall.py` are kept unchanged as the record of what `StrictGrader` and `RetrievalRankEvaluator` were
  ported from - a grading rule changes in .NET only. The replay mirrors v2's retrieval (`HYBRID_CANDIDATES`, `RRF_K`):
  after a retrieval change it no longer reproduces the app, silently - read ranks from the evaluation's "Answer rank".
- **The reranker's tokenization is Hugging Face's, reimplemented** (`BertPairEncoder`): `BertTokenizer`'s own basic
  tokenization drops line breaks, `|`, `$` and unknown symbols, and all 73,125 (question, chunk) pairs differed from
  what the spike measured. Another reranker model, or a `Microsoft.ML.Tokenizers` upgrade, needs the token-for-token
  parity check re-run against Python's `tokenizers` (Decision-Log.md, "Step 2b - build").
- **The evaluation's answering client must be its reporting configuration's chat client** (v4). The response cache
  key holds the model only through it - the library adds that client's provider and model id - and the prompts are
  byte-identical across models, so a Claude answer asked through any other client would silently replay llama's cached
  answer (or Opus's for Sonnet). `ResponseCacheKeyTests` holds the key; Decision-Log.md, "Step 3 - done".
- **Config values live only in `appsettings.json`** — no duplicate defaults in code (this has drifted
  twice). Presence of every key is validated at load, since `required` doesn't apply to the binder.
  The evaluation follows the same rule in its own `evalsettings.json` (graders, judges, run options; v3) - environment
  variables only override a key for one run, with the same name (`Evaluation__Graders=both`).
- After any package change, run `dotnet list package --vulnerable --include-transitive`
  (`Microsoft.Bcl.Memory` is pinned to `10.0.12` to avoid GHSA-73j8-2gch-69rq via a transitive `9.0.4`).
- Don't compare `VectorSearchResult.Score` values across vector-store providers — SqliteVec and
  InMemory use different scales; only ranking is comparable.

---

## Reference: already-validated facts (don't re-derive these)

- `Microsoft.Extensions.VectorData.Abstractions` — stable/GA since mid-2025, used internally by
  Semantic Kernel and Microsoft Agent Framework. No pivot risk.
- `Microsoft.Extensions.DataIngestion` — confirmed still 100% preview as of last check
  (`10.10.0-preview...`), no GA release yet. Real chunker classes: `HeaderChunker`, `SectionChunker`,
  `SemanticSimilarityChunker`, `DocumentTokenChunker`. Document readers currently limited to
  `MarkItDown` and `Markdig`. **Tried and abandoned in Step 3 for this project's input (SEC EDGAR
  HTML 10-Ks)** — not because the API surface is wrong on paper, but because: (a) neither reader
  handles raw HTML directly, (b) the parsed documents have zero real headings so the chunkers can't
  do their job, and (c) `MarkdownParser.Parse` hardcodes Markdig's `UseAdvancedExtensions()` with no
  way to disable it, and its math extension crashes (`NotSupportedException: MathInline`) on
  financial tables full of dollar figures. This is a property of *this* document family, not a
  blanket verdict on MEDI — a different input (e.g. genuinely Markdown-sourced content) might not
  hit any of these.
- `OllamaSharp` — confirmed direct `IChatClient`/`IEmbeddingGenerator` implementation, no wrapper needed.
- `Microsoft.ML.Tokenizers` — needs the matching `Microsoft.ML.Tokenizers.Data.*` package (e.g.
  `.Data.Cl100kBase` for GPT-4 encoding) for `TiktokenTokenizer.CreateForModel` to work fully offline;
  without it, vocab is fetched over the network at runtime.
- SEC EDGAR filings embed a hidden inline-XBRL metadata block (`<ix:header>...</ix:header>`) that
  isn't part of the human-readable filing and must be stripped before any text-extraction step, or it
  shows up as a huge garbage blob ahead of the real content.
- SEC filing agents are not structurally uniform: MSFT and ORCL share an `id="item_1_business"`-style
  HTML anchor convention; NDAQ's generator uses GUID-style IDs with no semantic anchors at all. Don't
  build section detection that depends on a specific filer's HTML conventions — work from the
  converted, plain-text output instead. The same "don't rely on one filer's HTML idiosyncrasies"
  lesson also applies to purely decorative markup (see Step 3's post-review finding in Decision-Log.md).
- `nomic-embed-text`'s output dimension (768) was directly confirmed via Ollama's own API
  (`GET /api/tags` → `"embedding_length":768`), not just assumed from the plan's original guess.
- `Microsoft.Extensions.VectorData`: a property marked `[VectorStoreVector]` with a `string` type
  (automatic embedding generation) **cannot be read back** after storage — confirmed both in MEVD's
  own docs and by testing. If the original text needs to be retrieved later, store it separately in
  an ordinary `[VectorStoreData]` property.
- `OllamaApiClient`'s default `HttpClient.Timeout` (100s, inherited from `HttpClient`'s default) is
  too short for bulk embedding generation under CPU-only inference — a batch of even a few dozen
  chunks can exceed it. Construct it with an explicit `HttpClient { Timeout = ... }` (5 minutes worked
  for 1,073 chunks) instead of the URI-only constructor, and upsert in smaller batches (25 at a time
  here) rather than one giant call, for both resilience and visible progress.
- **HTML parsing:** Microsoft Learn names no preferred .NET HTML parser (none built in; the Microsoft HTML
  DOM APIs are IE-backed WinForms or .NET-Framework-only Razor internals); Microsoft's own ASP.NET Core
  integration-test docs use AngleSharp. See Decision-Log.md, "linearized tables as a second chunking
  strategy".
- **nomic-embed-text reads at most 2,048 tokens through Ollama, and cuts the rest silently.** The model card says
  8,192 (rope scaling past 2,048), but Ollama's GGUF declares `context_length: 2048`, and `/api/embed` truncates by
  default instead of failing. Checked 2026-09-29 with `truncate: false` and Ollama's own token count, largest chunks
  of each strategy: Markdown 778 tokens, Linearized 636, Structured 579 - nomic's tokenizer counts 0.85-1.08x of
  cl100k's on these filings. Safe at the ~500-token budget; raising `MaxTokensPerChunk` past ~1,500, or a much
  longer embedding text (step 1d), needs this re-checked.
- **Embedding prefixes are per model family, not universal:** `search_query:`/`search_document:` is the
  Nomic convention (mandatory for v1.5); qwen3-embedding, embeddinggemma and mxbai use different
  templates, two with no document prefix. Full table in Decision-Log.md, "embedding-model comparison".
- These were checked directly against Microsoft's own documentation, not just general web search —
  treat them as reliable unless something has changed since. The MEDI-specific findings in this list came
  from direct experimentation against this project's actual filings, not from documentation.
