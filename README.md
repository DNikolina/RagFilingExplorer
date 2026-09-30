# RagFilingExplorer

A local, retrieval-augmented Q&A tool for SEC 10-K filings, built in C#/.NET.

**Zero cost, no API keys, no accounts.** Embedding, vector search, and answer generation all run
locally via [Ollama](https://ollama.com/) — nothing is sent to a hosted LLM API, and there's nothing
to sign up for.

Ask a natural-language question about one of the included filings and get an answer grounded in, and
cited to, the actual filing text — not the model's general knowledge.

```
> What was Microsoft's total revenue for fiscal year 2026?
(filtering to MSFT-10K-2026.html, statement type: income_statement)

--- Answer ---
According to the provided context [1] (Source: MSFT-10K-2026.html, PART II > Item 8. Financial
Statements and Supplementary Data), Microsoft's total revenue for fiscal year 2026 was $331,839 million.
```

**Why it's built this way** - why not XBRL, PDF, `Microsoft.Extensions.DataIngestion`, reranking, or a
bigger model - is answered briefly in [docs/Design-FAQ.md](docs/Design-FAQ.md).

## Stack

- **C# / .NET 10** — console app, top-level statements
- **Microsoft.Extensions.AI** + **Microsoft.Extensions.VectorData** — `IChatClient` /
  `IEmbeddingGenerator` / vector store abstractions
- **[OllamaSharp](https://github.com/awaescher/OllamaSharp)** — talks to a local Ollama instance;
  implements those abstractions directly, no custom wrapper
- **CommunityToolkit.VectorData.SqliteVec** — persistent, on-disk vector store (`rag.<strategy>.db`)
- **Microsoft.ML.Tokenizers** (offline Tiktoken, `cl100k_base`) — token-bounded chunking
- **Local models**: `nomic-embed-text` (274MB, embeddings) and `llama3.1:8b` (4.9GB, answer generation)
- **Source data**: public [SEC EDGAR](https://www.sec.gov/edgar) 10-K filings (raw HTML)

## Prerequisites — zero *cost*, not zero *setup*

- .NET SDK `10.0.400` (pinned via `global.json`)
- [Ollama](https://ollama.com/) installed and running, with both models pulled:
  ```
  ollama pull nomic-embed-text
  ollama pull llama3.1:8b
  ```
- **Python 3.12 + `pip install markitdown`** — the app shells out to the `markitdown` CLI to convert
  filing HTML to text before chunking. This wasn't in the original plan; see
  [docs/Decision-Log.md](docs/Decision-Log.md) (Step 3) for why it became necessary.

No API keys, no `dotnet user-secrets`, no cloud account of any kind.

## Hardware expectations

Developed and tested on a 12th Gen Intel i7-12800H, 32GB RAM, **CPU-only inference** (no GPU) — both
models were chosen specifically because they run acceptably on CPU alone.

- **First run** converts, chunks, and embeds every filing in `data/` and builds the index
  (`rag.markdown.db` for the default chunking strategy) from scratch. On the hardware above, that's
  roughly **10 minutes** for ~1,440 chunks across the 4 included filings.
- **Every run after that** finds the existing index and skips straight to the interactive loop —
  well under a minute to start.
- Each answer involves one local `llama3.1:8b` generation call, CPU-only. A question whose excerpts the model
  hasn't read before takes **about a minute or more**: on the hardware above, ~50 s to read a ~2,400-token prompt
  (~45 tokens/s) and ~20 s to write the answer (~6 tokens/s). Repeated or overlapping questions are much faster,
  because Ollama keeps already-read prompts in a RAM cache and skips re-reading a shared beginning.

## Running it

```
dotnet run --project RagFilingExplorer.Local
```

Works from any directory - `data/`, `chunk-review/` and the index are located relative to the repo
root, not the directory you launch from. The first line of output names the chunking strategy and
index in use.

- `--rebuild` — deletes the current strategy's index and rebuilds from scratch. Needed after changing chunking/embedding
  *code*; changes to settings or filings are detected automatically (see below).
- `--verbose` — also prints the full ranked candidate list for each question (score, filing, statement
  type, heading, snippet). Useful when diagnosing a bad retrieval; not needed for normal use.
- `--chunks-only` — chunks every filing and writes `chunk-review/<strategy>/`, then exits: no Ollama, no
  index. The fast way to read real chunk output after a chunking change.

Type a question at the `>` prompt; a blank line or `exit` quits.

### Startup checks

Before doing any work, the app checks for the problems most likely to trip up a fresh clone, and
exits with a one-line fix instead of a stack trace:

- Ollama isn't reachable at the configured URL, or either configured model isn't pulled (the error
  prints the exact `ollama pull` command).
- The `markitdown` CLI isn't on `PATH` (only checked when an index build is actually needed).
- **The index can't be trusted.** A build writes `rag.<strategy>.db.manifest.json` only after every
  chunk has been embedded. It records the embedding model, the chunking strategy and settings, and a
  SHA-256 hash of every filing. If the index has no manifest, the last build was interrupted or failed. If the manifest doesn't match
  the current `appsettings.json` and `data/`, the index is stale. Either way the app says exactly what's
  wrong and asks for `--rebuild` rather than silently answering from a partial or mismatched index.
  (A changed embedding model is the worst case: query vectors from the new model compared against
  stored vectors from the old one retrieve noise with no error at all.)
- Each filing registers its company for the company filter from its own tagged cover facts (name and
  ticker); a filing that tags no registrant name stops startup with a message saying so.

## Configuration (`appsettings.json`)

The tunable knobs — model names, Ollama's base URL/timeout, chunk size/overlap, tokenizer model,
vector-store upsert batch size, and retrieval top-K/temperature — live in
[`RagFilingExplorer.Local/appsettings.json`](RagFilingExplorer.Local/appsettings.json), not hardcoded
in `Program.cs`. Every key is required: the app validates on startup that each one is actually present
and fails with a clear error naming the missing key, rather than silently falling back to some other
default hiding in code.

**Do not change `VectorStore.UpsertBatchSize` above `1`** as long as this project is pinned to
`CommunityToolkit.VectorData.SqliteVec` `1.0.1-preview` (see the `.csproj`). That version's `vec0`
upsert workaround throws `SQLite Error 1: 'UNIQUE constraint failed on vec_chunks primary key'` on any
multi-record batch — reproducible on the very first batch against an empty table, so it isn't a real
duplicate-key issue in the data. It's a known, already-fixed upstream `sqlite-vec` bug that the NuGet
package just hasn't picked up yet. Full details, including why manually swapping in the newer native
`vec0.dll` was considered and rejected, are in
[docs/Decision-Log.md](docs/Decision-Log.md) ("Follow-up: persisted vector store"). If
this project ever upgrades past that SqliteVec version, re-check whether the fix landed before raising
this value — batching does meaningfully reduce embedding calls otherwise.

### Chunking strategies

`Chunking.Strategy` selects how filings are turned into chunks. Each strategy builds its own index
(`rag.<strategy>.db`) and chunk dumps (`chunk-review/<strategy>/`), so once both are built, switching is
a one-line settings change with no re-embedding - which is what makes side-by-side comparison practical.

- **`Markdown`** (default) - `markitdown` converts the whole filing to Markdown, sections are found from
  the plain-text Item headings, and oversized Markdown tables are split with their header, fiscal-period
  row and row-group labels repeated on every piece.
- **`Linearized`** - each HTML table is first turned into self-contained lines ("Comprehensive income —
  2026: $133,812 | 2025: $104,075") by `HtmlTableLinearizer`, working from the HTML because `markitdown`
  discards `colspan`. A split table repeats its statement title, units and period caption on every piece.
  A table it can't linearize unambiguously - or would lose any cell text from - falls back to the
  Markdown handling. 999 chunks vs 1,444. Ties `Markdown` on the original 24 questions; on 10 targeted
  questions (mid-table rows, split layouts, MD&A/notes tables) it gets the answer into the model's
  context for 7 vs 4 and answers 5 vs 4 correctly, with named trade-offs - see
  [docs/Decision-Log.md](docs/Decision-Log.md), "targeted questions and a rank metric". The final manual
  pass confirmed it (22/24 reliable on both, 5 vs 4 targeted) but Linearized missed two questions `Markdown`
  answers, so `Markdown` stays the default ("manual pass (v1)").

Everything after chunking (statement-type tagging, embedding, retrieval, generation) is shared, so a
strategy only has to implement `IChunkingStrategy`.

### Reasoning-model support

Models like `deepseek-r1`, `phi4-reasoning`, or `qwen3.5` emit an internal chain-of-thought ("thinking")
separately from their final answer. This app supports that deliberately, not just tolerates it, after
`qwen3.5:2b` (tested as a reference model, not the shipped default) exposed two real bugs:

1. Given this app's longer retrieved-context prompts, the model burned its entire generation budget on
   chain-of-thought and produced **no answer at all** — Ollama's own streaming response keeps `thinking`
   and `content` in genuinely separate fields, and OllamaSharp maps `thinking` into a distinct
   `TextReasoningContent` item that `ChatResponseUpdate.Text` doesn't include, so nothing upstream even
   noticed.
2. Naively sending a "think" request to a model that doesn't support reasoning at all doesn't get
   ignored — Ollama rejects it outright with a hard error (`"<model>" does not support thinking`), which
   took down the entire interactive session the first time it happened.

What ships now, in `RagAnswerService`, `OllamaSetup` and `InteractiveSession`:

- **`Retrieval.ReasoningEffort`** (one of `None`/`Low`/`Medium`/`High`/`ExtraHigh`) is only applied to
  questions `QueryIntentResolver.RequiresSynthesis` flags as needing genuine multi-step reasoning
  (comparisons, ratios, trends) — a plain single-fact lookup always uses `Effort.None`, so reasoning is
  never wasted on a task that doesn't need it.
- **Capability check at startup**: `OllamaSetup` calls Ollama's own `/api/show` for the configured
  `ChatModel` and only ever routes a question to reasoning if `"thinking"` is actually in that model's
  capability list — never assumed from the model name.
- **`Retrieval.MaxOutputTokens`** sets an explicit output ceiling for every chat model (thinking plus
  answer, for a reasoning model), instead of relying on Ollama's own default, which is exactly what let
  the budget-exhaustion bug happen silently. It shares Ollama's context window (`num_ctx`, 4096 by
  default) with the prompt, so it's sized at 768: a reasoning model gets little room to think unless
  `num_ctx` is raised too.
- **A starved-response guard**: if a model still hits that ceiling without ever producing real answer
  text, the app fails with a clear, specific error instead of showing an empty answer.
- **The interactive loop no longer dies on one bad turn**: any failure during a single question's
  generation (a starved response, an unsupported request, a dropped connection) is caught, reported, and
  the session continues to the next question.

Apart from the output ceiling, all of this is a no-op for a non-reasoning model like `llama3.1:8b` — it
reports no `"thinking"` capability, so `ReasoningEffort` never applies to it regardless of question or
configuration.

### Adding a new filing

Dropping a new `.html` file into `data/` (the app will then ask for `--rebuild`) is necessary but **not sufficient** -
onboarding Netflix (`NFLX-10K-2025.html`) surfaced three real bugs (and a later review found a fourth, in Nasdaq's filing), all fixed generically rather than
with filer-specific code, but worth checking for explicitly with any new filing:

1. **Check the company registered.** An unregistered company runs every question naming it **unfiltered
   across every filing** (the exact cross-company contamination metadata filtering exists to prevent) -
   the most consequential of the NFLX bugs: it caused a hallucinated figure, not just a missed answer.
   v1 kept a hand-written name/ticker table (`QueryIntentResolver.CompanyToFiling`); since v2 each filing
   registers itself from its tagged cover facts (`CompanyRegistry`: registrant name without its legal form,
   plus the common stock's ticker). Startup prints each registration ("Company filter: Netflix / NFLX ->
   NFLX-10K-2025.html") - check the new one reads as questions will name the company.
2. **Don't assume the source is UTF-8.** A raw EDGAR download usually is, but a browser-saved copy can
   declare (and genuinely be encoded as) something else entirely - Netflix's was `windows-1252`.
   `MarkItDownConverter.DetectEncoding` handles this automatically now (BOM, then the file's own
   `<meta charset>`, then a UTF-8 fallback), but it's worth spot-checking `chunk-review/<strategy>/*.chunks.txt`
   for stray `�` characters after a first run regardless.
3. **Item-heading punctuation varies by filer.** Netflix's converted output has no space after the
   period in most Item headings (`"Item 1.Business"` vs. the usual `"Item 1. Business"`) -
   `SectionSplitter.TitledItemHeaderRegex` now tolerates both, but a filer with a still-different
   convention could reintroduce this class of bug. Check `chunk-review/<strategy>/<new-filing>.chunks.txt` for a
   complete, correctly-nested Item outline before trusting the citations it produces.
4. **Statement titles vary too.** Statement-type filtering only works if each financial statement's
   title line is recognized - Nasdaq's "Consolidated Statements of *Changes in* Stockholders' Equity"
   wasn't at first, so every Nasdaq equity question found nothing. After a first run, check that the index
   tags each of the new filing's statements (see `docs/Implementation_Plan.md`, "Live constraints").

Full diagnostic detail, including how each bug was actually found, is in
[docs/Decision-Log.md](docs/Decision-Log.md) ("Follow-up: onboarding a new filer (NFLX)").

## Known limitations

- **Derived figures aren't reliable.** Sums, differences and ratios are computed by the model, which predicts
  digits rather than calculating: asked to add three 8-digit figures, it gave a slightly wrong total every time,
  across four phrasings. Check any figure the filing doesn't state directly.

- **Near-identical lines can be swapped.** Where a table has two lines for almost the same thing - Nasdaq's
  "Comprehensive income" and "Comprehensive income attributable to Nasdaq" - the model sometimes gives the
  other line's figure under the asked-for name. Three prompt wordings didn't fix it; check the cited line.

- **Statement routing is a hard filter.** A question containing a financial-statement term (revenue,
  net income, operating margin, total assets, cash flow, …) is searched only within that statement.
  Answers that live elsewhere in the filing can't be retrieved: segment or regional breakdowns, MD&A
  explanations, accounting policies, or a term from a different statement ("deferred revenue" is on
  the balance sheet). In testing the model declined these rather than guess, but the miss is by design.
  A soft filter was measured and not built in v1. On the v2 branch, hybrid search (`Retrieval:Search = Hybrid`:
  keyword + vector search, the statement type a boost rather than a filter) answers these routing misses - see
  [docs/Design-FAQ.md](docs/Design-FAQ.md) and [docs/Decision-Log.md](docs/Decision-Log.md), "XBRL hybrid (v2)".

## What I'd do differently

- **Start from the data, not the tool.** The pipeline was chosen before the filings were studied: a document
  library first, and when that failed, its Markdown converter. An hour with the source would have shown that
  every 10-K follows a structure fixed by regulation (Parts and Items) and tags every financial figure in inline
  XBRL with its concept, period, unit and scale. Much of the later work - recovering table structure, carrying
  units onto split tables, detecting statements from their titles - rebuilds information the filings already
  state. The v2 plan starts there ([Decision-Log.md](docs/Decision-Log.md), "XBRL hybrid (v2)").
- **Pick the stack from strengths, the pipeline from the data.** .NET was the right choice and covers every part
  of that design. The assumption was the conversion step - which is also what brought in Python.
- **Keep what's searched separate from what the model reads.** Tables were embedded and shown in the same
  Markdown form; roughly half of a financial statement's tokens turned out to be empty cells, slowing every answer
  and giving the model noise to count through.
- **Build the evaluation early, and grade strictly from the start.** The question set and the retrieval replay
  are what made every decision here measurable. But an earlier, looser grading scored the main questions 24/24;
  requiring the unit and the exact line exposed eight problems. And no question touched Microsoft's cover page,
  so a bug that silently dropped it survived every test.
- **Keep arithmetic out of the model.** It predicts digits rather than calculating; sums and ratios belong in
  code, called as a tool.

## Testing

```
dotnet test
```

Runs `RagFilingExplorer.Local.Tests` (NUnit + Moq) — 350 tests, fully offline, no live Ollama instance
or populated vector store required. Covers chunking, section splitting, statement-type detection,
query-intent resolution, settings loading/validation, index-manifest staleness detection, the
retrieve+generate orchestration (mocked), and the v2 inline XBRL reader - checked against the filings in `data/`,
read-only.

This is separate from, and doesn't replace, the real-question retrieval-quality testing documented as
Step 7 in the decision log — that required manually verifying actual answers against the source
filings, and is what actually caught this project's real bugs.
[docs/Manual-Test-Questions.md](docs/Manual-Test-Questions.md) has a broader set of questions (balance
sheet, cash flow, equity, comprehensive income, plus edge cases) for exactly that kind of manual pass,
each with an expected answer sourced directly from the filings.

## Why MarkItDown, not Microsoft.Extensions.DataIngestion

`Microsoft.Extensions.DataIngestion` (MEDI) was the original plan for document reading and chunking,
and was deliberately tried before being abandoned — not skipped. In short: MEDI's document readers
don't handle raw HTML directly, its heading-based chunkers have nothing to key off because SEC EDGAR
HTML has zero real `<h1>`–`<h6>` heading tags, and its hardcoded Markdig math extension crashes on
dollar-figures in financial tables. The full diagnostic path is in
[docs/Decision-Log.md](docs/Decision-Log.md) (Step 3).

What ships instead is a small hand-written pipeline: `markitdown` (the CLI) for HTML→text conversion,
then pattern-matching over the converted text for section boundaries and table-aware token chunking.

## Project structure

```
RagFilingExplorer.Local/                the app - chunking, retrieval, vector store, interactive loop
RagFilingExplorer.Local.Tests/          NUnit + Moq test suite
data/                                   source 10-K filings (HTML, from sec.gov/edgar), plus each filing's XBRL
                                        taxonomy (.xsd, and _pre/_lab/_cal/_def.xml where not embedded) for v2
chunk-review/<strategy>/                full per-chunk text dumps, one file per filing, for manual review
tools/                                  manual-question list; replay_recall.py (deterministic retrieval ranks);
                                        LinearizeSpike + xbrl_column_check.py (table linearization + its XBRL check)
.claude/                                Claude Code config: filer-onboarding-checker subagent, test-convention rule
docs/Implementation_Plan.md             current-state reference: ground rules, pipeline, live constraints
docs/Decision-Log.md                    the full build history: every step, decision point, and debugging path
docs/Manual-Test-Questions.md           a broader question set for manual retrieval-quality testing
docs/Design-FAQ.md                      short answers to "why not X?" design questions, linked to the log
```

## Further reading

[docs/Implementation_Plan.md](docs/Implementation_Plan.md) is the short current-state reference;
[docs/Decision-Log.md](docs/Decision-Log.md) has the complete build history and decision log — every dead end and bug found along the way (MEDI's abandonment, a SqliteVec upsert bug, the
statement-type detector false positives that silently mistagged 96 chunks, three real bugs found
onboarding a fourth filing, and more). This README is deliberately the short version.
