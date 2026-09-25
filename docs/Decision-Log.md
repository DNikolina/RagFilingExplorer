# Decision Log: SEC 10-K RAG Tool

The complete build record, moved verbatim from `Implementation_Plan.md` when that file was trimmed to a
current-state reference: every step's original instructions and outcome, every follow-up, and every
dead end - in the order they happened. Section headings are unchanged, so existing references like
"Follow-up: persisted vector store" still find the right section here.

"Above"/"below" references inside each section refer to this file. The "already-validated facts" list
and the live constraints stay in [Implementation_Plan.md](Implementation_Plan.md). Short answers to the
"why not X?" questions, each pointing back here, are in [Design-FAQ.md](Design-FAQ.md).

## Index

One line per section, in file order. Quote a section's name to reference it - names never change.

- **Step 1: Source data** - MSFT, ORCL, NDAQ 10-Ks from EDGAR (NFLX added later).
- **Step 2: Project setup** - SDK pinned; the package set, with pointers to what later follow-ups changed.
- **Step 3: Chunking** - done. MEDI tried and abandoned; hand-written markitdown -> sections -> token-chunker pipeline.
- **Step 4: Vector storage** - done, superseded in part (in-memory store); the `Content`/`Text` and timeout fixes still hold.
- **Step 5: Retrieval** - superseded: retrieval now runs filtered, in `RagAnswerService`.
- **Step 6: Answer generation** - citation-grounded prompt to `llama3.1:8b`.
- **Step 7: Testing** - done: 6/6, from 2/6 (the path is in "retrieval quality").
- **Step 8: Publish** - README written; push on hold until the user's manual pass.
- **Completion checkpoint** - Steps 7 and 8 done means the project is complete.
- **Follow-up: persisted vector store** - done. SqliteVec on disk; an upstream multi-record upsert bug forces batch size 1.
- **Follow-up: retrieval quality** - done. Prefixes, enrichment and smaller chunks didn't help; company + statement-type filtering did, after fixing a detector false positive that mistagged 96 chunks.
- **Considered and declined: PDF instead of HTML as the source format** - declined: cohesive tables, but ~15x slower and no fix for the hard problems.
- **Follow-up: unit test coverage** - done. Offline NUnit + Moq suite for the logic every earlier bug lived in.
- **Follow-up: repository review and Program.cs refactor** - done. `comprehensive_income` routing, `--verbose`, markitdown stderr, Program.cs restructured.
- **Follow-up: configurable settings** - done. `appsettings.json`, every key required and validated at startup.
- **Follow-up: reasoning-model support** - done. Thinking content handled, capability check at startup, starved-response guard, a failed turn no longer ends the session.
- **Follow-up: onboarding a new filer (NFLX)** - done. Three bugs: Item headings without a space, windows-1252 encoding, a missing company registration.
- **Follow-up: second repository review** - done. Repo-root paths, build manifest (interrupted or stale index), registration warning, config cleanup, readable startup errors.
- **Follow-up: statement-boundary chunking** - done. Statement titles start sections; NDAQ's equity statement had never been detected.
- **Follow-up: equity routing and title captions** - done. Period-end equity routes to the balance sheet; titles ride on a table's first piece.
- **Follow-up: trailing remainders, per-company search, table-piece headers** - done. Footer chunks merged, one search per named company, temperature 0.2 -> 0, two table-splitting fixes.
- **Follow-up: linearized tables as a second chunking strategy** - done. Tables linearized from the HTML (colspan-aware); XBRL as a test oracle only, not at runtime.
- **Follow-up: embedding-model comparison** - deferred; candidate models and their templates recorded.
- **Follow-up: targeted questions and a rank metric** - done. Linearized puts the answer in context 7/10 vs 4/10 but answers only one more right; `Markdown` stays default; scope stops here.
- **Follow-up: pre-manual-pass review** - done. Lost first chunk (key 0), back-matter headings, output ceiling; soft filter measured, not built; Program.cs split.
- **Follow-up: XBRL hybrid (v2)** - planned, after the v1 push: XBRL section labels, then figure lookup, then a router - phased, measured.

---

## Step 1: Source data
- Get 2–3 public 10-K filings from sec.gov/edgar, saved to `/data`
- Pick companies the user understands reasonably well — makes answer-checking easier later
- Ask the user which companies/filings to use if not already decided; don't pick arbitrarily

## Step 2: Project setup
**Pin the SDK version first.** The user has .NET SDK `10.0.400` installed (confirmed via
`dotnet --list-sdks`). Create a `global.json` at the repo root before scaffolding anything, so the
project doesn't depend on whatever SDK happens to resolve first:
```json
{
  "sdk": {
    "version": "10.0.400",
    "rollForward": "latestFeature"
  }
}
```
Then create `RagFilingExplorer.Local` as a .NET console app targeting `net10.0` explicitly
(`dotnet new console -n RagFilingExplorer.Local --framework net10.0`), rather than leaving the
target framework to default. Add these packages:
- `Microsoft.Extensions.AI` + `Microsoft.Extensions.AI.Abstractions`
- `OllamaSharp` — confirmed via Microsoft's own docs/code samples to implement `IChatClient` and
  `IEmbeddingGenerator` directly against Ollama's local API. No custom wrapper needed here.
- `Microsoft.Extensions.VectorData.Abstractions` (stable/GA)
- `CommunityToolkit.VectorData.InMemory` (stable/GA) — the in-memory vector store provider

**Update after Step 3:** `Microsoft.Extensions.DataIngestion` (and its `.MarkItDown`/`.Markdig`
reader packages) was tried and abandoned — see Step 3's outcome. It is not part of the final
package set. What Step 3 actually added instead:
- `Microsoft.ML.Tokenizers` + `Microsoft.ML.Tokenizers.Data.Cl100kBase` — offline GPT-4-encoding
  tokenizer, used only to count/bound tokens per chunk. The `.Data.*` package matters: without it,
  `TiktokenTokenizer.CreateForModel` fetches vocab over the network at runtime, which would quietly
  break the "fully local" pitch.
- `Microsoft.Bcl.Memory` pinned to `10.0.12` directly — a transitive dependency pulled in by the
  tokenizer data package resolved to a version (`9.0.4`) with a known high-severity vulnerability
  (GHSA-73j8-2gch-69rq); pinning to the newer GA version silences it. Check `dotnet list package
  --vulnerable --include-transitive` after any future package changes.

**Update after later follow-ups:** `CommunityToolkit.VectorData.InMemory` was replaced by
`CommunityToolkit.VectorData.SqliteVec` ("persisted vector store"), and `Microsoft.Extensions.Configuration`
("configurable settings") and `AngleSharp` ("linearized tables as a second chunking strategy") were added.
The current package set is in Implementation_Plan.md, "Pipeline as it ships".

No API keys, no `dotnet user-secrets` needed — everything runs locally.

## Step 3: Chunking — DONE, outcome below

**MEDI was tried and abandoned. This is now a hand-written pipeline, not `Microsoft.Extensions.DataIngestion`.**
Two independent, unfixable-without-disproportionate-effort blockers, in the order they were hit:

1. **Reader gap.** MEDI ships exactly two document readers: `MarkdownReader` (needs literal Markdown
   input) and `MarkItDownReader` (shells out to the Python `markitdown` package). Our filings are raw
   HTML, so only `MarkItDownReader` applies — which meant installing Python + `pip install markitdown`
   as a new toolchain dependency, on a machine that had neither. (This was surfaced to the user as a
   decision point before installing; they chose to proceed.)
2. **Structural + hard-crash gaps once the reader worked.**
   - SEC EDGAR HTML has **zero semantic heading tags** (`<h1>`-`<h6>` count: 0, confirmed by direct
     grep on the source). "Item 1. Business" is a bold-styled `<p>`, not a heading. MEDI's chunkers
     (`SectionChunker`, `HeaderChunker`) key off headings that simply don't exist in the parsed
     document — no configuration fixes this, it's a property of the input.
   - Even after working around that (injecting synthetic Markdown headers before re-parsing), MEDI's
     `MarkdownParser.Parse` **hardcodes** `MarkdownPipelineBuilder().UseAdvancedExtensions()` with no
     exposed way to disable individual Markdig extensions. Its math extension misreads dollar-figures
     in financial tables (e.g. `$1,234`) as inline-math delimiters and throws
     `NotSupportedException: Inline type 'MathInline' is not supported` — an unhandled crash, not a
     quality issue, and 10-K financial statements are wall-to-wall dollar figures.

Also worth knowing for anyone touching this again: **different SEC filing agents produce structurally
different HTML.** MSFT and ORCL share an `id="item_1_business"`-style anchor convention; NDAQ uses
an entirely different generator with GUID-style IDs and no semantic anchors at all. Any approach that
leans on a specific filer's HTML conventions (IDs, CSS classes) will silently break on filings from a
different agent. The approach below avoids this by working from the *converted, plain-text* output
instead, which is far more uniform across filers.

**What actually ships** (see `RagFilingExplorer.Local/Chunking/`):
1. `MarkItDownConverter` — shells out to the `markitdown` CLI directly (bypassing MEDI's reader
   entirely) to get raw converted text. First strips the hidden inline-XBRL metadata block
   (`<ix:header>...</ix:header>`) from the source HTML before conversion — left in, it dumps ~40,000
   characters of taxonomy garbage as the first "paragraph" of every filing.
2. `SectionSplitter` — pattern-matches "PART I" / "Item 1. Business"-style lines directly in the
   converted text to build a heading path per section (e.g. `PART II > Item 8. Financial Statements`).
   Filters page-header noise: some filers (MSFT) repeat "PART I" / bare "Item 1" as a running header
   on every page, which would otherwise register as dozens of false section boundaries — only the
   *first*, titled occurrence of each boundary counts; bare/repeated ones are dropped, along with lone
   page numbers and thematic-break markers.
3. `TokenChunker` — packs each section into ~500-token chunks (Tiktoken via `Microsoft.ML.Tokenizers`,
   50-token overlap between chunks) with a fixed prose paragraph as the unit. A contiguous run of
   Markdown table rows is treated as one atomic block so a table is never split mid-row-group. When a
   single block (a giant table, or a giant paragraph) exceeds the chunk budget on its own, it's split
   further:
   - For text: at the nearest word boundary under the token limit.
   - For tables: the header block (everything before the first real data — detected by the presence
     of `$`, a comma-grouped number, or a Markdown link, not just row position) is repeated on every
     split piece, **and** the nearest row-group label (e.g. "Cost of revenue:", detected as a row
     with text in only the first cell) is carried forward too. This was found and fixed by hand-
     checking actual output: an early version repeated only the syntactic 2-line Markdown header and
     lost row-group labels like "Cost of revenue:" across a split, silently reattaching numbers to
     the wrong line item.

Verified by manually reading chunk output (not just trusting summary stats) against MSFT, NDAQ, and
ORCL: complete/correct section outlines matching each filing's real table of contents, income
statement figures correctly retain both their row label and fiscal-year column context across a
split, and the exhibit index table (no row-group labels, ~100 rows of long text) no longer bloats
chunks by folding real data rows into a repeated "header." Full chunk dumps are in `chunk-review/`
(one `.txt` per filing) for manual review — this predates any test-question work in Step 7 and isn't
a substitute for it.

Both original requirements still hold and are met by the above:
- A chunk containing a number contains what that number refers to (nearest heading + row-group label).
- Each chunk carries its source filing name — currently as `FilingChunk.SourceFiling` (the file name)
  plus `FilingChunk.Heading` (the section path); Step 4 should persist both.

**Post-review finding (caught by manually diffing NDAQ's chunk output against the two other filings):**
different filing agents draw the same purely-decorative element (e.g. a horizontal rule line on the
cover page) using different HTML techniques, and markitdown doesn't treat them equivalently:
- MSFT/ORCL use an empty `<p>` styled with `border-top`/`border-bottom` — markitdown recognizes it has
  no real content and silently drops it.
- NDAQ uses an empty `<table>` with a bordered `<td>` for the same visual effect, plus a separate
  divider drawn as a literal run of underscore characters at 4pt font. markitdown always emits full
  Markdown table syntax for any `<table>` tag regardless of content, and always extracts real text
  regardless of font size — so both leaked into chunk output as fake "content" (a degenerate blank
  table, an escaped underscore-rule line like `\_\_\_\_...`).

Fixed generically (not filer-specific): `SectionSplitter` drops lines that are purely a decorative
rule (underscores/dashes/etc., with or without Markdown's backslash-escaping); `TokenChunker` drops
any table block where every row, once `|`/`-`/whitespace are stripped, has nothing left. Verified this
doesn't over-trigger on legitimate content - e.g. MSFT's signature-block tables mix blank spacer rows
with real text (names, titles) and correctly survive, since the filter only drops a table when *every*
row across the whole block is blank.

## Step 4: Vector storage — DONE, outcome below

**Superseded in part - read as history.** The in-memory store, the batches of 25, the code sample and the
1,073-chunk count below were all replaced later: the index is persisted with SqliteVec, one per chunking
strategy, and upserted one record at a time ("persisted vector store"); records carry `StatementType`
("retrieval quality"); keys start at 1 ("pre-manual-pass review"). The two corrections below - `Content`
vs `Text`, and the `HttpClient` timeout - still hold.

Uses `Microsoft.Extensions.VectorData`'s `InMemoryVectorStore`, configured with an
`IEmbeddingGenerator<string, Embedding<float>>` backed by `OllamaSharp` + `nomic-embed-text`, as
planned. `nomic-embed-text`'s 768-dimension output was directly confirmed via Ollama's own API
(`GET /api/tags` → `"embedding_length":768`), not just assumed. Two corrections to the original
sample, both found by actually running it against all 1,073 chunks rather than trusting the sample
compiled/looked right:

1. **A `[VectorStoreVector]` string property cannot be read back after storage.** Per
   `Microsoft.Extensions.VectorData`'s own docs: "these vector properties do not support retrieving...
   the original text." The original sample used a single `Text` property as both the embedding source
   *and* what Step 5 reads back (`result.Record.Text`) — that would silently return empty/null. Fixed
   by splitting into two properties: `Text` (embedding source, write-only in practice) and `Content`
   (plain `[VectorStoreData]`, the actual readable chunk — set to the same string on upsert, but this
   one is what retrieval actually reads).
2. **`HttpClient`'s default 100-second timeout isn't enough for bulk local embedding.** A single
   `UpsertAsync` call with all 1,073 records at once hit `TaskCanceledException: HttpClient.Timeout of
   100 seconds`, since CPU-only `nomic-embed-text` inference for that many chunks takes several
   minutes. Fixed two ways together: construct `OllamaApiClient` with an explicit
   `HttpClient { Timeout = TimeSpan.FromMinutes(5) }` instead of the URI-only constructor, **and**
   upsert in batches (25 records at a time) rather than one giant call — this also gives visible
   progress instead of a silent multi-minute wait.

See `VectorStore/FilingChunkRecord.cs` and `Program.cs` for what actually ships:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using CommunityToolkit.VectorData.InMemory;
using OllamaSharp;

HttpClient ollamaHttpClient = new() { BaseAddress = new Uri("http://localhost:11434/"), Timeout = TimeSpan.FromMinutes(5) };
IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = new OllamaApiClient(ollamaHttpClient, "nomic-embed-text");

var vectorStore = new InMemoryVectorStore(new() { EmbeddingGenerator = embeddingGenerator });
var collection = vectorStore.GetCollection<int, FilingChunkRecord>("chunks");
await collection.EnsureCollectionExistsAsync();

// Upsert in batches of 25, not all 1,073 at once.
foreach (var batch in records.Chunk(25))
{
    await collection.UpsertAsync(batch);
}

class FilingChunkRecord
{
    [VectorStoreKey] public int Key { get; set; }
    [VectorStoreData] public string SourceFiling { get; set; }
    [VectorStoreData] public string Heading { get; set; }
    [VectorStoreData] public string Content { get; set; } // the actual retrievable chunk text
    [VectorStoreVector(dimensions: 768)] public string Text { get; set; } // embedding source only, not retrievable - set equal to Content on upsert
}
```

Do not manually call `GenerateAsync` before storing — the store calls the configured embedding
generator automatically on upsert and on search.

**Verified working:** all 1,073 chunks across the three filings embedded and upserted successfully
(~366s / ~6 minutes total, CPU-only). A smoke-test query ("What was total revenue?") returned
correctly-ranked, relevant results (cosine scores 0.69→0.67, all from the actual income statement
section) with readable `Content` — confirming the full round-trip (embed → store → search →
retrieve-readable-text) works end to end. This was a plumbing check, not Step 7's real test-question
validation — that's still separate work.

## Step 5: Retrieval

**Superseded - read as history.** Retrieval now runs in `RagAnswerService`, filtered by company and
statement type from `QueryIntentResolver` ("retrieval quality"), with one search per company when a
question names several ("trailing remainders, per-company search, table-piece headers").

```csharp
await foreach (var result in collection.SearchAsync(userQuestion, top: 5))
{
    // result.Record.Content (not .Text - see Step 4's outcome), result.Record.SourceFiling,
    // result.Record.Heading, result.Score
}
```
No manual cosine similarity code — this is handled by the store.

## Step 6: Answer generation
Use `OllamaSharp`'s `IChatClient` (backed by `llama3.1:8b`) directly — no wrapper needed.
- Prompt the model to answer **based only on the retrieved chunks**, and to **cite which filing (and
  section, if tracked) backed each part of the answer**. This citation requirement is not optional
  polish — it's the strongest single demonstration of RAG grounding in the whole project.
- **Known risk to watch for in testing:** local models are more prone than hosted models to ignoring
  "stick to the provided context" instructions and answering from training knowledge instead. If
  this shows up, tighten the prompt (explicit repeated instruction, consider lowering temperature)
  before concluding retrieval itself is broken.
- Print the answer with citations to the console.

## Step 7: Testing — DONE, outcome below

Six real questions with expected answers, verified directly against the source filings before
running anything (three number-lookup questions, one per filing; one prose fact; one prose
description; one deliberate negative/out-of-scope test): see the "Follow-up: retrieval quality"
section below for the full diagnostic journey and final result.

**Final result: 6/6 correct**, after a real, multi-stage debugging effort — this did not pass on the
first attempt (2/6 originally), and getting to 6/6 required diagnosing and fixing genuine bugs across
chunking, retrieval, and generation, exactly as this step's original guidance anticipated. The
individual fixes and dead ends are documented in the follow-up section so the diagnostic path isn't
lost - number-lookup questions in particular went through several rounds of "looks like a fix, verify
it actually helped" before landing on the real cause (a tagging bug, not a retrieval-model or
chunk-size problem as first suspected).

## Step 8: Publish
- Push to a public GitHub repo
- Write a README covering: what it does, stack used (C#/.NET, Microsoft.Extensions.AI, Ollama,
  local models, SEC EDGAR public data), and lead with the differentiator — **zero cost, no API keys
  or accounts required to run**
- Note the specific models and rough hardware expectations, so a cloner knows what they're pulling
  before multi-GB model downloads
- **Update after Step 3: this is "zero cost," not "zero setup."** Chunking requires Python +
  `pip install markitdown` as a prerequisite alongside the .NET/Ollama stack — list it plainly next
  to the other prerequisites so a cloner isn't surprised.
- MEDI (`Microsoft.Extensions.DataIngestion`) was tried and **not** used in the end — say so plainly,
  and briefly why (see Step 3's outcome), rather than silently leaving no trace of it. This is a
  legitimate, documented engineering decision, not something to gloss over.

**Status: README.md written**, covering everything above plus the config system and reasoning-model
support added after this step was first drafted (see the follow-ups below). **Not yet pushed** — the
user asked to hold the push until after their own manual testing pass (`docs/Manual-Test-Questions.md`),
so the repo is still local-only as of this writing.

## Completion checkpoint
**When Step 7 passes and Step 8 is done: this project is complete.** Report this clearly to the user
and stop. Nothing further is assumed or owed beyond this.

---

## Follow-up: persisted vector store — DONE, outcome below

**The problem this solved:** `InMemoryVectorStore` has no persistence, so every run re-chunked all
three filings and re-embedded every chunk via Ollama from scratch (6-15 minutes depending on chunk
size) before any retrieval could happen. That made Step 7's retrieval-quality debugging (search
prefixes, chunk-content enrichment, chunk size — none of which fixed the underlying "sparse tables
lose to narrative prose" retrieval problem, see below) slow and expensive to iterate on.

**What shipped:** swapped `InMemoryVectorStore` for
[`CommunityToolkit.VectorData.SqliteVec`](https://www.nuget.org/packages/CommunityToolkit.VectorData.SqliteVec/)
1.0.1-preview, writing to an on-disk `rag.db` (`.gitignore`d, alongside `bin/`/`obj/` which also
weren't ignored before this). `Program.cs` now checks for the file at startup: if it exists, chunking
and embedding are skipped entirely and the app goes straight to the interactive retrieval+generation
loop; if not, the full pipeline runs as before and creates it. A `--rebuild` flag deletes the file
first, for deliberately picking up a chunking/embedding change. `FilingChunkRecord` needed **zero**
changes — the earlier "not verified" inference (that the `string`-typed auto-embedding pattern is
provider-agnostic, based on one stack trace) turned out correct, confirmed properly this time via the
connector's own `StringVectorFixture` conformance test on GitHub. The native `sqlite-vec` extension's
Windows binary (`runtimes/win-x64/native/vec0.dll`) also loaded with no manual setup, as its NuGet
package promised.

**A real bug found and worked around:** `CommunityToolkit.VectorData.SqliteVec` 1.0.1-preview throws
`SQLite Error 1: 'UNIQUE constraint failed on vec_chunks primary key'` on **any** multi-record
`UpsertAsync` batch — reproduced crashing on the very first batch of all-new keys against an empty
table, so it's not a real duplicate-key issue in our data. Root cause, confirmed by reading the
connector's own source: `vec0` (the SQLite vector virtual table) has no native `UPSERT`, so the
connector works around it with delete-then-insert — a pattern that appears to only be exercised
correctly for single-record batches, not multi-record ones. **Confirmed this is a known, already-fixed
upstream bug**: `sqlite-vec` v0.1.10-alpha.3 (["Proper `INSERT OR REPLACE INTO` support"](https://github.com/asg017/sqlite-vec/releases/tag/v0.1.10-alpha.3))
fixes exactly this, but the NuGet `sqlite-vec` package has never been updated past `0.1.7-alpha.2.1`
(the version that ships transitively with `CommunityToolkit.VectorData.SqliteVec` 1.0.1-preview), so
the fix isn't reachable through normal package management. Manually swapping in the newer native
`vec0.dll` was considered and rejected: it would work locally but silently regress for anyone else
who clones the repo and runs a normal `dotnet restore`, trading a documented, reproducible workaround
for a fragile, unreproducible one. **Fix**: batch size 1 (see `Program.cs`, the `batchSize` constant).
Confirmed this isn't a meaningful performance cost either way - ~4-5 records/sec on this hardware
whether batched or not, since `Microsoft.Extensions.VectorData` unified batch upserts into a single
`UpsertAsync(IEnumerable<TRecord>)` overload (there's no separate, faster `UpsertBatchAsync` in the
current API generation - that name only exists in the older, deprecated `Microsoft.SemanticKernel.Connectors.*`
package family), and per that overload's own doc remarks it's *supposed* to batch the embedding
generation call itself; our per-record workaround forgoes that, but it wasn't the bottleneck here.

**Verified end to end**, isolating the storage swap from the retrieval-quality experiments running in
parallel (chunk size, in particular, needed to be held constant at the original 500 tokens for this
check to mean anything - an earlier verification attempt that left it at the experimental 256-token
size gave a false scare, see below):
- First run (no `rag.db`): full pipeline runs, creates the file (1,073 chunks at 500 tokens, 403s).
- Second run (`rag.db` exists): skips straight to the interactive loop - **42.7s total** including one
  full `llama3.1:8b` generation, versus 400s+ before. This is the actual payoff.
- The same question that worked correctly back in Step 6 ("What was Microsoft's total revenue for
  fiscal year 2026?" → "$331,839 million", correctly cited) returns the identical correct answer
  against the SQLite-backed store. The storage swap alone didn't change retrieval/generation quality.
- `--rebuild` correctly deletes and rebuilds.
- One cosmetic difference worth knowing, not a bug: `VectorSearchResult.Score` values are on a very
  different scale under SqliteVec (~0.2-0.3) than under `InMemoryVectorStore` (~0.65-0.80) for the
  same queries and same chunks - almost certainly a different distance/similarity formula or
  normalization between the two providers. Ranking order and final answer quality were unaffected;
  don't compare raw score values across providers.

## Follow-up: retrieval quality — DONE, 6/6 after a real multi-stage fix

The Step 7 problem - sparse financial tables losing to narrative prose in ranking for exact-number
questions - is now **solved**. Getting there took six rounds, four of which didn't work; recorded in
order since the dead ends are as informative as the fix.

**Tried and found insufficient on their own:** `search_query:`/`search_document:` prefixes (nomic-
embed-text's documented asymmetric-matching convention), row-label text enrichment of table chunks
(prepending detected row labels like "Total revenues" to the embedding input), and smaller (256-token)
chunk size (Azure's own guidance suggests this for precise fact retrieval). None of these moved the
needle on the actual blocking questions, and smaller chunks was mildly net-negative (more chunks
competing for the same fixed top-K).

**Metadata filtering (company-level) - real but narrow effect, and revealed a worse failure mode.**
`FilingChunkRecord.SourceFiling` marked `[VectorStoreData(IsIndexed = true)]`; a simple keyword match
against a company→filename map applies a `VectorSearchOptions<FilingChunkRecord>.Filter` (SqliteVec's
documented `EqualTo` support) when a question names exactly one company. Confirmed via logs: it fully
eliminated cross-company contamination (top-25 became 100% single-company for every filtered
question), but the pass count didn't move (3/6) - and one result got *worse*: Microsoft's gross-margin
question went from correctly declining to confidently stating a wrong number ($16.6 billion - the
*increase* amount, not the actual value) with a citation attached. Best explanation: removing the
cross-company noise left cleaner-looking but still insufficient context, and the model became more
willing to guess from it. A confident wrong answer is worse than an honest "I don't know" for a
project whose core value is grounding/citation trust - "the pass count didn't change" would have been
the wrong conclusion to draw here.

**Why company-level filtering wasn't enough:** the real blocker was never cross-company confusion -
it's that a single filing has many similarly-shaped tables (ORCL's dozens of "Item 15"
financial-statement tables) or multiple close-but-distinct line items for the same topic (NDAQ's Total
revenues vs. Net revenues, both in the same income statement). Filtering by filename narrows the
*document*, not the *statement or line item* within it.

**Metadata filtering (statement-type, stacked on company) - this is what fixed it, after fixing a real
bug in the detector first.** `StatementTypeDetector` (`Chunking/StatementTypeDetector.cs`) tags each
chunk by scanning for statement-title lines as chunks are built, carrying the detected type forward
(same "carry the nearest marker" pattern as `TokenChunker`'s row-group labels) until a new title line
appears or the filing changes. `FilingChunkRecord.StatementType` is a second indexed filter property,
combined with the company filter when a question's own wording clearly points at one statement (e.g.
"revenue"/"gross margin" → `income_statement`; "total assets" → `balance_sheet`).

The first version of the detector had a real, confirmed bug: to accommodate three different filer
conventions for the same statement titles - ORCL's `"CONSOLIDATED STATEMENTS OF OPERATIONS"`, MSFT's
`"INCOME STATEMENTS"` (no "CONSOLIDATED" at all, reversed word order), and NDAQ's `"Consolidated
Statements of Income"` (title case, "Income" not "Operations") - the regex made the word "STATEMENTS"
entirely optional. That meant a bare line reading
just **"OPERATIONS"** matched too - and MSFT's Item 1 Business section has exactly such a line, a
normal subsection heading ("Devices face competition... OPERATIONS We have a global operations
service center..."), completely unrelated to any financial statement. Confirmed by querying `rag.db`
directly (`SELECT Key, StatementType ... ORDER BY Key`, watching for transitions): this single false
positive mistagged **96 consecutive chunks** - all of Items 1 through 7 - as `income_statement` before
the real title line ever appeared in Item 8. The same flaw affected `CashFlowRegex` too ("Cash Flows"
alone is also a real MD&A subsection heading, mistagging 14 chunks inside Item 7). Fix: require
"STATEMENTS" to actually appear (as a prefix "STATEMENTS OF X" or suffix "X STATEMENTS") for every
statement type except balance sheet, whose real title in all three conventions never contains that
word at all (`"(CONSOLIDATED) BALANCE SHEETS"`) - so that one pattern is correctly left as-is.
Re-verified via the same direct SQL query after the fix: clean, tightly-localized transitions for all
three filings, no more false positives spanning unrelated Items.

**Result after the fix: 6/6 correct**, all with proper citations:
- MSFT gross margin: **"$225,465 million"** - correct, and the model showed its derivation (Total
  revenue $331,839M − Total cost of revenue $106,374M), both figures correctly pulled and cited.
- ORCL total revenues: **"$67,357 million"** - correct.
- NDAQ total revenues: **"$8,262 million"** - correct. This was the most persistent failure across
  every prior experiment (it kept finding "Net revenues" $5,249M instead) and is now right.
- MSFT CEO, NDAQ Index business, Apple negative test: all correct (as they had been through most of
  the prior rounds too).

Full arc across every experiment this session, for reference: baseline 2/6 → search prefixes 2/6 →
chunk enrichment 2/6 → smaller chunks (mixed, no net gain) → company-only filter 3/6 →
company+statement-type filter with the buggy regex 4/6 → **company+statement-type filter with the
regex fixed, 6/6**. The lesson underneath all of it: Microsoft's own retrieval-quality guidance
(metadata-based filtering ranked above chunk-size/text tweaks) was right, but "add a filter" alone
wasn't sufficient - the filter's own classification logic needed the same level of verification
(actually reading what it tagged, not just trusting the regex looked reasonable) as every other part
of this pipeline has needed throughout the project.

---

## Considered and declined: PDF instead of HTML as the source format

After Step 7 passed, tested whether converting from a PDF copy of the same filing (saved separately,
`data/MSFT-10K-2026.pdf`) produces meaningfully better `markitdown` output than the HTML source
already in use - a legitimate question given how much of this project's debugging effort went into
working around HTML-specific quirks (the hidden XBRL block, decorative-markup noise, inconsistent
per-filer conventions). **Declined** - not a clear win, tested on MSFT only via direct comparison of
the two converted outputs, no pipeline changes made.

**What was genuinely better about PDF:**
- The income statement converted as **one cohesive table block**, with row-group labels ("Revenue:",
  "Cost of revenue:") sitting directly next to their data rows - without the fragmented, multi-part
  structure the HTML conversion produced for the same table (the exact problem `TokenChunker`'s
  row-group-label-carrying logic, documented under Step 3, exists to patch).
- **No hidden XBRL metadata block** - that's an artifact specific to raw EDGAR HTML source, and
  doesn't exist in a rendered PDF at all, so there was nothing to strip.

**What was worse:**
- **~15x slower**: 78 seconds vs. ~5 seconds for the same filing (`markitdown` PDF conversion goes
  through a completely different code path - `pdfplumber`/`pypdfium2` positional text extraction
  rather than HTML/BeautifulSoup parsing). Minor at this project's scale (3 filings); would compound
  at any real scale.
- Less reliable table structure elsewhere in the document - e.g. the cover page's securities table
  flattened to plain space-separated text instead of a proper Markdown grid. PDFs have no `<table>`
  tag to anchor extraction to, unlike HTML.
- One PDF-parser warning surfaced (`Could not get FontBBox from font descriptor...`) - isolated, and
  didn't visibly corrupt the extracted content, but a reminder that PDF extraction has its own class
  of failure modes to watch for, not just HTML's.

**What was unchanged either way - the more important finding:**
- **Still zero Markdown headings.** Same as HTML - the entire `SectionSplitter` heading-detection
  approach (Step 3) would be needed regardless of source format; PDF doesn't make this easier.
- **The root cause of the hardest retrieval bug this session (Step 7's follow-up) is unaffected by
  format.** MSFT's PDF-converted text contains the identical ambiguous sentence pattern - "Gross
  margin increased $31.6 billion or 16%" (the *change* amount, not the value) - that caused the
  original confusion between a table value and its MD&A narrative discussion. That's a property of
  how 10-Ks are *written*, not how they're converted to text. The statement-type filtering fix would
  have been equally necessary on a PDF-sourced pipeline.

Given a working, fully-verified 6/6 pipeline already exists on HTML, and PDF's one real advantage
(table cohesion) doesn't offset its cost (conversion speed) or reach either of the problems that
actually required the most debugging effort, switching source formats wasn't worth pursuing further.

---

## Follow-up: unit test coverage (NUnit + Moq) — DONE, outcome below

Before Step 8, added fast/offline/repeatable regression coverage. Every bug this project actually hit
(the `StatementTypeDetector` false positives that mistagged 96 chunks, the table row-group-label loss,
the exhibit-index header-folding bug) was found by slow, manual end-to-end testing (Step 7) - none of
it was covered by anything that runs in milliseconds. `RagFilingExplorer.Local.Tests` (NUnit) now
covers that logic directly; a `RagFilingExplorer.slnx` at the repo root ties both projects together so
`dotnet test` runs everything in one command.

**Scope decision: extracted two small pieces of `Program.cs` so Moq has something real to mock.**
Most of the project's interesting logic (`Chunking/`) is already pure static classes with no I/O -
`StatementTypeDetectorTests`, `SectionSplitterTests`, `TokenChunkerTests`, `EmbeddingTextBuilderTests`,
and a small `MarkItDownConverterTests` (covering just the extracted `StripIxHeader` regex, not the
`markitdown` process shell-out itself - that stays an integration point, not something meaningfully
mocked) cover these with plain NUnit, no mocking needed. To make Moq earn its place rather than sit in
the project unused, two things were pulled out of `Program.cs`'s top-level statements:
- `Retrieval/QueryIntentResolver.cs` - the company/statement-type keyword-matching logic (still pure,
  tested with plain NUnit).
- `Retrieval/RagAnswerService.cs` - the retrieve+generate flow (filter construction →
  `collection.SearchAsync` → citation-formatted context prompt → `chatClient.GetStreamingResponseAsync`),
  now taking `VectorStoreCollection<int, FilingChunkRecord>` and `IChatClient` as constructor
  dependencies instead of calling them inline. `RagAnswerServiceTests` mocks both with Moq and verifies
  the orchestration - filter is null/non-null correctly, retrieved chunks pass through unchanged, the
  prompt sent to the chat client contains the citation-formatted context and the original question -
  without a live Ollama instance or a populated vector store. `Program.cs` itself now only does
  console I/O around `RagAnswerService.AskAsync`.

**A real, non-obvious blocker found and fixed: Moq couldn't mock `VectorStoreCollection<int,
FilingChunkRecord>` at first.** Every `RagAnswerServiceTests` test failed with `Can not create proxy
for type VectorStoreCollection<int, FilingChunkRecord> because type FilingChunkRecord is not
accessible` - Moq mocks this abstract class via Castle DynamicProxy, which generates the mock into its
own dynamic assembly (`DynamicProxyGenAssembly2`); since `FilingChunkRecord` is `internal` and used as
a generic argument on a class from the strong-named `Microsoft.Extensions.VectorData.Abstractions`,
Castle needs explicit permission scoped to its own well-known public key, not just an assembly-name
`InternalsVisibleTo`. Fixed with a second `[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2,
PublicKey=...")]` in `AssemblyInfo.cs` (alongside the first one, granting the test project itself
access to `internal` types) - this is Castle/Moq's own documented workaround for exactly this
situation, not a project-specific hack. Confirmed via direct reflection first (`VectorStoreCollection<
,>.SearchAsync` is `public virtual` / generic - genuinely mockable, not just assumed) before writing
the tests against it.

**Verified:**
- `dotnet test` from the repo root: **60/60 passing**, fully offline, no Ollama/SQLite/network
  dependency for any of them.
- `dotnet build` on `RagFilingExplorer.Local` still succeeds after the `Program.cs` extraction.
- Ran the app against the existing `rag.db` with the same known-good question from Step 6/the SqliteVec
  follow-up ("What was Microsoft's total revenue for fiscal year 2026?") - identical result to before
  the refactor: filtered to `MSFT-10K-2026.html`/`income_statement`, same top-ranked chunk, answer
  `"$331,839 million"` with the same citation. The `Program.cs` → `RagAnswerService` extraction did not
  change runtime behavior.

---

## Follow-up: repository review and Program.cs refactor — DONE, outcome below

A full read-through of the codebase (not just running the tests) turned up several small real issues,
fixed together before moving on to configuration work:

- **`data/MSFT-10K-2026.pdf` (9.5MB) removed.** Leftover from the "Considered and declined: PDF instead
  of HTML" experiment above. Harmless functionally (the app only globs `*.html`), but dead weight about
  to go into a public repo with no purpose once the decision not to use it was already made.
- **The interactive loop's diagnostic dump is now behind `--verbose`.** It had been printing the full
  top-25 ranked-candidate list on every question unconditionally - genuinely useful for Step 7's
  retrieval-quality debugging, but never meant to be permanent, shipped behavior (the code's own comment
  already admitted as much: "not the normal Step 6 behavior"). Normal runs now search `top: 5` (exactly
  what generation uses) instead of `top: 25`; `--verbose` restores both the wider search and the dump.
- **`QueryIntentResolver` couldn't filter to `comprehensive_income`.** `StatementTypeDetector` tags five
  statement types, but the resolver's keyword dictionary only routed four of them to a search filter -
  added `["comprehensive_income"] = ["comprehensive income", "other comprehensive income"]`.
- **`MarkItDownConverter.ConvertAsync` now captures `stderr` on failure.** It checked `process.ExitCode`
  but never read `StandardError`, so a real `markitdown` failure threw an exception with no detail from
  the actual Python error. Fixed by reading `StandardOutput` and `StandardError` concurrently (reading
  either one sequentially risks a pipe-buffer deadlock if the other fills first) and including `stderr`
  in the thrown message.
- **`Program.cs` restructured into named local functions.** It had grown into one ~250-line linear
  top-level script mixing four distinct concerns (flag parsing, index building, per-filing chunking, the
  interactive loop). Broken into `BuildIndexAsync` → `IngestFilingAsync` → `WriteChunkReviewFileAsync`,
  `BuildRecords`, `UpsertRecordsAsync`, and `RunInteractiveLoopAsync` → `PrintMatchedFilter` /
  `PrintRetrievedChunks` - same single-file, top-level-statements structure, no new classes, purely
  decluttering the linear flow into named steps. The two near-identical `HttpClient` constructions
  (embedding client, chat client) were also deduplicated into one `CreateOllamaHttpClient` local
  function.

**Verified:** `dotnet build` and `dotnet test` (61/61 at this point) clean after every change; live
smoke-tested against the existing `rag.db` after the `Program.cs` refactor specifically, since it's the
kind of change where a silent behavior regression would be easy to introduce - same correct, cited
answer as before.

---

## Follow-up: configurable settings (`appsettings.json`) — DONE, outcome below

**The problem this solved:** tunable values (model names, Ollama's base URL/timeout, chunk size/overlap,
tokenizer model, upsert batch size, retrieval top-K, chat temperature) were hardcoded across `Program.cs`
and `RagAnswerService.cs`. Extracted into `RagFilingExplorer.Local/appsettings.json`, bound via a small
`AppSettings.cs` POCO using `Microsoft.Extensions.Configuration` + `.Json` + `.Binder` (pinned to
`10.0.12`, matching the installed runtime's versioning line rather than the `Microsoft.Extensions.AI`
family's `10.10.0`; confirmed via `dotnet list package --vulnerable --include-transitive` that this
didn't introduce anything vulnerable). Retrieval-relevant domain logic that isn't just a tunable number -
`QueryIntentResolver`'s company and statement-type keyword lists, the section-boundary and
statement-title regexes - deliberately stayed in code: a typo there would silently break filtering with
no compiler to catch it, unlike a config value.

**A real duplication caught by the user, not found independently:** the first version gave every
`AppSettings` property a C# default value that just repeated the real value already in
`appsettings.json` (e.g. `public string ChatModel { get; set; } = "llama3.1:8b";`), so the same numbers
existed in two places with nothing keeping them in sync if one changed and not the other. Fixed by
making every property `required` and removing the duplicate defaults - `appsettings.json` became the
one real source of truth.

**A real gap in that fix, found by testing rather than trusting the keyword:** C#'s `required` modifier
is a *compile-time* check tied specifically to object-initializer syntax (`new Foo { ... }`) - confirmed
against Microsoft's own compiler-diagnostics docs (CS9035: "Required member must be set in the object
initializer"). `Microsoft.Extensions.Configuration.ConfigurationBinder.Get<T>()` constructs and
populates the object via reflection (`Activator.CreateInstance` + property setters), which never goes
through an object initializer and so never checks `required` at all. Confirmed directly, not just from
the docs: deploying a copy of `appsettings.json` with `Ollama:ChatModel` removed still started the app
successfully, with that property silently `null`. Fixed with an explicit `EnsureAllKeysPresent` check in
`Program.cs`'s `LoadSettings()` that verifies presence against the **raw configuration keys**
(`configuration["Ollama:ChatModel"] is null`), not the bound values - deliberately, so a legitimately
zero-valued setting (`OverlapTokens: 0`, `ChatTemperature: 0`) is never mistaken for "missing." Verified
all three cases directly against the built binary: normal startup works, a genuinely missing key throws
a clear `InvalidOperationException` naming exactly which key is missing, and a present-but-zero key
doesn't false-positive.

`VectorStore.UpsertBatchSize`'s constraint (must stay `1` - see the SqliteVec follow-up above) is now
documented in three places that reinforce each other: an inline JavaScript-style comment directly above
the key in `appsettings.json` itself, and a full explanation in the README. Comments in `appsettings.json`
were confirmed to actually work with this app's plain `ConfigurationBuilder().AddJsonFile(...)` setup (not
just ASP.NET Core's default host, which is what Microsoft's own docs describe) before relying on them,
by testing a JS-style `//` comment directly against the deployed config.

**A real retrieval bug found live-testing this change, unrelated to configuration itself:**
`StatementType` was leaking past where it should stop. The "carry the last detected type forward" logic
in `BuildRecords` (see Step 4/the retrieval-quality follow-up above for the original pattern) only ever
reset `currentStatementType` on a filing change - it had no reset boundary for *leaving* the five
primary financial statements and entering the Notes section that follows them. Since a filing's equity
statement is conventionally the last of the five, whichever type was detected last silently "leaked"
across every subsequent chunk for the rest of that filing. Confirmed directly: asking "What was total
stockholders' equity for Microsoft?" correctly filtered to `equity_statement`, but `--verbose` showed the
top-25 candidates dominated by unrelated `equity_statement`-tagged chunks - Note 17 (Employee Stock
Plans), Item 12 (Security Ownership), even Item 15/16 - burying the real `$442,387 million` figure
outside the top 25 entirely. Fixed generically, not filer-specifically (confirmed all three filings share
this convention via direct grep): `StatementTypeDetector.IsNotesToFinancialStatementsBoundary` recognizes
the `NOTES TO (CONSOLIDATED) FINANCIAL STATEMENTS` title line that immediately follows the primary
statements in every filing, and resets the carried-forward type to narrative there. Re-verified after
rebuilding `rag.db` from scratch: the same equity question now narrows to exactly 5 candidates (the real
equity table) instead of hundreds, and returns the correct `$442,387 million`.

**Verified:** `dotnet test` 67/67 (up from 61, including new coverage for `IsNotesToFinancialStatementsBoundary`
and the `comprehensive_income` keyword). Rebuilt `rag.db` from scratch (`--rebuild`) after the
statement-type fix - same 1,073 chunks as before (the fix changed tagging, not chunk boundaries) - and
re-verified three spot-check questions against the rebuilt index (MSFT revenue, MSFT equity, Oracle
comprehensive income) all correct with proper citations.

---

## Follow-up: reasoning-model support — DONE, outcome below

**Context:** `qwen3.5:2b` was pulled locally (alongside `nomic-embed-text` and `llama3.1:8b`) to try as
a reference chat model, not to replace `llama3.1:8b` as the shipped default. Two real, distinct bugs
were found this way - both only visible through live testing against real Ollama, since the unit test
suite mocks `IChatClient` and would never have caught either one.

**Bug 1: empty answers on real questions, despite a working raw connection.** Ollama's own streaming API
keeps a reasoning model's internal chain-of-thought (`thinking`) and its real answer (`content`) as
genuinely separate fields per streamed chunk - confirmed directly with `curl -N` against `/api/chat`,
watching `thinking` deltas arrive with `content` empty, then `content` arrive once thinking finished.
`Microsoft.Extensions.AI` has first-class support for exactly this split: `OllamaSharp` 5.4.30 maps a
model's `thinking` into a distinct `TextReasoningContent` item, kept separate from the `TextContent` that
`ChatResponseUpdate.Text` aggregates - confirmed by grepping the installed DLL's metadata for member
names (`get_Thinking`, `TextReasoningContent`, `get_Reasoning`) and then verifying with a small throwaway
console project built against this project's exact pinned package versions, not just trusting the
strings. Given this app's longer retrieved-context prompts (system prompt + up to 5 retrieved chunks),
`qwen3.5:2b` spent its **entire** generation budget "thinking" about even simple lookup questions and
never reached the answer - `update.Text` stayed empty for the whole 2-3 minute response, with nothing
upstream noticing anything had gone wrong.

**Fix, three parts, verified together:**
1. `ChatOptions.Reasoning` (`ReasoningOptions.Effort`, a genuine C# enum - `None`/`Low`/`Medium`/`High`/
   `ExtraHigh`, confirmed via reflection) maps through OllamaSharp to Ollama's own `think` request field.
   `QueryIntentResolver.RequiresSynthesis` (a keyword-based classifier, same pattern as the existing
   company/statement-type resolvers) decides whether a question needs genuine multi-step reasoning
   (comparisons, ratios, trends) before `RagAnswerService` ever applies the configured
   `Retrieval.ReasoningEffort` - a plain single-fact lookup always gets `Effort.None` regardless, so a
   reasoning model never pays the "thinking" cost on a task that doesn't need it.
2. `Retrieval.MaxOutputTokens` gives the model explicit room for a full reasoning trace plus the answer,
   instead of relying on Ollama's own default - the actual root cause of the budget exhaustion above,
   since nothing had ever set this explicitly before.
3. A starved-response guard in `RagAnswerService` (`GuardAgainstStarvedResponse`, wrapping the raw
   stream): if a model still hits the `MaxOutputTokens` ceiling (Ollama reports this as `ChatFinishReason.
   Length` on a normal completion, not an error) without ever producing real answer text, the app now
   throws a specific, actionable error instead of silently showing nothing. Verified both ways: unit
   tested with a mocked stream carrying `FinishReason.Length` and no text (throws) versus `FinishReason.
   Length` with partial-but-real text (does not throw - a truncated real answer must still be shown, not
   treated as an error), and reproduced live by deliberately asking a hard comparison question that
   exceeded the configured budget.

**Bug 2, found immediately live-testing bug 1's fix, and more serious: it crashed the whole app.** Ollama
does not quietly ignore a "think" request for a model that doesn't support reasoning at all - it throws a
hard `OllamaSharp.Models.Exceptions.OllamaException` (`"llama3.1:8b" does not support thinking`), which
propagated all the way up through the interactive loop's `await foreach` and took down the entire
session the very first time a synthesis question tried to route `llama3.1:8b` (the shipped default) to
`Effort.Medium`. Fixed two ways:
1. **Capability check at startup, not assumed.** `Program.cs` now calls Ollama's own `/api/show` for the
   configured `ChatModel` (`OllamaApiClient.ShowModelAsync` → `ShowModelResponse.Capabilities`) once
   before the interactive loop starts, and only routes a question to reasoning if `"thinking"` is
   actually present in that list. Confirmed directly: `llama3.1:8b` reports
   `[completion, tools]`, `qwen3.5:2b` reports `[completion, vision, tools, thinking]`.
2. **The interactive loop no longer dies on one bad turn.** Exception handling around a single question's
   generation was broadened from catching only the starvation guard's `InvalidOperationException` to
   catching any `Exception` - a deliberate choice, not reflexive defensiveness: a live call to an
   external service (Ollama) is exactly the kind of boundary where failures can't be fully predicted in
   advance, and the whole point of an interactive session is that one bad turn shouldn't end it.

**Verified end to end, repeated with both models after every fix:**
- `llama3.1:8b` (shipped default): a simple lookup and a synthesis comparison question both answered
  correctly, no crash, reasoning correctly never engages (confirmed it reports no `thinking` capability).
- `qwen3.5:2b`: a simple lookup stayed fast and correct with reasoning skipped (routing correctly
  withheld it); a synthesis question correctly engaged `Medium` reasoning and produced real, on-target
  reasoning content citing the right figures from the right sources - though it hit the 4096-token
  ceiling on the hardest comparison question before finishing. The guard caught this cleanly, reported a
  clear error, and the session continued to the next question normally. That's an honest reflection of
  this specific small model's verbose reasoning style under this budget, not a flaw in the routing or
  guard mechanism itself - a larger or more decisive reasoning model would likely need a smaller budget
  for the same task.

`dotnet test`: **82/82 passing** (up from 67), including new regression coverage for both bugs -
`RequiresSynthesis` routing, the `chatModelSupportsThinking` capability gate, `MaxOutputTokens` wiring,
and both the throw and no-throw cases of the starved-response guard.

---

## Follow-up: onboarding a new filer (NFLX) — DONE, outcome below

**Context:** the user added `data/NFLX-10K-2025.html` - a fourth filing, Netflix's 10-K - and asked
whether it would work against the pipeline as it stood. It didn't, not fully: three real bugs surfaced,
each found by actually running the pipeline and checking the output, not by inspection alone. All three
are fixed generically (no filer-specific branches), consistent with this project's standing rule about
not building logic that depends on one filer's conventions.

**First difference noticed, before any code ran:** unlike the other three filings (downloaded directly
as raw HTML from SEC EDGAR), `NFLX-10K-2025.html` was a browser "Save As - Complete Webpage" download -
confirmed by a `<!-- saved from url=... -->` comment at the top of the file - which brought a companion
`NFLX-10K-2025_files/` folder (an image and an oddly-named resource file) along with it. That folder
turned out unnecessary for text extraction and was deleted; the specific way the file was *saved*,
rather than anything about Netflix's filing itself, is what caused two of the three bugs below.

**Bug 1: `SectionSplitter.TitledItemHeaderRegex` required a space after the item period.** Netflix's
markitdown-converted output has no space in 21 of its 22 Item headings (`"Item 1.Business"`, not
`"Item 1. Business"` - only `Item 16.` kept the space, confirmed by grepping every Item line in the
converted output, so this is a filer quirk, not a total absence). The regex's `\.\s+` required at
least one space, so every one of these lines would have silently fallen through as ordinary body text
instead of a section boundary - collapsing the whole filing into 4 giant `PART`-only sections with no
Item-level heading at all, which would have badly degraded citation granularity even though the
underlying retrieval and figures would likely still have been correct. Fixed by relaxing `\.\s+` to
`\.\s*`; verified directly with a regex test that the relaxed pattern matches both Netflix's no-space
and with-space forms and every existing filer's with-space convention, while still correctly excluding
the bare `"Item 1"` noise case (no period at all).

**Bug 2: `MarkItDownConverter` always read the source file as UTF-8.** Netflix's file declares (and is
genuinely encoded as) `windows-1252` via its own `<meta http-equiv="Content-Type" content="text/html;
charset=windows-1252">` tag - the other three filings declare no charset at all and are safely UTF-8,
which is what the original unconditional assumption relied on. Reading windows-1252 bytes as UTF-8
silently replaced every non-ASCII character (curly quotes, etc.) with the U+FFFD replacement character
before markitdown ever saw the content - confirmed by diffing chunk output against the other three
filings: 0 corrupted characters there, 700+ in NFLX's before this fix. Fixed with a new
`MarkItDownConverter.DetectEncoding` method: check for a UTF-8 BOM first, then scan the first 4KB
(decoded as plain ASCII, which is safe because HTML charset declarations are always pure ASCII per the
HTML5 spec - this is what makes it possible to find the real encoding without already knowing it) for a
`<meta charset>` declaration, and fall back to UTF-8 when neither is present. Needed
`Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` for `Encoding.GetEncoding("windows-1252")`
to resolve at all - confirmed it throws `ArgumentException` without it - but confirmed no separate NuGet
package reference is needed for this in .NET 10: `dotnet add package System.Text.Encoding.CodePages`
triggered an explicit `NU1510` warning that the package is already part of the shared framework, so the
explicit reference was removed again.

**Bug 3, the most consequential: `QueryIntentResolver.CompanyToFiling` was never given a Netflix
entry.** Adding a filing to `data/` does not, on its own, make it filterable by company name - that
mapping is a separate, manual dictionary. Confirmed directly: every question naming Netflix ran with
`(filtering to statement type: income_statement)` and **no company filter at all**, searching all four
filings' `income_statement`-tagged chunks at once. This is exactly the cross-company contamination
metadata filtering was built to prevent in the first place, and it produced two real failures before
the fix: a question about Netflix's total revenue got a declined "not explicitly stated" answer (the
top-ranked unfiltered chunk happened to be a differently-formatted table missing that line), and a
question about Netflix's net income returned a **hallucinated figure, `$12,443`**, that doesn't match
any real line in the filing - a confident wrong answer with a citation attached, the specific failure
mode this project has already flagged elsewhere as worse than an honest decline. Fixed by adding
`["Netflix"] = "NFLX-10K-2025.html"` and `["NFLX"] = "NFLX-10K-2025.html"` to the dictionary, with a
comment on the dictionary itself calling out that this step is required for any new filing, not
optional.

**Verified end to end after all three fixes, with a full `--rebuild`** (had to be re-run once - the
first `--rebuild` was already in progress against the pre-fix code when bug 2 was found and fixed, so
its output was stale and the whole thing was re-run against the corrected code):
- `chunk-review/NFLX-10K-2025.chunks.txt`: 0 occurrences of the `�` corruption marker (down from 700+),
  a complete 25-heading outline (`PART I`-`PART IV`, all 16 Items correctly nested), and the apostrophe
  in "Registrant's" rendering correctly.
- 1,479 chunks total across all four filings (up from 1,073 across three), embedded and upserted in
  594s.
- Live Q&A, re-run after the `CompanyToFiling` fix specifically: "What was Netflix's total revenue for
  fiscal year 2025?" → **$45,183,036** (thousand), correctly filtered to `NFLX-10K-2025.html`,
  `income_statement`. "What was Netflix's net income for 2025?" → **$10,981,201** (thousand) - both
  exactly matching the figures confirmed directly from the converted text, replacing the earlier
  declined answer and the `$12,443` hallucination.
- MSFT regression check (`"What was Microsoft's total revenue for fiscal year 2026?"`) still returns
  `$331,839 million` correctly after the full rebuild - no regression from adding a fourth filing.
- `dotnet test`: **90/90 passing** (up from 82) - new coverage for the relaxed Item-heading regex, five
  `DetectEncoding` cases (both charset-declaration forms, no declaration, a UTF-8 BOM overriding a
  conflicting declaration, and an unrecognized charset name falling back to UTF-8), and Netflix entries
  in the `ResolveFiling` test cases.

**One cosmetic side effect worth knowing, not a bug:** citations for Netflix's financial figures say
`PART IV > Item 16. Form 10-K Summary`, not `Item 8`. Confirmed why: Netflix's actual financial
statement pages are physically positioned in the converted document *after* the `Item 16` heading line,
even though they logically belong to Item 8 - `Item 8` and `Item 9`'s heading lines sit only four lines
apart in the raw converted text, meaning Item 8 is just a short stub that refers elsewhere, and
`SectionSplitter`'s "carry the last Item heading forward" logic has no way to know that. This is the
same class of structural quirk already documented for ORCL (whose real statements live under
`Item 15`, not `Item 8`) - a property of how these specific filings are laid out, not a new defect. The
dollar figures themselves are unaffected.

---

## Follow-up: second repository review — index integrity, startup checks, config cleanup — DONE, outcome below

A second full read-through (every source file, the tests, README, and the manual test plan), with each
suspicion checked against real behavior or data before being acted on. Nothing was broken on the
happy path the project had been exercised through, but several things would bite a fresh clone or
silently degrade answers:

**1. The README's own run command didn't work.** `Program.cs` resolved `data/`, `chunk-review/` and
`rag.db` as `Directory.GetCurrentDirectory() + ".."` - correct only when launched from inside
`RagFilingExplorer.Local/` (which is how every run so far had been done, hence `rag.db` at the repo root).
Confirmed with a throwaway probe app that `dotnet run --project X` keeps the *caller's* working
directory, so `dotnet run --project RagFilingExplorer.Local` from the repo root - exactly what the README
says - looked for `C:\Projects\data`. Fixed with `RepoPaths.FindRoot`, which walks up from
`AppContext.BaseDirectory` (the build output) to `RagFilingExplorer.slnx`, independent of working
directory.

**2. An interrupted or failed build left a `rag.db` that every later run trusted.** "`rag.db` exists"
was the only completeness check, but `SqliteVectorStore` creates the file before any chunking starts.
Reproduced directly: a `--rebuild` with `markitdown` hidden from `PATH` failed on the first filing and
left `rag.db` behind; before this fix, the next plain run would have skipped the build and answered
from an empty index. The same applies to Ctrl+C during the ~10-minute embedding pass.

**3. A stale index wasn't detected.** Changing `EmbeddingModel`, chunk size/overlap, or the filings in
`data/` silently reused the old index unless `--rebuild` was remembered. The embedding-model case is the
worst: query vectors from the new model compared against stored vectors from the old one retrieve noise
with no error.

Fix for 2 and 3 together: `VectorStore/IndexManifest.cs`. After the last chunk is upserted - and only
then - `rag.db.manifest.json` (`.gitignore`d) is written, recording the embedding model, tokenizer model,
chunk size/overlap, and a SHA-256 of every filing. On startup an existing `rag.db` is only used if the
manifest exists and matches; otherwise the app lists exactly what differs and asks for `--rebuild`.
Deliberately *not* an automatic rebuild: silently starting a 10-minute re-embed is its own surprise.
Chunking/embedding *code* changes still aren't detectable this way - `--rebuild` remains the tool for
those.

**4. An unregistered filing still ran unfiltered, silently.** The NFLX onboarding bug (a filing with no
`QueryIntentResolver.CompanyToFiling` entry) was documented but unguarded. `FindRegistrationProblems`
now reports it (and entries pointing at a filing no longer in `data/`) as a startup warning, and a unit
test runs it against the real `data/` folder so `dotnet test` fails on it too.

**5. Config-value duplication had crept back.** `RagAnswerService`'s constructor carried its own
defaults (`generationTopK = 5`, `chatTemperature = 0.2f`, `maxOutputTokens = 2048`, ...) - the same class
of duplication fixed earlier for `AppSettings`, and one had already drifted (`2048` vs. `4096` in
`appsettings.json`). It now takes `RetrievalSettings` directly with no defaults; tests build their own
fixture settings. `Retrieval.ReasoningEffort` now binds straight to the `ReasoningEffort` enum, so a typo
fails at load time instead of at a separate `Enum.Parse` call.

**6. The required-config-key list was hand-maintained**, separately from `AppSettings`' properties -
adding a setting and forgetting the list would reintroduce the "missing key binds as null" gap.
`AppSettings.RequiredConfigurationKeys()` now derives it by reflection; loading/validation moved from
`Program.cs` into `AppSettings.Load` so it's unit-testable against the shipped `appsettings.json`.

**7. `CommunityToolkit.VectorData.InMemory` removed** - unused since the SqliteVec swap.

**8. Actionable startup errors instead of stack traces**, via a `StartupException` that `Program.cs`
prints as one line and exits `1`: Ollama unreachable (checked up front via `/api/tags`), either model not
pulled (prints the exact `ollama pull` command), and `markitdown` missing from `PATH` (previously a bare
"The system cannot find the file specified" that never named markitdown).

**9. `docs/Manual-Test-Questions.md` Q2 had the wrong expected filter** (`balance_sheet`; the resolver
routes "stockholders' equity" to `equity_statement`). Corrected - and confirmed the equity statement's
closing row carries the same $43,056M total, so the answer itself is unaffected.

**Not done, deliberately deferred:** a chunk's `StatementType` is the last statement title seen within it,
so text *before* a mid-chunk title inherits the new tag. Checked all four filings: every mid-chunk title
has only page-header lines above it except NDAQ chunk 192, where auditor-report prose is tagged
`balance_sheet` - pollution, but no statement figures mistagged today. Relatedly, `F-3`-style page markers
aren't filtered (and one is carried as chunk overlap). Both change chunk boundaries and need a rebuild
plus a manual-question re-run, so they're left until after the manual testing pass.

**Verified end to end against the real app, not just the unit tests** (each run from a directory other
than `RagFilingExplorer.Local/`, most from an unrelated scratch folder):
- README's `dotnet run --project RagFilingExplorer.Local` from the repo root now finds the repo-root
  `rag.db`; with the pre-existing, manifest-less `rag.db` it refused with the "no build manifest" message.
- Ollama pointed at a dead port (temporary edit to the *build output's* `appsettings.json`, restored
  after): one-line "Could not reach Ollama at ..." message, exit `1`. A non-existent `ChatModel`: "doesn't
  have these model(s) pulled ... Run: ollama pull ...".
- `--rebuild` with `markitdown`'s directory removed from `PATH`: the new markitdown message, a partial
  `rag.db` left behind with no manifest, and the next plain run refused it - the exact scenario item 2
  describes, now caught.
- Full `--rebuild`: 1,479 chunks (unchanged), 592s, manifest written; `chunk-review/` regenerated
  byte-identical to the committed dumps (no chunking change). "What was Microsoft's total revenue for
  fiscal year 2026?" → `$331,839 million`, correctly filtered and cited.
- Plain run reusing that index: "What was Netflix's net income for 2025?" → `$10,981,201` (thousand),
  matching `Manual-Test-Questions.md` Q18.
- Stale detection: with `OverlapTokens` changed 50→40 and a temporary unregistered copy of a filing in
  `data/`, the app warned about the unregistered filing, listed both differences, and exited `1`.
  Restoring both made the same index accepted again.
- `dotnet test`: **109/109** (up from 90) - new coverage for `IndexManifest` (each kind of difference,
  save/load round trip, corrupt/missing file, content hashing), `AppSettings.Load` (shipped settings
  bind, missing key named, zero values not "missing", misspelled `ReasoningEffort` fails at load),
  `RepoPaths`, and `FindRegistrationProblems` (including a check of the real `data/` folder).

**Also done in this pass: this log was split out of `Implementation_Plan.md`.** That file had grown to
~68KB (~23k tokens), auto-loaded into every Claude Code session via `CLAUDE.md`, mostly as debugging
narrative that rarely matters for the task at hand. It's now a ~10KB current-state reference (ground
rules, status, the pipeline as shipped, live constraints, already-validated facts); everything else moved
here verbatim - checked mechanically that all 825 moved lines appear in this file, in order.

---

## Follow-up: statement-boundary chunking (review item #10) — DONE, outcome below

Picked up the item the second review had deferred, and found a worse bug while scoping it.

**Scoping check first:** forcing a chunk boundary at every statement title is only safe if the detector
matches nothing but real titles - a table-of-contents hit would become its own tiny stray chunk.
Scanned every line of all four chunk dumps: exactly one match per real title plus the Notes boundary,
no TOC hits.

**The worse bug that scan surfaced: NDAQ's equity statement was never detected.** NDAQ titles it
"Consolidated Statements of *Changes in* Stockholders' Equity"; `EquityRegex` didn't allow "Changes in".
Confirmed in the live `rag.db`: **0** NDAQ chunks tagged `equity_statement` - the equity statement had
inherited the preceding `comprehensive_income` tag - and "What was Nasdaq's total stockholders'
equity?" returned "(no results)" live, since the filter matched nothing. No manual question covered NDAQ
equity, which is how it went unnoticed. Fixed by allowing an optional `CHANGES IN` after `OF`.

**The original item, fixed together:**
- `SectionSplitter` starts a new section (same heading) at every statement title and at the Notes
  boundary (`StatementTypeDetector.IsStatementTypeBoundary`), so the title always opens a fresh chunk
  and the carried-forward tag covers the whole chunk. Before, text preceding a mid-chunk title - the
  previous statement's "See accompanying notes... F-5" footer, NDAQ's auditor-report prose - got the
  next statement's tag. Since overlap never crosses a section, a footer also can't leak forward as
  overlap any more.
- `PageNumberRegex` also drops `F-N` financial-statement page markers (45 in NDAQ - the only other
  marker style in any filing). Lone roman numerals deliberately not dropped: a lone "x" can be a real
  cover-page checkbox mark.

**Verified:**
- Chunks 1,479 → 1,500 (a boundary per title). Compared every content line of the regenerated chunk
  dumps against the committed ones, ignoring the renumbered `Chunk n/N` headers: MSFT/NFLX/ORCL have
  *identical* content lines; NDAQ lost exactly its 45 `F-N` markers, plus 38/37 lines whose only change
  was how often overlap repeats them - all 8,955 distinct content lines still present, none invented.
- `rag.db` after `--rebuild`: every filing shows exactly six transitions (five statements + the Notes
  reset to narrative), and every tagged run's first chunk starts with its own title line. NDAQ now has
  6 `equity_statement` chunks.
- `dotnet test`: **119/119** (new: NDAQ's equity title, `IsStatementTypeBoundary`, the section split at
  titles and at the Notes boundary, `F-N` markers dropped, a line merely *containing* "F-3" kept).
- `Manual-Test-Questions.md` chunk-dump line references re-mapped to the new dumps (each by finding the
  original line's exact text in the new dump), and NDAQ equity added as Q23.

**Full manual question set run (all 22 + Q23) against the rebuilt index - 20 correct, 3 not:**
- Correct: Q1, Q3, Q5-Q22 - every Netflix figure, every comprehensive-income figure, prose facts, the
  MSFT-vs-ORCL comparison (both figures, cited), both negative tests declined properly.
- **Q23 (NDAQ equity)** now retrieves the equity statement (was "(no results)"), but labels the $12,232M
  "Total equity" figure (including $5M of noncontrolling interests) as "Total Nasdaq stockholders'
  equity" ($12,227M). Right statement, near-right figure, wrong label.
- **Q4 (MSFT "net cash from operations")** declined honestly - pre-existing, not caused by this change:
  `QueryIntentResolver` has no keyword for MSFT's exact wording, so no `cash_flow_statement` filter
  applied.
- **Q2 (ORCL stockholders' equity)** answered wrongly ("not explicitly stated", then unrelated 2023
  figures). Diagnosed via `rag.db`: tagging is correct (17 `equity_statement` chunks, including the one
  with the closing $43,056M row), but ORCL's equity statement is so wide it splits into 15
  near-identical row fragments, each repeating the same column header, and the target row is labelled
  "Balances as of May 31, 2026" rather than "Total stockholders' equity" - nothing distinguishes it, and
  only the top 5 reach the model. Pre-existing: this change doesn't alter how that table is split.

---

## Follow-up: equity routing and title captions — DONE, outcome below

Fixes for the three misses from the statement-boundary run (Q2, Q4, Q23), after first settling whether
that change had caused Q2.

**Did the statement-boundary change break Q2? No - verified, not assumed.** Q2 had never been run before
that change, so there was no prior result. Checked out the initial commit into a separate git worktree,
built its own index, and asked the three equity questions with `--verbose` on both versions:

| Question | Initial commit | After statement boundaries |
|---|---|---|
| Q2 ORCL equity | wrong ("cannot provide") | wrong |
| Q23 NDAQ equity | "(no results)" | right statement, $12,232M mislabelled |
| Q7 MSFT equity | $442,387M | $442,387M |

The chunk holding ORCL's closing $43,056M row was byte-identical in both, and - recomputing distances
directly against Ollama, which reproduced the app's own scores exactly - ranked **8th** before and **9th**
after, outside the top 5 either way. It never reached the model. But the comparison did expose a real
side effect of the boundary change: each statement title had become a near-empty chunk of its own
(just the title and "(in millions)"), and those took a top-5 slot for every statement question (rank 1
or 2 for all three equity questions).

**Fixes:**
- **Equity routing** (`QueryIntentResolver`): plain "(total) stockholders' equity" / "total equity"
  questions now route to `balance_sheet`, which has one clean total row in all four filings (checked).
  "Changes in ... equity" / "statement of stockholders' equity" questions still route to
  `equity_statement`: `ResolveStatementType` now applies a longest-match rule, so a matched keyword
  contained in a longer matched keyword ("stockholders' equity" inside "changes in stockholders'
  equity") is ignored. Genuinely different matches still resolve to null, as before.
- **Q4 keyword:** "cash from operations" added for `cash_flow_statement` - MSFT's own line label ("Net
  cash from operations") matched no keyword, so that question ran with no statement filter.
- **Title captions** (`TokenChunker`): a pending lead-in of at most 100 tokens with no table in it,
  directly before an oversized table, becomes that table's first piece's caption instead of a chunk of
  its own; the first piece's row budget shrinks to match.

**Verified:** `dotnet test` 127/127. Rebuild: 1,500 → 1,454 chunks. Compared against the previous dumps:
0 distinct content lines lost or gained in any filing; every vanished chunk is either a caption now
prefixed to a table piece or a table fragment re-split because its first piece's budget shrank. Beyond
statement titles, the rule also absorbed real table captions ("Supplemental cash flow information
related to leases was as follows:") and stray `[Table of Contents]` link chunks - both improvements. No
title-only chunks remain.

**Full question run (Q1-Q24, `--verbose`) - 22/24:**
- **Fixed:** Q2 ORCL equity **$43,056M** and Q23 NDAQ equity **$12,227M** (correctly labelled), both
  failing since the initial commit. New Q24 (dividends per the equity statement) → **$27,034M**,
  declared rather than the $26,445M paid - confirming "changes" questions still reach the equity
  statement. Q4 now filtered to the cash flow statement and ends with the right **$182,935M**, though the
  model pasted the raw table first.
- **Q3 (NDAQ total liabilities) - generation slip, not retrieval:** the rank-1 chunk contains the correct
  `Total liabilities 18,821` row; the model read the adjacent "Total liabilities and equity 31,053".
  Essentially the same context produced the right answer on the previous run - `llama3.1:8b` run-to-run
  variance at temperature 0.2.
- **Q20 (MSFT vs ORCL revenue) - a real side effect of the caption change:** ORCL's revenue chunk now
  carries the title caption, which shifted its embedding to rank **10th** for this question. The
  question names two companies, so no company filter applies and 5 slots are shared across all four
  filings - it passed last time partly by luck. Planned fix: one company-filtered search per named
  company.
- **Observed, not yet fixed:** statement footers and short footnotes now form tiny chunks of their own
  (the boundary change moved them off the next title's chunk) and rank high - rank 1 for all five NFLX
  statement questions, rank 1 for Q20. No wrong answer traced to them this run, but they waste a
  context slot. Planned fix: merge short trailing remainders into the preceding chunk.

## Follow-up: trailing remainders, per-company search, table-piece headers — DONE, outcome below

Fixes for the two side effects observed in the previous run (Q20, tiny footer chunks), one further
retrieval miss found while fixing them, then - after pinning temperature to 0 - two table-splitting bugs
behind the remaining generation errors. Question numbers here are line numbers in
`tools/manual-questions.txt` (the same numbering as the entries above); `Manual-Test-Questions.md` numbers
NFLX as 17-22 and the edge cases as 14-16.

**Retrieval fixes:**
- **Trailing remainders** (`TokenChunker`): a remainder of at most 100 tokens with no table in it,
  directly after an oversized table's last piece (a "See accompanying notes..." footer, a one-line
  footnote), is appended to that piece instead of becoming its own chunk. Deliberately limited to that
  case - the short last paragraph of an ordinary narrative section is left alone. 1,454 → 1,433 chunks.
- **Per-company search** (`QueryIntentResolver.ResolveFilings` + `RagAnswerService`): a question naming
  2+ registered companies runs one company-filtered search per company (same statement-type filter) and
  interleaves the results by rank, instead of one unfiltered search sharing 5 slots across all filings.
  The printed filter line reads `(searching A and B separately, ...)`.
- **Row-label summary for captioned tables** (`EmbeddingTextBuilder`): the summary of a table's row labels
  was only added to chunks *starting* with `|`. The title caption added in the previous follow-up had
  silently disabled it for every statement table's first piece - which is what dropped ORCL's revenue
  piece out of Q20's context. It now applies to any chunk containing a table.

**Retrieval replay tool** (`tools/replay_recall.py`): replays the app's retrieval against `rag.db` - the
stored vectors, the filters a `--verbose` run printed, the same cosine distance and per-company
interleave - and reports whether each answerable question's expected figure is in the top-5 context. No
LLM involved, so it separates "retrieval missed it" from "the model misread it"; verified to reproduce the
app's own `--verbose` scores exactly. After the three fixes: **22/22** answerable questions in context, up
from 21/22 (Q20). The remaining wrong answers (Q8, Q10, Q18 - all comprehensive income) had the right
figure in context and flipped between runs.

**Temperature 0.2 → 0** (`Retrieval.ChatTemperature`): with the answer varying run to run, a changed
result couldn't be attributed to a code change. Not part of the index manifest, so no rebuild. Full run at
0: **21/24 correct**; Q8 now *reliably* wrong, Q18 and Q24 right figure but wrongly framed:
- **Q8 (MSFT comprehensive income) → $133,749M, the Net income row.** The statement is split in two
  pieces. The first had the title, the years and Net income $133,749 as its first figure; the second held
  only "Other comprehensive income" and the **Comprehensive income $133,812** total, with no title and no
  years. The model quoted both numbers and chose the one with context. Cause: the repeated header stops at
  the first row-group label, and MSFT's `(In millions)` row (text in the first cell only) reads as one -
  so `Year Ended June 30, ... 2026 ... 2025 ... 2024`, below it, was body text and never repeated.
  NFLX keeps its years only because its `(in thousands)` is a paragraph outside the table.
- **Q18 (NFLX comprehensive income) → right figure, called "comprehensive income for fair value
  hedges".** The label repeated at a piece's top was read when the piece was *emitted*, so each piece got
  its own last label: `Cash flow hedges:` above Net income on the first piece, `Fair value hedges:` heading
  the cash flow hedge rows on the second - and, never closed, over the Comprehensive income total on the
  third. The same bug put `Other comprehensive income (loss), net of tax:` above `(In millions)` in MSFT's
  first piece.
- **Q24 (MSFT dividends, equity statement) → right $27,034M, but claimed the equity statement "is not
  explicitly shown".** Same headerless-continuation pattern as Q8.

**Table-piece fixes** (`TokenChunker.SplitOversizedTable`):
- The header extends through the last fiscal-period row (`... ended`, or 2+ bare-year cells) found before
  the first data row, so a label-like units row above it no longer cuts it off.
- The label repeated at a piece's top is the one in force where the piece *starts*; none if the piece
  starts on a new label row. A `Total ...` row closes the current group.

**Verified:** `dotnet test` 139/139 (two new tests: period row below a units row; every row under its own
label, checked as an invariant at three budgets). Rebuild: 1,433 → 1,440 chunks (MSFT +8, from the longer
header now on 87 continuation pieces; NFLX -1, from injected labels no longer taking room). 0 distinct
content lines lost or gained in any filing. Replay: **22/22**. Full run (`--verbose`, temperature 0):
**24/24**, all with the expected filter - Q8 $133,812M, Q18 without the hedge label, Q24 citing the
equity statement.

**Known, not fixed:** a group closed by something other than a `Total ...` row stays open. NFLX's
"Other income (expense):" group ends at "Income before income taxes", so that label is still repeated
above Net income on the next piece (Q15 answered correctly regardless). No reliable, filer-independent
rule for such closers was found; a guessed one would be worse than a visible stale label.

## Follow-up: linearized tables as a second chunking strategy — DONE, outcome below (decision in "targeted questions and a rank metric")

**Why.** Every new filer so far has surfaced at least one table-chunking bug (NFLX: three; NDAQ: the
equity title; MSFT: the "(In millions)" header cut-off). No chunking code names a company - the rules are
about 10-K / US GAAP layout - but they reverse-engineer structure from `markitdown`'s Markdown, which
loses it: tables arrive with empty header rows, `$` and `)` in cells of their own, and spacer columns, and
the chunker guesses which row holds the periods, which rows are group labels, and where a group ends.

**Evidence from the raw HTML** (all four filings): 68-83 tables per filing use `colspan`, which
`markitdown` discards - the reason NFLX's Markdown rows are misaligned. 65-80% of numeric tables carry
inline-XBRL tags (MD&A tables mostly don't). Indentation styles are filer-specific (NDAQ has none), so
row hierarchy can't come from indentation.

**Approach.** Linearize tables from the HTML (with `colspan` expanded into a real grid) before
`markitdown`: each row becomes one self-contained line, e.g.
`Comprehensive income — Year Ended June 30, 2026: $133,812 | 2025: $104,075 | 2024: $88,889`, and each
chunk repeats its statement title and units. Split points stop mattering, which removes the whole
header-repeat / label-carry class of bugs. Group-label scoping ("label row, closed by a Total row") stays
a heuristic - linearization makes it uniform, not solved.

**Decisions (user-approved):**
- **A second strategy, not a replacement.** `Chunking:Strategy` selects `Markdown` (the original,
  untouched) or `Linearized`; each has its own `rag.<strategy>.db` and `chunk-review/<strategy>/`, so
  switching needs no re-embed and both can be compared side by side. Tables the linearizer can't convert
  fall back to the Markdown strategy's table code (reused, not copied). `Markdown` stays the default
  until `Linearized` wins on the numbers.
- **AngleSharp for HTML parsing.** Microsoft Learn names no preferred HTML parser: .NET has none built
  in; the only Microsoft HTML DOM APIs are the IE-backed WinForms `HtmlDocument` and .NET-Framework-only
  `System.Web.Razor.Parser.HtmlMarkupParser` ("not intended to be used directly"); MEDI still ships only
  MarkItDown and Markdig readers (`10.9.0-preview`). Microsoft's own ASP.NET Core integration-testing docs
  use AngleSharp to parse HTML. AngleSharp: .NET Foundation project (per its repo README and the
  foundation's older project page; the current listing loads dynamically and couldn't be confirmed), MIT,
  WHATWG-compliant parser; NuGet 1.8.2 (2026-09-18), targets net10.0 with no dependencies, no
  vulnerability/deprecation flags. Pin 1.8.2 and run the vulnerable-package check when adding it.
- **Inline XBRL as a test oracle only, not at runtime.** Using it to align columns would make the check
  circular (verifying the linearizer with its own input). Design, to avoid the oracle's own heuristics:
  - Label-free consistency: values the linearizer puts in the same column must share an XBRL context
    (period + dimensions), values in different columns must not - no matching of header text to dates.
  - Dimensions matter: 95-98% of contexts are dimensional (the equity statement's columns are
    components, not periods).
  - Report coverage next to accuracy - untagged (mostly MD&A) tables get no automatic check.
  - Scope first to the five primary statements; a Python tool beside `replay_recall.py`.
  - XBRL is also less filer-independent than it looks: 5-16% of facts use company extension concepts
    (`ndaq:` 255, `orcl:` 186), the same idea maps to different concepts (revenue: `us-gaap:Revenues` at
    NFLX, `RevenueFromContractWithCustomerExcludingAssessedTax` at MSFT/NDAQ, both at ORCL), and the tag
    syntax varies by filing agent (NFLX lowercases its tags). Runtime XBRL (question -> concept lookup)
    is a different, text-to-query project.

**Steps:**
1. Refactor: `IChunkingStrategy`, `Chunking:Strategy`, per-strategy index/dumps; the `Markdown` strategy's
   output must be byte-identical to the committed dumps.
   **Done (2026-09-24):** `MarkdownChunkingStrategy` holds the original convert/split/chunk code, moved
   verbatim from `Program.IngestFilingAsync`; `Chunking:Strategy` binds to an enum (a typo fails at load);
   the manifest records the strategy; dumps moved to `chunk-review/markdown/` with `git mv`. Verified: 141/141
   tests (two new: strategy change detected by the manifest, misspelled strategy fails at load); a fresh
   `rag.markdown.db` build regenerated all four dumps **byte-identical to HEAD** (git blob hashes) - 1,440
   chunks; `replay_recall.py` (now taking the index path) 22/22; the full 24-question run's answers and
   filter lines identical, word for word, to the pre-refactor 24/24 run. The old single `rag.db` was
   deleted.
2. Spike (no pipeline change): standalone linearizer over all four filings, reporting coverage, the XBRL
   consistency check, token impact, and samples of the hard cases (MSFT no-`colspan`, NFLX `colspan`,
   multi-row equity headers, negatives, percentages). **Check-in: go/no-go on the numbers.**
   **Done (2026-09-24) - user decision: go.** `HtmlTableLinearizer` (in the app project, not yet wired
   in), `tools/LinearizeSpike` (runner, outside the solution), `tools/xbrl_column_check.py` (oracle).
   AngleSharp 1.8.2 added; `dotnet list package --vulnerable --include-transitive` clean.

   | | MSFT | NDAQ | NFLX | ORCL |
   |---|---|---|---|---|
   | Non-empty tables | 88 | 116 | 80 | 87 |
   | Fallback (kept as Markdown) | 1 | 1 | 2 | 1 |
   | Primary statements linearized | 5/5 | 5/5 | 5/5 | 5/5 |
   | Tokens vs Markdown, full format | -39% | +36% | -10% | -4% |
   | Tokens vs Markdown, compact (no nil `—`, shared caption once) | -49% | +5% | -27% | -23% |

   - Fallbacks: 4 exhibit indexes + 1 small NDAQ table (5 of 371) - content preserved as Markdown.
   - XBRL oracle: primary statements **1,458/1,470** tagged values aligned; the 12 are NFLX's equity
     statement, verified correct by hand - NFLX tags share repurchases with `CommonStockMember` while the
     rest of that column uses `CommonStockIncludingAdditionalPaidInCapitalMember` (a filer tagging
     inconsistency, i.e. an oracle false positive). All tables: 5,060/5,143 (98.4%); every one of the 24
     flagged tables was read and renders correctly (arithmetic spot-checked on four) - the flags are
     layouts the oracle can't model (quarter rows, fair-value levels, inconsistently tagged segments).
     Year-in-label vs XBRL period: 0/327 mismatches. Blind spot: untagged (mostly MD&A) values, ~30%,
     verified by reading only.
   - **The oracle itself needed two corrections before it could be trusted:** "any constant aspect"
     was vacuous (plain statements have no dimensions, so every column was trivially dimension-constant)
     - replaced by the aspect (or pair of aspects, incl. unit) that *distinguishes* columns; and
     roll-forwards mix a duration with its opening/closing instants, now mapped to that duration.
   - Layout patterns found by reading real output, each fixed with a general rule, not per filer:
     units row spanning every column (NFLX/NDAQ) swallowed all columns into one; a year row placed
     *below* Shares/Amount (MSFT repurchases) collapsed into a caption; group labels wider than the label
     column (NDAQ) read as headers - label column now decided by where a cell starts; one header over two
     values (amount / %: NFLX "Change", tax-rate tables) now sub-columns, not a conflict; a period row in
     the label column ("June 30, 2026") now stays on every row path; a units cell among headers is the
     units, and a table whose only header was units reads "Label: value".
   - Known rough edges: tables of contents read awkwardly ("Page: Business / 1"); group-label scoping is
     still heuristic; `HtmlTableLinearizer` has no unit tests yet (step 4). The spike measured table
     fidelity only - retrieval/answer impact is measured in step 4.
   - Compact rendering is the chosen format; each chunk must then repeat title + units + shared caption.
3. `Linearized` strategy: HTML pre-pass -> `markitdown` -> sections -> row-atomic chunks with a repeated
   title/units line; fallback tables via the existing table code.
4. Verify both strategies side by side: unit tests from real rows of each filer, the StatementType
   transition check, `replay_recall.py`, the full 24-question run, reading the dumps.
5. Decision-Log outcome, README, plan.

**Steps 3-4 done (2026-09-24). Default stays `Markdown` - see "Why the default didn't change".**

**How it's built.** `LinearizedChunkingStrategy` replaces each table `HtmlTableLinearizer` can linearize
with a `<pre>` holding a `RowBlock` (`#rows` marker, optional `#context` line = units + shared caption,
one line per row). Checked directly that markitdown turns `<pre>` into a fenced block with no Markdown
escaping (`$`, `*`, `_`, `<` and line breaks survive). Fallback tables stay HTML and reach TokenChunker as
Markdown tables. Shared code gained, all inert for the Markdown strategy:
- `SectionSplitter` copies fenced blocks through untouched: a linearized table of contents reads
  "PART I" / "Item 1. — Page: ...", and "PART I" only counts as a boundary the first time it's seen, so
  a TOC read as headings would hijack every Part heading after it. No filing contains `<pre>`, so the
  Markdown output has no fences.
- `TokenChunker` treats a row block as table-like (never overlap, never a lead-in). An oversized one
  splits between rows only, and **every piece repeats the lead-in (statement title, units) and the
  context line** - the context MSFT's Q8 total lacked on its second Markdown piece. A row block that fits
  alone but not after its short lead-in takes the lead-in along instead of leaving a title-only chunk.
- `MarkItDownConverter.ConvertHtmlAsync` (HTML already decoded and cleaned) split out of `ConvertAsync`.
- `--chunks-only`: chunk every filing and write `chunk-review/<strategy>/`, no Ollama, no index - a
  one-minute loop for reading real chunk output instead of a ten-minute re-embed.

**A real bug the spike's checks couldn't catch.** Comparing the two strategies' dumps showed 8 MSFT
exhibit numbers missing: the exhibit rows after 10.17 ("31.1 | Certification of Chief Executive
Officer ... | X") carry no numbers, were read as a header block, and no data row followed to consume it -
silently dropped. The XBRL oracle only sees tagged numbers, and the spike compared figures, not text.
Fixes: header rows no data row follows are kept as plain lines; and a **content guard** - every non-empty
cell's text must appear in the rendered output, or the table falls back to Markdown (content intact).
Across all 371 tables the guard fired once more (the same MSFT exhibit table's "Filed Herewith" header,
over non-numeric "X" marks - now a fallback): no other table loses any cell text. XBRL results unchanged.

**Verified:**
- `dotnet test` **159/159** (18 new: `HtmlTableLinearizerTests` - one fixture per layout rule, each
  reduced from the filing that needed it, column positions kept - and `LinearizedStrategyTests`: RowBlock
  round trip, HTML rewrite, fenced rows never headings, row-block splitting).
- Markdown strategy after all shared-code changes: dumps **byte-identical** to HEAD, 1,440 chunks.
- Linearized dumps: section outline identical to Markdown's in all four filings; every figure in the
  Markdown dumps present (0 missing); every visible word present - the only differing "words" are link
  targets (`#item_10_directors_executive_ficers_corpo`, EDGAR exhibit URLs); no leftover fences/markers,
  no U+FFFD. StatementType transitions identical to Markdown's (five statements, then the Notes reset,
  every filing).

| | Markdown | Linearized |
|---|---|---|
| Chunks | 1,440 | **994** (-31%) |
| Embedding time | 587 s | **377 s** |
| Statement chunks (e.g. ORCL / NFLX equity) | 16 / 13 | 4 / 3 |
| `replay_recall.py` (figure in top-5 context) | 22/22 | 22/22 |
| Top-1 distance closer to the query | 6 questions | 15 (3 equal) |
| Full 24-question run (temperature 0) | 24/24 | 24/24, one framing slip |

- MSFT's comprehensive income statement is now one chunk with title, units and years - the Q8 failure
  class (a total split away from its title and years) is structurally gone.
- The framing slip: Q5 (ORCL operating cash) gives the right figures but says they're in the "Changes in
  operating assets and liabilities" section; Q17's citation shows the same path. Cause: the group-label
  heuristic (a group closes only at a "Total ..." row) - shared with the Markdown strategy, but more
  visible when the group path is written on every row.

**Why the default didn't change.** The plan said `Markdown` stays default until `Linearized` wins on the
numbers; this is a tie on answers, and the tie says little:
- The question set is saturated (22/22 and 24/24 on both) and was written and debugged against the
  Markdown strategy - every fix tuned until these questions passed.
- It's almost all headline totals. Linearization's claimed advantage - figures a split used to separate
  from their context, mid-table rows, MD&A tables - is barely exercised (Q8 only, already fixed on
  Markdown).
- Top-1 distance measures how close the best chunk is, not whether it's the right one.
- A rank metric (recall@1, MRR) on 22 questions still moves by noise-sized steps.
- Linearized's residual risk isn't covered by any automatic check: a value under the wrong header in an
  *untagged* table (~30% of values, verified by reading only). Its rules also came from these four
  filings - better safety nets than Markdown's (content guard, fallback), same "derived from what we've
  seen" caveat.

**Next:** rank metric in `replay_recall.py` plus ~10 targeted questions aimed at what linearization
claims to fix (mid-table figures, year-below-subcolumn layouts, amount/% pairs, MD&A tables), expected
figures from the source, run on both strategies; then decide the default. If still a tie, keep
`Markdown` and record the tie - a measured "no difference" is a legitimate result.

**Known, not fixed - group-label scope.** The obvious rule "a row starting with Net or Total closes the
group" is wrong on this data: MSFT's OCI group *contains* "Net change related to derivatives" / "... to
investments". Layout has no reliable end-of-group signal (NDAQ has no indentation; `$` marks first rows
and totals alike); XBRL's calculation structure would, but stays out of runtime. Plan if pursued:
measure the leak first, using indentation as a *test* oracle for the three filers that indent (a row
indented no deeper than its group label is outside it), and fix only if the rate and its effect on
answers warrant it.

## Follow-up: embedding-model comparison — DEFERRED (its first step, the rank metric, done; comparison not run)

**Why not now.** Retrieval isn't what's failing: `replay_recall.py` has 22/22 answerable questions with
the figure in the top-5 context, and every recent wrong answer had the right chunk in context. But a
saturated in/out-of-top-5 metric can't show a better embedder as better - and `EmbeddingTextBuilder`'s
row-label summary exists because sparse pipe tables embed poorly with `nomic-embed-text`, so there's
plausibly headroom.

**Order matters.** Linearization changes *what* gets embedded (self-contained rows read like the prose
embedders are trained on), likely a bigger effect than the model, and the two interact - so compare
embedders on both strategies, after linearization.

**Steps:**
1. Make the metric discriminating: `replay_recall.py` reports the rank of the first chunk containing the
   expected figure - recall@1, recall@3, MRR - not only in/out of the top 5.
2. Make the embedder swappable: the query/document templates (today `search_query: ` /
   `search_document: ` in `RagAnswerService` and `BuildRecords`) and the vector dimension (today
   `[VectorStoreVector(dimensions: 768)]` on `FilingChunkRecord`) move to settings beside
   `Ollama:EmbeddingModel`; `replay_recall.py` reads them instead of hardcoding nomic and `768f`. The
   manifest already refuses an index built with a different embedding model. A wrong template fails
   silently (worse retrieval, no error) - add it to Live constraints once it's configurable.
3. Index per combination (e.g. `rag.<strategy>.<embedder>.db`) so every combination stays built.
4. Compare `nomic-embed-text` (baseline) vs `qwen3-embedding:0.6b` vs `embeddinggemma` on both strategies:
   the rank metrics, then the full 24-question run for the best candidates. Record embed time too - it's
   CPU-only.

**Candidate facts** (checked against each model's Hugging Face card, 2026-09-24 - don't re-derive):

| Model | Query template | Document template | Max tokens | Dim |
|---|---|---|---|---|
| `nomic-embed-text` v1.5 (current) | `search_query: {q}` | `search_document: {d}` | 2048 native, scalable to 8192 | 768 |
| `nomic-embed-text-v2-moe` | `search_query: {q}` | `search_document: {d}` | **512** | not checked |
| `qwen3-embedding:0.6b` | `Instruct: {task}` + newline + `Query: {q}` | none | 32K | up to 1024 |
| `embeddinggemma` (300M) | `task: search result \| query: {q}` | `title: none \| text: {d}` | 2048 | 768 |
| `mxbai-embed-large` (335M) | `Represent this sentence for searching relevant passages: {q}` | none | 512 (Ollama's model page) | not stated |

- Nomic's card makes the prefix mandatory ("the text prompt *must* include a task instruction prefix").
  Qwen's says omitting the query instruction costs ~1-5% - a wrong template degrades silently, never errors.
- **Excluded for now: 512-token models** (`nomic-embed-text-v2-moe`, `mxbai-embed-large`). Current chunks
  reach ~800 cl100k tokens, more under other tokenizers, so table rows would be silently truncated.
  Reconsider only with smaller linearized chunks.
- Qwen3 4b/8b are impractical for a ~10-minute CPU-only build; 0.6b is already ~4x nomic's size.

## Follow-up: targeted questions and a rank metric - Markdown vs Linearized — DONE, outcome below

**Why.** The 24-question set was saturated on both strategies (22/22 replay, 24/24 answers), written and
debugged against Markdown, and almost all headline totals - it couldn't tell the strategies apart. Two
additions, both in `tools/`:
- **Rank metric** in `replay_recall.py`: recall@1/@3/@5 and MRR per question group (Q1-Q24, T1-T10,
  R1-R2), from the rank of the first chunk holding the expected figure(s).
- **12 new questions** (`tools/manual-questions.txt` lines 25-36; expected answers, traps and sources in
  `docs/Manual-Test-Questions.md`): T1-T10 aimed at what linearization claims to fix - mid-table rows,
  split layouts, MD&A/notes tables - and R1-R2, routing tests whose keyword route excludes every chunk
  holding the answer. Each expected figure was confirmed present in both strategies' dumps, and each
  question's route was taken from the real `QueryIntentResolver` (run via reflection), not predicted -
  predicting had already been wrong once (T10).

**Before trusting the tools:** the rank metric reproduced the earlier recall@5 22/22 on both indexes. A
parsing bug surfaced mid-analysis: the model once wrote an answer line as a Markdown blockquote ("> Share
repurchase program - ..."), which the replay read as a question prompt, shifting every later question
onto the wrong log block (T9 appeared to run unfiltered). Fixed: a block counts as a question only if it
opens with the filter line or the retrieved-chunks header. Temperature 0 held: the Q1-Q24 answers of
both strategies were identical to their previous runs.

**Results (both indexes, 36 questions, temperature 0; filter lines identical between strategies):**

| Retrieval | Markdown | Linearized |
|---|---|---|
| Q1-Q24: recall@1 / @3 / @5, MRR | 12 / 19 / 22 of 22, 0.714 | 14 / 21 / 22 of 22, 0.784 |
| **T1-T10: recall@1 / @3 / @5, MRR** | 3 / 4 / **4** of 10, 0.391 | 3 / 5 / **7** of 10, 0.463 |
| R1-R2 | 0/2 | 0/2 |

| Answer | Markdown | Linearized |
|---|---|---|
| T1 MSFT OCI FY25 ($2,243M) | right, but *computed* as CI minus NI ("not explicitly stated") | right, read from the row |
| T2 MSFT Q2 FY25 repurchases ($3,500M) | declined (rank >25) | **right** |
| T3 NFLX T&D change (+16%) | declined (rank 16) | declined (rank 8) |
| T4 ORCL FY28 operating leases ($3,603M) | right (rank 2) | **declined (rank 18)** |
| T5 MSFT U.S. govt securities ($48,562M) | **wrong: $19,100M** - another table's "government and agency" row | declined |
| T6 NDAQ FinTech goodwill ($7,952M) | declined (rank 25) | **wrong: $5,933M** - the Adenza acquisition's goodwill, with the right chunk at rank 1 |
| T7 MSFT Ireland rate effect ((2.6)%) | right | right |
| T8 NDAQ Nov 2025 avg price ($91.47) | declined (rank 6) | **right** |
| T9 ORCL FY25 dividends ($4,743M) | wrong year | wrong year ($5,725M is FY26) |
| T10 NFLX 2024 hedge reclass ($(96,795)K) | right | right |
| **T1-T10** | **4 right, 4 declined, 2 wrong** | **5 right, 3 declined, 2 wrong** |
| R1-R2 | declined | declined |

Every wrong answer or decline in the Markdown run had the answer outside the top 5 - these questions
test retrieval, as intended.

**Reading it critically.** Linearized's retrieval advantage is now visible on the class of question it
was built for (7 vs 4 of 10 in the model's context) and slightly on the original set. The answer gain is
+1 of 10 - within noise at this size - because one regression and one generation error offset it:
- **T4 - a real trade-off of denser chunks.** Linearized rows are compact, so one 500-token chunk now
  holds two small ORCL tables (supplemental lease cash flows *and* lease maturities); the mixed chunk
  embeds as the former and ranks 18th. In Markdown the maturities table was its own chunk, caption first.
- **T6 - generation, not retrieval.** The right chunk ranked first; `llama3.1:8b` took a similar-looking
  goodwill figure from an acquisition table instead. Better retrieval can't fix that.
- Wrong answers are 2 each; the Markdown ones are confident misreads of other tables (T5) - Linearized
  declined there instead.

**Decision (user): record both strategies with this comparison and stop; `Markdown` stays the default.**
The project was one manual pass away from done before this work; each further round adds scope. The
comparison itself - a measured, modest retrieval gain for tables, with the trade-offs named - is the
result.

**Known, not fixed (follow-ups if the work resumes):**
- **Roll-forward period gap (both strategies, T9).** An equity roll-forward row doesn't carry its fiscal
  year - it's implied by the "Balances as of May 31, 20xx" row above it. Possible fix: treat such balance
  rows as period markers and put the period on the following rows.
- **Table mixing in Linearized chunks (T4).** Possible fix: a row block never shares a chunk with another
  table block (costs chunk count; measure with the replay).
- **Keyword routing is substring matching over a hard filter.** "Deferred revenues" contains "revenues"
  and routes to the income statement, so R1 can never find the balance-sheet figure; "operating income"
  does the same for segment tables (R2). Colliding keywords silently drop the filter instead: "cash flow
  hedge" contains "cash flow", so T10 ran with no statement filter. The statement filter is also coarse -
  93% (Markdown) / 95% (Linearized) of chunks are `narrative`. Considered, measured, not built: a soft
  filter (filtered + company-wide results merged), and hybrid keyword + vector search - `IKeywordHybridSearchable`
  exists in MEVD, but `CommunityToolkit.VectorData.SqliteVec` 1.0.1-preview (the latest) doesn't
  implement it; SQLite FTS5 does work in the app's SQLite (3.50.4, checked), so it would be an own FTS5
  table plus rank fusion. Ollama has no rerank endpoint (checked in its API docs), so cross-encoder
  reranking isn't available locally.
- **Embedding-model comparison** (planned above) - deferred with the rest; its first step, the rank
  metric, now exists.

**Tooling added alongside (Claude Code, not the app):** `.claude/agents/filer-onboarding-checker.md`, a
read-only subagent that runs the onboarding checklist on a new filing and reports PASS/FAIL with
evidence (`omitClaudeMd: true` - it reads only the plan's Live constraints section instead of reloading
~19 KB of CLAUDE.md + plan); and the test conventions moved from CLAUDE.md into the path-scoped
`.claude/rules/testing.md`, loaded only when a test file is read.

## Follow-up: pre-manual-pass review — lost first chunk, back-matter headings, output ceiling — DONE, outcome below

**Why.** A critical review before the manual pass, checking the code against the real indexes and the
SEC's own Form 10-K instructions (the form PDF: Items 1-16, cover-page fields, Part III incorporated by
reference from the proxy statement) rather than against the test set.

**Bug 1 - the first chunk of every build was silently dropped.** `BuildRecords` keyed records from 0, and
an `int` key of 0 is the vector store's "generate a key" value: SqliteVec stored chunk 0 under a generated
key 1, and the real key-1 chunk then overwrote it. Found by comparing row counts: 1,439 rows vs 1,440
dumped chunks (Markdown), 993 vs 994 (Linearized), key 0 absent from both and key 1 holding chunk 2.
Confirmed with a standalone repro against `CommunityToolkit.VectorData.SqliteVec` 1.0.1-preview (upsert
keys 0, 1, 2 -> `Get(0)` null, `Get(1)` = "chunk 1"). The lost chunk was MSFT's cover page: its street
address, state of incorporation, I.R.S. number, commission file number and registered-securities table
("STATE OF INCORPORATION" appeared nowhere in either index). "Where is Microsoft headquartered?" was
*not* affected - Item 2 ("Our corporate headquarters are located in Redmond") ranks 1st either way. No
test question touched MSFT's cover page, so the suite couldn't catch it.
Fix: `BuildRecords` moved out of `Program.cs` into `VectorStore/FilingChunkRecords.cs` (testable), keys
start at 1; and after upserting, the build counts the stored records (`GetAsync(r => true, ...)`) and fails
before writing the manifest if any are missing.

**Bug 2 - NFLX's and NDAQ's financial statements were headed "Item 16. Form 10-K Summary".** Form 10-K
lets a filer put the financial statement pages after Part IV (referenced from Item 8/15). NFLX and NDAQ
do, right after "Item 16. Form 10-K Summary - None.", and `SectionSplitter` kept the last heading it saw:
249 NFLX and 110 NDAQ chunks - every statement, auditor's report and Note - carried that heading into the
embedding text and the model's citations. ORCL's exhibit index and every filer's signatures landed there
too. Found by tabulating chunks per Item per filing against the form's item list.
Fix: in Part IV only, the standalone titles "INDEX TO (CONSOLIDATED) FINANCIAL STATEMENTS", "SIGNATURES"
and "EXHIBIT INDEX"/"INDEX OF EXHIBITS" start their own heading ("PART IV > Financial Statements" /
"Signatures" / "Exhibit Index"). Each appears only as back matter in all four filings (checked in the
dumps); the auditor's report title isn't usable as a signal - it also appears inside Items 8 and 9A. Part
IV only, because an index to the statements inside a filer's Item 8 is already headed correctly.

**Config fix - `Retrieval.MaxOutputTokens` 4096 -> 768.** It applies to every chat model, not only
reasoning ones (`RagAnswerService` sets it unconditionally; OllamaSharp maps it to `num_predict` - read in
its `AbstractionMapper`). The app never sets `num_ctx`, so Ollama's default 4,096-token window holds prompt
*and* output: a 4096 output ceiling could never be reached. Ollama's server log showed seven runaway
`llama3.1:8b` answers on 2026-09-23 (before temperature 0) generating 1,375-1,673 tokens until the window
filled, with `truncated = 1` and `n_keep = 4` - Ollama then drops the oldest tokens, i.e. the system prompt
and the top-ranked chunks. From the 2026-09-24 runs (243 answers): answers at most 274 tokens (p95 172),
prompts at most ~2,980. 768 is ~3x the longest answer and leaves ~350 tokens spare (1,024 would have left
92). A reasoning model gets little room to think within 4,096; that needs `num_ctx` raised. Added to the
plan's Live constraints, with a unit test on the shipped value.

**Soft statement filter - now actually measured** (the "Keyword routing" follow-up above said "considered,
measured, not built"; only the narrative share had been measured). Simulated retrieval-only against both
indexes with a Python port of the routing, validated by reproducing the replay's hard-filter numbers
exactly. Top-5 recall:

| | Hard (shipped) | No statement filter | Soft (filtered/unfiltered interleaved) |
|---|---|---|---|
| Q1-Q24, Markdown / Linearized | 22/22 / 22/22 | 11/22 / 15/22 | 21/22 / 21/22 |
| T1-T10, Markdown / Linearized | 4/10 / 7/10 | 3/10 / 6/10 | 4/10 / 6/10 |
| 8 new probes (segment revenue, revenue recognition, drivers, cash-flow risk, ...) | 1/8 | 6/8 | 5-6/8 |

The statement filter is load-bearing (removing it halves Q1-Q24). Soft filtering trades one curated
question (Q20 to rank 6; T9 on Linearized) for 3-5 probe questions - and the probes were written after
seeing the failure, two with loose needles. It can't buy slots back with a larger `GenerationTopK`:
prompts already reach ~3,000 of the 4,096-token window. Section (Item) routing was also considered and
not pursued: questions it would target (Q11-Q13, Q19) already rank 1-4 unfiltered. Reading company
identity from the cover page's `dei:` inline-XBRL tags (all four carry them) was considered for automatic
`CompanyToFiling` registration and declined: registration is two lines per filing and already guarded;
deriving aliases ("MICROSOFT CORPORATION" -> "Microsoft", ORCL's several trading symbols) adds a new silent
failure mode. Not built; recorded here.

**Verified after `--rebuild` of both indexes:** 1,444 / 999 records stored, keys 1..N, matching the dumps;
MSFT's cover page at key 1; one statement-type run per primary statement in every filing, same chunk
counts as before; NFLX/NDAQ statement chunks headed "PART IV > Financial Statements". Replay (retrieval
only, no LLM) top-5 totals unchanged: Q1-Q24 22/22 both, T1-T10 4/10 and 7/10, R1-R2 0/2. 170 unit tests.
The full question run with answers wasn't repeated - the manual pass covers it.

**Docs updated alongside.** README gained a "Known limitations" entry for the hard filter. Checked before
writing it, on the rebuilt indexes: the misses aren't only narrative questions - segment and regional
revenue, operating margin and deferred revenue are figure questions that rank 1-6 unfiltered and are
unreachable routed. Asked live, the model declined segment and regional revenue and *computed* NFLX's
operating margin from the income statement (29.5%, matching the MD&A's figure). New routing test **R3**
("Microsoft's Intelligent Cloud segment revenue", expected: a decline; the figure is $137,791 million)
in `docs/Manual-Test-Questions.md`, line 37 of `tools/manual-questions.txt` (before the session-ending
blank line) and `replay_recall.py`. The rebuild shifted seven NDAQ/NFLX line citations in
`Manual-Test-Questions.md` by 1-6 lines (back-matter title lines removed, boundaries moved); each was
re-pointed and checked against the new dumps, and Q17's note now expects the `PART IV > Financial
Statements` citation.

**Program.cs split (same day, before the manual pass).** `Program.cs` had grown to 465 lines, of which ~100
were the startup flow. Moved without behavior changes, each comment travelling with its code:
`OllamaSetup` (HTTP client, readiness and thinking-capability checks), `VectorStore/IndexFiles` (the
index's paths, `EnsureCurrent`, `Delete` - replacing a dbPath/manifestPath string pair),
`VectorStore/IndexBuilder` (chunking, dumps, upsert and the stored-count check) and `InteractiveSession`
(the question loop and its output). `Program.cs` is now 131 lines. `PrintMatchedFilter` became
`InteractiveSession.FormatMatchedFilter`, returning the "(filtering to ...)" line - the line the manual pass
checks and `replay_recall.py` parses - so its format is now unit-tested (6 cases, 176 tests). Verified as a
pure move: `--chunks-only` dumps byte-identical on both strategies, and an end-to-end `--verbose` run
against the existing index gave the same filter lines and answers (MSFT total assets; the Apple decline).

**Docs restructured alongside (no code change).** Added `docs/Design-FAQ.md`: short answers to the "why
not X?" questions (XBRL, PDF/OCR, MEDI, the hard filter, reranking, the chat and embedding models,
temperature, batch size), each pointing to its section here, with no counts that go stale. It is the only
record of the 2026-09-25 Print-to-PDF and OCR tests, by the user's choice. This log was not shortened -
its sections back the FAQ's claims - but gained an index, two corrected status headings (the linearized
strategy read "PLANNED, in progress" after it was done; the embedding comparison read "PLANNED" after it
was deferred), and superseded-pointers on Steps 2, 4 and 5.

**Correction - hybrid search was never measured.** The "targeted questions and a rank metric" entry lists
hybrid keyword + vector search under "Considered, measured, not built". Only its feasibility was checked;
its effect was never measured. Re-verified 2026-09-25: `IKeywordHybridSearchable<TRecord>` exists in
`Microsoft.Extensions.VectorData.Abstractions` 10.10.0, but no type in `CommunityToolkit.VectorData.SqliteVec`
1.0.1-preview implements it (reflection; the live collection also returns null from `GetService`), 1.0.1-preview
is still the newest NuGet release, and Microsoft's SQLite connector page lists "HybridSearch supported? No"
and "IsFullTextIndexed supported? No". FTS5 is compiled into the bundled SQLite 3.50.4 (`MATCH` + `bm25`
checked), so a hand-built FTS5 table plus rank fusion remains the only local route. Why it wasn't built:
scope - it was listed as a follow-up when the user chose to stop adding scope before the manual pass.
The same connector page lists "IsIndexed supported? No", while `FilingChunkRecord` marks its filter
properties `IsIndexed = true` with a comment calling that required for filtering - unverified which is
stale; harmless either way, since the filters demonstrably work.

## Follow-up: XBRL hybrid (v2) — PLANNED, after the v1 push

**Why.** The hard statement filter can't reach answers outside the primary statements (segment and
regional figures, policies, MD&A drivers), and pure RAG over tables is the weakest way to answer headline
figures. The filings carry structure that addresses both, checked 2026-09-25 in all four:
- **Section-level tags.** Each filing tags 67-91 blocks of text (notes, policies, schedules) as inline-XBRL
  text blocks, ~75% under standard `us-gaap` names shared across filers - e.g. all four tag their segment
  table `us-gaap:ScheduleOfSegmentReportingInformationBySegmentTextBlock`; three tag the revenue policy
  `RevenueFromContractWithCustomerPolicyTextBlock`, NFLX `RevenueRecognitionPolicyTextBlock`. That labels
  note topics filer-independently - including ORCL's unnumbered notes, which heading patterns can't.
- **Tagged facts.** Headline figures are standard concepts; the linearizer already pairs each tagged value
  with its row label, column label and concept (that's how the XBRL oracle works), so a label -> concept
  mapping can be derived per filing instead of hand-written. Segment figures are dimensional, with
  company-specific members (`msft:IntelligentCloudMember`, `ndaq:CapitalAccessPlatformsMember`, ...), whose
  names can be turned into labels.

This doesn't reverse the "linearized tables" decision to keep XBRL out of runtime: that was about aligning
table columns (circular with the oracle) and about a general question -> concept lookup. Text-block labels
are a separate use; the figure lookup (phase B) is the text-to-query problem that entry flagged, now scoped.

**Decisions (user, 2026-09-25):**
- **v1 first.** Manual pass and push of the current state; v2 on a branch.
- **Phased, stop on bad numbers.** Each phase is spiked and measured (replay, both strategies, Q1-Q24 as the
  no-regression bar) before it's built; stop after any phase whose numbers don't justify the next.
- **Section labels as a setting on both strategies**, not a third chunking strategy - so a gain is
  attributable to the labels, not to the chunking.

**Phases:**
- **A - XBRL section labels on chunks**, used for routing (e.g. "segment" -> the segment text block).
  Main unknown: carrying HTML text-block regions through markitdown onto chunks.
- **B - figure lookup from tagged facts** for headline statement items, passed to the model as cited
  context. Unknowns: period resolution per fiscal-year end, dimensional (segment) facts.
- **C - router** between B and retrieval; fallback to retrieval when no fact matches. Carries today's
  misrouting risk, so measured like the statement filter.
