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
   reading actual chunk output or querying `rag.db` directly, not by trusting that code looked right.

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
| 4. Vector storage | Done — now SqliteVec-persisted (`rag.db` + build manifest) |
| 5. Retrieval | Done — with company + statement-type metadata filtering |
| 6. Answer generation | Done — citation-grounded prompt, reasoning-model support |
| 7. Testing | Done — 6/6 on the Step 7 questions; 24/24 manual questions; 139 offline unit tests |
| 8. Publish | README written; **push on hold** until the user's manual pass (`docs/Manual-Test-Questions.md`) |

**Completion checkpoint:** once the manual pass is done and the repo is pushed, the project is
complete. Report that clearly and stop — nothing further is assumed or owed.

**Latest full run (2026-09-24, temperature 0):** 24/24 on `tools/manual-questions.txt`, every filter as
expected; `tools/replay_recall.py` 22/22 answerable questions with the figure in the top-5 context
(deterministic, no LLM - see its header); 139 unit tests. `Retrieval.ChatTemperature` is 0 so a changed
answer can be attributed to a code change rather than sampling. Details in Decision-Log.md, "trailing
remainders, per-company search, table-piece headers". Next: the user's manual pass, then push.

**Known, not planned:** chunks routinely exceed the 500-token budget (up to ~800 for NFLX's widest
tables) - rows and blocks are counted separately, without the separators joining them. Well inside
nomic-embed-text's context, so harmless in practice; the budget is approximate by design.

---

## Pipeline as it ships

```
data/*.html
  → MarkItDownConverter   detect encoding, strip <ix:header>, shell out to `markitdown`
  → SectionSplitter       "PART I > Item 1. Business" heading paths from plain-text patterns;
                          a new section (same heading) at every statement title / Notes boundary
  → TokenChunker          ~500-token chunks (cl100k), 50 overlap; tables atomic, headers +
                          fiscal-period row + in-force row-group label repeated across
                          splits; a short lead-in (title, "(in millions)") rides on an
                          oversized table's first piece, a short footer on its last
  → BuildRecords          StatementType tagged by carrying the last statement title forward,
                          reset at "Notes to Financial Statements" and on filing change
  → SqliteVec rag.db      nomic-embed-text, "search_document:" prefix, EmbeddingTextBuilder text
  → RagAnswerService      QueryIntentResolver filter (company + statement type) → top-K search
                          (one per company, interleaved, when 2+ are named) →
                          citation prompt → llama3.1:8b (reasoning only for synthesis questions
                          on a model that reports the "thinking" capability)
```

Packages: `Microsoft.Extensions.AI`, `Microsoft.Extensions.VectorData.Abstractions`,
`CommunityToolkit.VectorData.SqliteVec` (1.0.1-preview), `OllamaSharp`,
`Microsoft.ML.Tokenizers.Data.Cl100kBase`, `Microsoft.Extensions.Configuration(.Json/.Binder)`,
`Microsoft.Bcl.Memory` (pinned — see below). Tests: NUnit + Moq. Tunables live in
`RagFilingExplorer.Local/appsettings.json`; domain logic (keyword lists, regexes) stays in code.

---

## Live constraints — each one is a silent regression if forgotten

- **`VectorStore.UpsertBatchSize` must stay `1`** while on SqliteVec `1.0.1-preview`: multi-record
  upserts throw `UNIQUE constraint failed on vec_chunks primary key` (an upstream `sqlite-vec` bug fixed
  in a native build NuGet hasn't picked up). Don't hand-swap `vec0.dll` — it wouldn't survive a clone.
- **A new filing needs a `QueryIntentResolver.CompanyToFiling` entry**, or questions naming it run
  unfiltered across every filing (this produced a hallucinated figure for NFLX). Startup warns and a
  unit test fails on an unregistered filing, but the entry is still manual. Also spot-check its
  `chunk-review/*.chunks.txt` for `�` and a complete Item outline.
- **A new filer's statement titles must actually be detected.** After onboarding, check `rag.db` for
  one `StatementType` transition per primary statement plus the Notes reset
  (`SELECT Key, StatementType FROM chunks WHERE SourceFiling = ... ORDER BY Key`). NDAQ's "Statements of
  Changes in Stockholders' Equity" went undetected until the second review: 0 `equity_statement`
  chunks, so every NDAQ equity question returned "(no results)".
- **Period-end equity questions route to the balance sheet**, not the equity statement - the
  balance sheet has one clean total row; the equity statement is a wide roll-forward whose closing row
  gets lost among near-identical fragments (ORCL: rank 8-9 of 17). "Changes in ... equity" questions
  still go to the equity statement via `ResolveStatementType`'s longest-match rule.
- **Chunking/embedding *code* changes need `--rebuild`.** Settings and filing changes are detected
  automatically via `rag.db.manifest.json`; code changes can't be.
- **Statement-type regexes must require "STATEMENTS"** (except balance sheet) — without it, bare
  headings like "OPERATIONS" or "Cash Flows" mistagged up to 96 consecutive chunks.
- **Never send a reasoning ("think") request to a model without the `thinking` capability** — Ollama
  hard-errors rather than ignoring it. The capability comes from `/api/show` at startup, never the name.
- **Config values live only in `appsettings.json`** — no duplicate defaults in code (this has drifted
  twice). Presence of every key is validated at load, since `required` doesn't apply to the binder.
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
- These were checked directly against Microsoft's own documentation, not just general web search —
  treat them as reliable unless something has changed since. The MEDI-specific findings in this list came
  from direct experimentation against this project's actual filings, not from documentation.
