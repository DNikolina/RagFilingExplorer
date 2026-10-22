# Implementation Plan: SEC 10-K RAG Tool

A RAG tool over public SEC 10-K filings: fully local and zero-cost by default (Ollama), with paid providers (Claude,
Voyage) as options since v4.

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
- Python 3.12 + `pip install markitdown` - only for v1's `Markdown` and `Linearized` strategies, which shell out to
  the `markitdown` CLI for HTML→text (Decision-Log.md, Step 3). The default `Structured` strategy reads the DOM itself.
- Hardware: 12th Gen Intel i7-12800H, 32GB RAM, Intel UHD integrated graphics — **CPU-only inference,
  no GPU acceleration available.** This is why `llama3.1:8b` was chosen over larger local models.

---

## Status

| Step | Status |
|---|---|
| 1. Source data | Done — MSFT, ORCL, NDAQ 10-Ks from EDGAR; NFLX added later |
| 2. Project setup | Done |
| 3. Chunking | Done — hand-written pipeline; MEDI tried and abandoned; `Structured` (v2) the default |
| 4. Vector storage | Done — SqliteVec, one index per chunking strategy, embedding model and chunk budget, each with a build manifest |
| 5. Retrieval | Done — company filter; `Hybrid` search (v2), the statement type a boost; reranking opt-in |
| 6. Answer generation | Done — citation-grounded prompt, reasoning-model support |
| 7. Testing | Done — unit tests, and the .NET evaluation (v3): every question graded strictly, its answer ranked, its figures traced |
| 8. Publish | Done — `v1.0` to `v4.0` tagged on `main` |

Each version is complete on its own; the next starts only when the user asks. The history behind every line below is
in Decision-Log.md, under the section named.

| Version | What it added | Headline | Decision-Log.md |
|---|---|---|---|
| v1 (`v1.0`, 2026-09-28) | `Markdown` and `Linearized` chunking over markitdown, vector search with a statement-type filter, the citation prompt | Manual pass graded strictly: 22/24 reliable on both strategies | Steps 1-8, "manual pass (v1)" |
| v2 (`v2.0`, 2026-10-02) | `Structured` chunking from the DOM and inline XBRL; `Hybrid` search (FTS5 + vectors, RRF); a fixed decline form; the local cross-encoder reranker, opt-in | Held-out H1-H15 10 -> 14/15 from hybrid search; reranking measured and left off (recall@5 85 vs hybrid's 86, of 94); granite4.1 tried, llama kept | "XBRL hybrid (v2)" |
| v3 (`v3.0`, 2026-10-06) | The evaluation in .NET (`Microsoft.Extensions.AI.Evaluation`): strict grade, answer rank, figure source, reports, response cache; a local judge as a second view | v2's grades reproduced; llama's Equivalence judge passed 14 of 18 wrong answers, so the strict grade stays the grade | "evaluation in .NET (v3)" |
| v4 (`v4.0`, 2026-10-22) | Paid providers beside the local stack: Core plus provider libraries, one app (`RagFilingExplorer.Cli`); Claude as chat model and judge; Voyage embeddings and reranker | Below | "paid services (v4)" |

**v4, batch by batch:**
1. **Claude as the chat model - complete.** Opus 5.5 and Sonnet 5.5 on the default index: every remaining miss a
   retrieval miss; wording varies between runs, answers don't.
2. **A paid judge - complete.** Sonnet and Opus as judges pass no wrong figure (llama's passed 14 of 18) but are lenient
   on rounding and units; the strict grade stays the grade, `JudgeModel` an optional second view.
3. **Paid embeddings - complete.** First the chunks fixed (split statements cut at a row-group change, NDAQ's lines joined
   into paragraphs, row groups checked against the XBRL presentation trees). Retrieval-only over 94 questions, recall@5 /
   MRR: nomic 86 / 0.756, `voyage-finance-2` 90 / 0.794, `voyage-4-large` 89 / 0.834; bigger chunks measured,
   `MaxTokensPerChunk` stays 500. `Claude:GenerationTopK` 5 -> 8.
4. **A paid reranker - complete.** `rerank-3` lifts recall@5 nomic 86 -> 94, finance-2
   91 -> 95, losing no answer; `rerank-3-lite` one behind. llama barely moves (86 and 87/102) - its failures are now
   misreadings with the answer at rank 1-3.
5. **The Mac - moved out of v4** (user): a bigger local model on better hardware, when the hardware arrives.

**Where it stands:**
- **Shipped defaults - free and local:** `Structured`, `Hybrid`, nomic-embed-text, llama3.1:8b at temperature 0 (so a
  changed answer is a changed input, not sampling), reranking off. llama's baseline: 85/102,
  `structured-hybrid-chunk-fixes`. Compare llama runs only on the same Ollama build - a new build can change answers.
- **Best measured with Claude:** Opus 5.5 (medium) on `voyage-finance-2` + `rerank-3` at 5 excerpts, 102/102 strictly,
  ~$2.05 a run; Sonnet 5.5 the same, 100/102 strictly, ~$0.91. Without the reranker Opus needs 8 excerpts for 102/102
  (553K input tokens against 366K).
- **Measured and not built** (don't re-propose without new evidence): a calculator tool (llama copies figures into the
  call wrongly), answer verification (flagged 0 of 7 wrong answers - they're misreadings, not inventions), one table per
  chunk (retrieval worse), `embeddinggemma` (ties nomic; EmbeddingGemma 2 can't run here), a soft statement filter.

**Known, not planned:** statement routing is keyword *substring* matching - "deferred revenues" routes to the income
statement (R1); colliding keywords drop the route ("cash flow hedge", T10). Under `Vector` search it's a hard filter, so
R1 can't reach the balance sheet; under `Hybrid` it only boosts, and R1 is answered. Chunks routinely exceed the
500-token budget (up to ~800 for NFLX's widest tables) - rows and blocks are counted separately, without the separators
joining them. Well inside nomic-embed-text's context, so harmless in practice; the budget is approximate by design.

---

## Pipeline as it ships

### Projects

| Project | What it is | References |
|---|---|---|
| `RagFilingExplorer.Core` | The whole pipeline below - chunking, the index, retrieval, the prompt, the local reranker - and no model provider | - |
| `RagFilingExplorer.Ollama` | Embedder and chat client (`OllamaSharp`) | Core |
| `RagFilingExplorer.Claude` | Chat client (`Anthropic` SDK, `AsIChatClient`) | Core |
| `RagFilingExplorer.Voyage` | Embedder and reranker (a hand-written `HttpClient`) | Core |
| `RagFilingExplorer.Cli` | The app: chooses the providers from `appsettings.json`'s `Providers` and passes Core the embedder, chat client and reranker | Core + the three providers |
| `RagFilingExplorer.Evaluation` | The evaluation: asks the question sets through the same composition as the app (`AppComposition`) | Core + the three providers |

The providers never reference each other. Tunables live in `RagFilingExplorer.Cli/appsettings.json`; domain logic
(keyword lists, regexes) stays in code.

### The default flow

`Structured` chunking, `Hybrid` search, reranking off:

```
Indexing - at startup, rebuilt when the build manifest says the index is stale
  data/*.html + taxonomy (.xsd, linkbases)
  → CompanyRegistry       registrant name + common-stock ticker from each filing's cover facts
                          → the company filter, and the company line opening every chunk
  → InlineXbrlReader      contexts, units, every fact; TaxonomyReader: roles, presented concepts, labels
                          (matches EDGAR's extraction fact for fact)
  → FilingBlockReader     the DOM (AngleSharp) → typed blocks in reading order; each table → a row block
                          (HtmlTableLinearizer), roll-forward rows named by the filer's fiscal calendar (PeriodLabels)
  → StatementLabels       the five primary statements' tables, from the filer's Statement roles;
    NoteTopics            each note's extent and topic, from text-block tags + Disclosure roles
  → StructuredSections    heading paths, a note its own section ("... > <topic>"); the Cover Page section first
  → StructuredChunker     ~500-token chunks (cl100k); a table split between row groups, every piece repeating its
                          title, units and periods; a chunk's statement type is its table's
  → embedder              the model card's document template + company line + chunk text
  → SqliteVec index       vectors + an FTS5 table (chunks_fts) + the build manifest

Answering - per question
  → QueryIntentResolver   company filter (CompanyRegistry) + statement type (keyword substring match)
  → Hybrid search         per named company: vector + FTS5 bm25 + vector-within-statement, 50 deep each,
                          fused by RRF (k = 60); two or more companies interleaved
  → [reranker, opt-in]    each company's top 25 reordered
  → prompt                the top GenerationTopK as excerpts (company line + header + chunk), the citation
                          system prompt, a fixed decline form
  → chat model            reasoning only for synthesis questions, on a model that reports "thinking"
```

### Providers

| Role | Default - free, local | Paid option | Chosen by |
|---|---|---|---|
| Embedding | Ollama `nomic-embed-text` (768 dimensions) | Voyage `voyage-finance-2` or `voyage-4-large` (1024): Voyage's `input_type` differs for chunks and questions, so the index is built through a "document" embedder and searched through a "query" one | `Providers:Embedding`, `Embedding:Model` |
| Chat | Ollama `llama3.1:8b`: temperature 0, `num_ctx` 4096, 5 excerpts | Claude: no temperature (Claude rejects one), reasoning effort on lookups too (never None - the SDK sends it as thinking disabled, a 400), a 16,000-token output ceiling (thinking counts), 8 excerpts | `Providers:Chat`, the `Claude` section |
| Reranking, off by default | Local cross-encoder `ms-marco-MiniLM-L6-v2` (ONNX; its model fetched separately) | Voyage `rerank-3` or `rerank-3-lite`: every candidate scored, `truncation: false`; one billed call at startup checks the key and model | `Retrieval:Rerank`, `Providers:Rerank`, `Voyage:RerankModel` |

API keys are never in `appsettings.json` (refused at load; the repo is public): `ANTHROPIC_API_KEY` or `Claude:ApiKey`
in the Claude library's user secrets; `VOYAGE_API_KEY` or `Voyage:ApiKey` in the Voyage library's. The evaluation
chooses the chat model with `evalsettings.json`'s `ChatModel` and embeds and reranks as the app's settings say. A Voyage
reranker is called on every evaluation run, cached answers or not - the response cache keys on the excerpts it picks.

### v1's strategies

`Markdown` and `Linearized` (`Chunking:Strategy`) stay selectable. They convert each filing with the `markitdown` CLI
(encoding detected, `<ix:header>` stripped; `Linearized` first turns each table it can into a row block), find
sections from plain-text heading patterns (`SectionSplitter`), and pack them with `TokenChunker` (the same 500-token
budget, 50 overlap). A chunk's statement type is the last statement title carried forward, reset at the Notes. The rest
of the flow is the default's. Decision-Log.md, Steps 3-4 and "linearized tables as a second chunking strategy".

### Packages

Each project's `.csproj` lists them, with versions. Not obvious from the code:

- `Microsoft.Data.Sqlite` - referenced directly, at SqliteVec's version, for the FTS5 queries.
- `Microsoft.ML.Tokenizers.Data.Cl100kBase` - keeps the tokenizer offline.
- `Microsoft.Bcl.Memory` - pinned (Live constraints).
- Tests: NUnit + Moq.
- The evaluation: `Microsoft.Extensions.AI.Evaluation`, `.Quality` and `.Reporting`, on NUnit.

---

## Live constraints — each one is a silent regression if forgotten

### Ingestion and the index

| Rule | If forgotten | Held by / how to check |
|---|---|---|
| Keep `VectorStore.UpsertBatchSize` at `1` while on SqliteVec `1.0.1-preview` | Multi-record upserts throw `UNIQUE constraint failed on vec_chunks primary key` (an upstream `sqlite-vec` bug fixed in a native build NuGet hasn't picked up) | Don't hand-swap `vec0.dll` - it wouldn't survive a clone |
| A new filing registers its company from its cover facts (`CompanyRegistry`: registrant name without its legal form + the common stock's ticker) | An unregistered company's questions run unfiltered across every filing - this produced an invented NFLX figure | Startup's "Company filter:" line (a brand unlike the legal name still needs attention); its `chunk-review/<strategy>/*.chunks.txt` for `�` and a complete Item outline |
| A new filing needs its XBRL taxonomy in `data/` (`Structured`): the `.xsd`, plus the `_pre`/`_lab`/`_cal`/`_def.xml` linkbases when the filer ships them separately (NDAQ and NFLX do), from its EDGAR folder | Not silent: chunking stops ("no taxonomy schema (.xsd)"), as it does for a Statement role it can't map - never a mislabel | - |
| A new filer's statement titles must be detected (`Markdown`, `Linearized`) | No chunks of that statement type - every NDAQ equity question returned "(no results)" | One `StatementType` transition per primary statement plus the Notes reset: `SELECT Key, StatementType FROM chunks WHERE SourceFiling = ... ORDER BY Key` |
| Statement-type regexes require "STATEMENTS" (except the balance sheet) | Bare headings like "OPERATIONS" or "Cash Flows" mistagged up to 96 consecutive chunks | - |
| Period-end equity questions route to the balance sheet, not the equity statement | The equity statement's closing row gets lost among near-identical fragments (ORCL: rank 8-9 of 17) | "Changes in ... equity" still goes to the equity statement (`ResolveStatementType`'s longest match) |
| Chunking or embedding *code* changes need `--rebuild` | The old index is used as if current | Settings, filings and the filers' taxonomy files are detected by `rag.<strategy>.db.manifest.json` (not EDGAR's `_htm.xml` test files); code can't be |
| An embedding model's query/document templates and vector size come from its model card (`EmbeddingTemplates`, in code) | A wrong template never errors - retrieval just gets worse; the manifest records the model, not its template, so a template change needs `--rebuild` | An unlisted model is refused at startup |
| Each embedding model and chunk budget has its own index and dumps: `rag.<strategy>[.<model>][.<n>tok].db`, nomic and 500 tokens unnamed (`IndexFiles.UnnamedChunkBudget`) | - | Changing either builds a new index beside the old one rather than making it stale |

### Models and Ollama

| Rule | If forgotten | Held by / how to check |
|---|---|---|
| Never send a reasoning ("think") request to a model without the `thinking` capability | Ollama hard-errors rather than ignoring it | The capability comes from `/api/show` at startup, never the name |
| Prompt + `Retrieval.MaxOutputTokens` must fit Ollama's `num_ctx` | Ollama silently drops the oldest tokens - the system prompt and the top-ranked chunks; its own default depends on the machine's VRAM (4K / 32K / 256K) and its settings | The app sends `Ollama:ContextLength` (4096) with every llama request; startup refuses an index whose largest prompt + answer won't fit (`PromptFit`; today ~3,121 + 768 of 4,096) and an Ollama embedding model whose context is under the chunk budget; embeddings sent with `truncate: false` |
| The reranker's tokenization is Hugging Face's, reimplemented (`BertPairEncoder`) | `BertTokenizer`'s own basic tokenization drops line breaks, `\|`, `$` and unknown symbols - all 73,125 (question, chunk) pairs differed from what the spike measured | Another reranker model, or a `Microsoft.ML.Tokenizers` upgrade: re-run the token-for-token parity check against Python's `tokenizers` (Decision-Log.md, "Step 2b - build") |

### The evaluation

| Rule | If forgotten | Held by / how to check |
|---|---|---|
| The .NET evaluators are the source of truth, not the Python tools (user, 2026-10-06) - a grading rule changes in .NET only | `tools/replay_recall.py` mirrors v2's retrieval (`HYBRID_CANDIDATES`, `RRF_K`); after a retrieval change it no longer reproduces the app, silently | `tools/grade_answers.py` and `tools/replay_recall.py` kept unchanged as the record of what `StrictGrader` and `RetrievalRankEvaluator` were ported from; read ranks from the evaluation's "Answer rank" |
| The answering client must be the reporting configuration's chat client (v4) | The response cache key holds the model only through it, and the prompts are byte-identical across models - a Claude answer asked through any other client silently replays llama's cached answer (or Opus's for Sonnet) | `ResponseCacheKeyTests` (Decision-Log.md, "Step 3 - done"); a Claude judge gets its own client per question (`JudgeChatEvaluator`), or every cached answer would be asked again |

### Configuration and packages

| Rule | If forgotten | Held by / how to check |
|---|---|---|
| Config values live only in `appsettings.json` (the evaluation's in `evalsettings.json`) - no duplicate defaults in code | Duplicates have drifted twice | Every key's presence is validated at load, since `required` doesn't apply to the binder; an environment variable overrides one key for one run, with the same name (`Evaluation__Graders=both`) |
| After any package change, run `dotnet list package --vulnerable --include-transitive` | A vulnerable transitive package ships unnoticed | `Microsoft.Bcl.Memory` is pinned to `10.0.12` to avoid GHSA-73j8-2gch-69rq via a transitive `9.0.4` |
| Don't compare `VectorSearchResult.Score` across vector-store providers | SqliteVec and InMemory use different scales | Only ranking is comparable |

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
