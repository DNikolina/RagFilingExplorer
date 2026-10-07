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
- **Follow-up: XBRL hybrid (v2)** - done, tag `v2.0` (2026-10-02). `Structured` chunking from the HTML and inline XBRL (no markitdown), hybrid search (vector + FTS5, RRF); reranking built and left opt-in; answer-side rule and calculator screened, not built.
- **Follow-up: manual pass (v1)** - done. Main set 23/24 correct, 16/24 reliable (units); targeted 3/10; routing and negatives all declined. Prompt change re-run: 22/24 reliable on both strategies; two further prompt revisions tried and not kept; `Markdown` stays default.
- **Follow-up: evaluation in .NET (v3)** - planned: the strict grader and retrieval rank as `Microsoft.Extensions.AI.Evaluation` custom evaluators, with reporting and response caching; each step must reproduce the Python tools exactly.

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

**Effort levels checked (2026-10-07, Ollama 0.40.0): on these models the level is an on/off switch.** The request
OllamaSharp 5.4.30 sends, logged by a throwaway probe on the app's package versions: `Effort.None` -> `"think": false`,
`Effort.Medium` -> `"think": "medium"`, temperature and `MaxOutputTokens` as `options.temperature`/`num_predict`.
`qwen3.5:2b` asked the same question at temperature 0 with `think` = `true`, `"low"`, `"medium"`, `"high"` and
`"bogus"`: five identical responses (2,235 characters of thinking, 879 tokens, the same answer). Ollama's `/api/show`
now says why - `qwen3.5:2b` reports `"thinking": {"values": [false, true], "default": true}`, and per Ollama's docs
(docs.ollama.com/capabilities/thinking) a named level is honoured only by a model that lists it there (their example:
`gpt-oss`, low/medium/high); other names fall back to the model's default, on for qwen. `llama3.1:8b` and both
granite4.1 models report no thinking capability at all. So for every model this project has run, only `None` versus
any other value matters; the levels matter for `gpt-oss` (not pulled, not measured) and paid reasoning models (v4).
Kept as an effort level, not replaced by a boolean (user): it's Microsoft.Extensions.AI's provider-neutral setting
and v4 needs the levels. Fixed: the setting's comment (`AppSettings.cs`) and the README say this, and the
starved-response error now suggests setting `ReasoningEffort` to `None`, not "lowering" it - lowering does nothing
on qwen. Also seen: qwen thinks by default, so the explicit `"think": false` on lookups is load-bearing. Reading
`thinking.values` instead of the capability list would tell levels from on/off - left for v4.

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

## Follow-up: XBRL hybrid (v2) — DONE (tag `v2.0`), outcome below

**Rewritten 2026-09-25, after the manual pass** (the first, phased plan is kept at the end of this section).
The pass showed that most failures trace to how the filings are turned into chunks - lost column structure,
units and titles only on a table's first piece, ~50% empty-cell padding - plus model habits no ingestion
fixes. So v2 is a new *ingestion* design built from first principles, not a layer on the Markdown path.

**Principle: build on what the SEC standardizes, not on how the page looks.** A 10-K has three layers:
- the **Form 10-K structure** (Parts, Items 1-16) - fixed by regulation;
- **inline XBRL** - every financial number tagged with concept, period (context), unit and scale; notes and
  policies tagged as text blocks with ~75% standard `us-gaap` names; cover facts (company, fiscal year end).
  Checked 2026-09-25: every tagged number carries `scale` (6 = millions; 3 = thousands for 901 NFLX facts;
  -2 = percent) and `unitRef` (USD, shares, pure); 1,292-1,855 tagged numbers per filing, in 45-57 of each
  filing's 82-117 tables;
- the **visual HTML** - prose and tables, different for every filing agent.
Current pipelines, this one's included, rebuild structure from the visual layer. v2 starts from the regulated
layers and uses the visual layer only for what they don't cover (prose, untagged tables).

**Pipeline:**
1. Parse the HTML once as a DOM (AngleSharp); read the hidden XBRL header (contexts, units) before dropping it.
   No markitdown - which also removes the Python dependency.
2. **Facts table** from every tagged number: concept, value x scale, unit, period, dimensions (segment
   members), sign, and the row label beside it. Company identity and fiscal year end from the cover facts.
3. Blocks in reading order - headings (the form's PART/Item patterns plus generic short-bold-line rules),
   paragraphs, tables; page artifacts dropped by generic rules.
4. **Structure labels on every block:** Item path; note/policy topic from the enclosing XBRL text block;
   statement type from the *concepts* in a table (`us-gaap:Assets` -> balance sheet), replacing title regexes.
5. **Tables:** expand merged cells, drop spacer columns, keep **one table per chunk** (the T4 lesson), split
   between rows with title, units and period repeated. Units and periods from XBRL for tagged cells, from the
   caption/header for untagged ones.
6. **Two texts per chunk:** an embedding text (company, filing, section path, topic label, row labels) and a
   compact display text for the model.
7. One SQLite file: vectors, an FTS5 keyword index, and the facts table - still local and zero-cost.
Retrieval: statement/section labels as soft boosts rather than hard filters; hybrid (keyword + vector) search;
exact headline figures from the facts table; arithmetic in code (a calculator tool - `llama3.1:8b` supports
tool calling), never by the model.

**Measured choices, not assumptions** (each settled on the question set before it's kept):
- What the model sees for a table: self-contained row lines vs cleaned HTML table markup (what Microsoft's
  Content Understanding uses for merged cells) vs a clean grid for simple tables - HTML keeps structure but
  costs tokens, and reading tokens is the CPU bottleneck (~45 tokens/s).
- Model-written table summaries as embedding text - the strongest retrieval aid in the guidance, but one model
  call per table: ~370 tables x ~40 s ~ 4 hours per build on this hardware. Try on a subset first.
- Company/filing context on every chunk's embedding text (Microsoft's chunking guidance: append the document
  title to mid-document chunks).
Rejected: Azure Document Intelligence / Content Understanding themselves - paid cloud services; the techniques
are what's useful.

**Risks:** untagged tables (a third to a half of all tables, mostly MD&A and schedules) still need layout-based
parsing - the hardest part, with no XBRL help; XBRL isn't perfectly uniform (company extension concepts, tag
spelling by filing agent, dimensional complexity); heading detection without heading tags needs rules validated
per filer; none of it is measured yet.

**Carries over from v1:** the question set (Q, T, R, V), `replay_recall.py`, the index manifest, the retrieval
service and the test conventions - what makes it possible to show v2 beats v1 rather than assume it. Estimate:
one to two weeks at this project's verification standard. The v1 decisions stay: filer-independent rules only,
verify against real output, measure before switching.

**Answer skills - added 2026-09-28 (user's question after the manual pass).** One generic `SystemPrompt`
serves every question, and its rules leak across question types: the units rule became a whole decline (Q15,
T2), the arithmetic rule was read both ways (R2 invented a calculation, V3 refused a requested one). v2 answers
come in distinct kinds - a headline figure from the facts table, a retrieved passage or table, a calculation
via the calculator tool, a decline - so each gets its own instruction section on a small base prompt, in the
spirit of Agent Skills (instructions loaded only when relevant). Placed late, deliberately:
- **After ingestion.** XBRL supplies unit and scale for tagged figures, so the rule that failed most in v1
  (units, 7 of 24) largely disappears; instructions written earlier would target problems v2 removes.
- **After the compact display text.** Dropping the empty-cell padding (30-63% of statement tokens) frees the
  context room that sections and tool definitions need; v1 runs at 3,871 of 4,096.
- **Code selects first.** The router (facts vs retrieval, if step 4 is built) and `RequiresSynthesis` know the answer kind,
  so the section is added without an extra model call; a misclassification only drops a rule. The calculator
  uses `Microsoft.Extensions.AI` function calling, already in the stack.
- **Model selection only if measured.** Microsoft Agent Framework's `AgentSkillsProvider` (`SKILL.md` files,
  `load_skill` / `run_skill_script` tools) is the model-chosen form. Check first: its package status, whether
  `llama3.1:8b` picks the right skill, and the cost of the extra round trips (~50 s per cold prompt on this CPU).

**Storage stays SQLite - PostgreSQL considered 2026-09-28 and declined.** Postgres has no built-in hybrid
search: vectors need `pgvector`, `ts_rank` isn't BM25, and its .NET connector
(`CommunityToolkit.VectorData.PgVector`, preview) doesn't implement hybrid search per Microsoft Learn's connector
page - so hybrid is the same own query + rank fusion either way. SQLite's FTS5 (with `bm25()`) is already
confirmed working in the app's SQLite; Postgres would add a server, a compiled extension (or Docker) and a
second preview connector for no capability gain at ~1,000-1,450 chunks. Hybrid search in v2 = FTS5 `bm25()` +
sqlite-vec + reciprocal rank fusion in code. See Design-FAQ.md, "Why no reranking or hybrid search?".

**Plan review (2026-09-28, before implementation; all five points applied by the user's decision).**
1. **Step 1 bundled too much to measure.** DOM parse, facts, block detection, structure labels, table handling,
   two texts per chunk and new storage in one step leave a changed score unattributable - the reason the first
   plan made section labels a setting. Split into 1a-1d, each measured.
2. **The evaluation set is small and already tuned against** (40 questions, 4 filings, three prompt rounds), and
   v2 is designed around its failures, so v2 could beat v1 on it without being better. Grading is manual (~40 min
   of runs per strategy plus reading). Hence step 0.
3. **The facts table targets what already works.** Headline figures pass 22/24; the targeted losses are mostly
   retrieval (T2, T3, T6, T8, T9 - answer chunk ranked 6-25+). The facts table's hard part - question to
   concept + period + dimension - is the text-to-query problem flagged in "linearized tables", and mainly fixes
   Q10. Moved after hybrid search and the calculator, with its own go/no-go.
4. **Reuse v1's tested parts; settle the interface.** `HtmlTableLinearizer` already parses the DOM with
   AngleSharp (a dependency already) and handles `colspan`; `tools/xbrl_column_check.py` already reads inline
   XBRL. "From first principles" is the design, not a rewrite - untagged tables, the hardest part, are where the
   linearizer helps most. `IChunkingStrategy` returns only sections and chunks; v2 also produces facts and two
   texts per chunk, so step 1a decides: extend the interface, or a separate ingestion path sharing retrieval and
   evaluation.
5. **Separate model limits from pipeline limits.** Lookalike lines (Q10, V1) and arithmetic may be
   `llama3.1:8b` itself; one run on a second small model (to be pulled - only `qwen3.5:2b`, a reasoning model,
   is local) bounds what pipeline work can gain. The no-regression bar is the strict grading: **22/24
   reliable** (unit + exact line), not the looser 24/24.

**Order, each step measured on the question set before the next:**
- **0. Evaluation first.** A held-out set of ~15 new questions with expected answers, written before any v2
  output and never tuned against; an automatic answer grader for figure questions (expected digits + unit in the
  answer, as `replay_recall.py` does for retrieval); v1's per-question results saved as the baseline file; then
  the second-model run.
- **1. Ingestion, in parts:** 1a DOM parse (no markitdown) with today's chunking, and the interface decision;
  1b structure labels (Item path, XBRL text-block topic, statement type from concepts) - split into 1b-i to
  1b-iii below; 1c one table per chunk + compact display text; 1d embedding text with company and section context.
- **2. Hybrid search** - FTS5 `bm25()` + sqlite-vec + rank fusion; statement labels as soft boosts.
- **2b. Reranking spike** - can a small cross-encoder rerank the top 20-25 before the top 5 go to the model,
  locally? Ollama has no rerank endpoint, so the candidates are an ONNX cross-encoder run from .NET or the chat
  model as a reranker (likely too slow at ~45 tokens/s). Spike first: CPU time per question and replay rank
  gains; build only if both are acceptable. Targets the retrieval misses (T2, T6, T8, H10).
- **3. Calculator tool** - `Microsoft.Extensions.AI` function calling.
- **3b. Answer verification + observability** - before an answer is shown, code checks that every figure it
  states appears in the context it was given (or comes from the calculator); an unsupported figure is flagged
  or the answer becomes a decline. Deterministic and measurable with the grader - it targets figures inferred
  from absence (T9's "$0") and invented calculations (prompt v2's R2). With it, per-request tracing
  (`Microsoft.Extensions.AI`'s OpenTelemetry support: retrieved chunks, prompt tokens, latency) replaces reading
  verbose logs and Ollama's server log by hand, and Ollama's context window is set explicitly instead of
  relying on the 4,096 default the prompts now fill to 3,984.
- **4. Facts table + router** - only if steps 1-3 leave headline-figure failures that it would fix.
- **5. Answer skills selected by code** - the answer kinds above, minus "headline figure" if step 4 is skipped.
- **6. Model-selected skills** - only if step 5 leaves a gap.

**Step 1b, detailed 2026-09-28 (user; prompted by the user spotting `dei:EntityAddressAddressLine1` and the
many `contextRef`/`id` attributes in the HTML).** An inventory of all four filings: each tags 1,292-1,855
numbers and 176-340 text facts, against 382-545 contexts and 6-14 units declared in the hidden `<ix:header>` -
the official mapping, per filing (the IDs themselves are arbitrary: MSFT's are GUIDs). Both v1 strategies and
1a delete that header unread. Every filing also tags a full cover page: registrant name, address
(`EntityAddressAddressLine1`, city, state, ZIP), ticker and exchange, state of incorporation, fiscal year end and
focus, shares outstanding, public float, and the auditor (`AuditorName`, `AuditorLocation`, `AuditorFirmId`).
Three sub-steps, so an infrastructure change and two answer-changing ones are measured apart:
- **1b-i. The XBRL header map - changes no chunk.** Contexts (period: start/end or instant; dimensions:
  `explicitMember` and `typedMember`), units (incl. `divide` - USD per share), and per fact `name`, `scale`,
  `decimals`, `format` (`ixt:fixed-zero` for "-", `ixt-sec:numwordsen` for "three"), `xsi:nil` and **`sign="-"`**
  (106-144 per filing: the stored value is negative while the page shows no minus - miss it and outflows read
  positive), plus a resolver for **`ix:continuation` chains** (58-186 per filing: a text block that starts in
  one place and continues elsewhere - without it only a note's first piece gets its topic). Verified against the
  filings, no question run: every `contextRef`/`unitRef` resolves, every chain completes, values spot-checked
  against the chunk dumps (the `sign="-"` ones included), unit tests.
- **1b-ii. A filing-profile chunk from the cover facts - measured.** One short chunk per filing in plain
  sentences, cited as the cover page: registrant and ticker/exchange, "principal executive offices" (the
  filing's wording, not "headquarters"), state of incorporation, fiscal year end and the fiscal year covered,
  the auditor. Values cleaned (trailing commas, MSFT's capitals); checkbox facts (&#9746;/&#9744;) left out - their
  meaning depends on which box carries the tag (MSFT's `EntityShellCompany` sits on the ticked "No"); number
  facts scaled (MSFT's public float is "3.6" at trillions scale). Targets H11, Q12, Q13, Q22. Risk to watch in the
  replay: a chunk naming the company and fiscal year may also rank for ordinary figure questions and take one of
  the model's 5 slots - if main-set ranks drop, tighter wording or a filter.
- **1b-iii. Structure labels - measured.** Note/policy topic from the text block enclosing each chunk (through
  the continuation chains) and statement type from the filer's own taxonomy (below), with the fiscal calendar
  (`DocumentPeriodEndDate`, `DocumentFiscalYearFocus`) for period labels. The widest-reaching change.

**The filers' taxonomies - checked 2026-09-28, added to `data/` (user downloaded them from each filing's EDGAR
"Data Files").** Scripted EDGAR access is refused without a User-Agent carrying a contact email (SEC fair
access: "Undeclared Automated Tool"), so onboarding a filing means downloading its HTML and taxonomy by hand.
Each taxonomy holds the filing's official structure - the role list, and per role which concepts it presents:

| Filing | Layout | Roles: Document / Statement / Disclosure | Labels | Statement roles (primary + parenthetical) |
|---|---|---|---|---|
| MSFT | linkbases embedded in the `.xsd` (DFIN) | 1 / 6 / 109 | 1,922 | 5 + 1, 6-40 concepts each |
| NDAQ | `.xsd` + `_pre`/`_lab`/`_cal`/`_def.xml` | 2 / 6 / 107 | 2,224 | 5 + 1, 7-52 |
| NFLX | `.xsd` + four files | 2 / 7 / 79 | 1,734 | 5 + 2, 5-38 |
| ORCL | embedded | 1 / 7 / 89 | 2,521 | 5 + 2, 9-46 |

Role descriptions follow EDGAR's "sort code - type - title" convention on all four, so "Statement" and
"Disclosure" are read the same way for any filer. What that changes in 1b:
- **Statement type from the Statement roles**, replacing v1's title regexes: every filing declares exactly its
  five primary statements - NDAQ's "Consolidated Statements of Changes in Stockholders' Equity", which v1's
  patterns missed until the second review (0 `equity_statement` chunks), is simply a Statement role here.
  Parentheticals are excluded by title. The roles don't say *which* statement each is ("INCOME STATEMENTS",
  "Statements of Income", "STATEMENTS OF OPERATIONS"), so a small title rule maps each role to the five types,
  cross-checked against the concepts it presents (a role with `us-gaap:Assets` and `us-gaap:Liabilities` is the
  balance sheet) - on 5-7 clean titles per filing, not the page text, and a disagreement fails loudly. A table
  gets the role its tagged concepts overlap most (a concept like net income appears in several statements).
- **Note topics from the Disclosure roles** (79-109 per filing: "Revenue from Contracts with Customers",
  "Goodwill and Acquired Intangible Assets", ...), and **official labels** for company members ("Intelligent
  Cloud [Member]", "Capital Access Platforms") - no name-splitting, no FASB download.
- **1b-i reads the taxonomy too**, in either layout (the `.xsd`'s `linkbaseRef`s say which), with an XML parser:
  the two layouts differ in attribute order and titles carry entities ("Stockholders&amp;#8217; Equity"), which
  broke a regex check. The header parser follows the specifications the filings declare - Inline XBRL 1.1,
  XBRL 2.1, Dimensions 1.0 - not just the shapes these four use, and the Transformation Registry for `format`:
  MSFT and ORCL use `ixt` 2022-02-16, NDAQ and NFLX 2020-02-12, all four `ixt-sec` 2015-08-31. An unknown format
  code fails loudly instead of guessing. No classes generated from the XSDs (the header uses a handful of element
  types). NFLX's browser-saved HTML isn't XHTML (no `<?xml`), so the HTML side stays on AngleSharp.
- The twelve files (four schemas, NDAQ's and NFLX's four linkbases each) are committed like the filings (~9.9 MB next to ~27 MB of HTML), so a clone still runs offline;
  the app reads only `data/*.html`, so neither v1 strategy is affected.
**Step 1b-i done (2026-09-28).** `Xbrl/`: `InlineXbrlReader` (header contexts and units, every fact, from the
DOM), `IxTransformations` (the format codes), `TaxonomyReader` (roles, presentation, labels; both layouts;
finds a filing's taxonomy by the namespace its page declares - NFLX's saved copy has no schemaRef), and
`XbrlModel`. Not wired into chunking, so no chunk and no index changed. Verified on all four filings: every
contextRef and unitRef resolves; every continuation chain completes (43/104/38/33 facts continued); 106-144
negated facts per filing; five primary statements each; 434-585 of 449-590 fact concepts carry an official
label. Known figures come out exactly, stored in dollars from each fact's scale - MSFT total assets
758,376,000,000; NFLX total assets 55,596,993,000 (thousands); NDAQ's **total** comprehensive income is
`...IncludingPortionAttributableToNoncontrollingInterest` = 2,113M (2024: 940M) and "attributable to Nasdaq" is
`ComprehensiveIncomeNetOfTax` = 2,114M (942M) - the Q10/V1 mix-up is two concepts; ORCL's dividends declared
4,743M carry the period 2024-06-01..2025-05-31 - the fiscal year T9's roll-forward row doesn't show. Parentheses
are presentation: dividends shown "(4,743)" are stored positive; `sign="-"` marks values that are negative
(ORCL's accumulated deficit "4,309" -> -4,309M). Found building it:
- **Facts nest.** MSFT's `dei:DocumentPeriodEndDate` wraps the "June 30" and "2026" facts; its value is the
  whole text. A pattern-based inventory stopped at the inner closing tag and missed a format code
  (`ixt:date-monthname-day-en`) - the reader works from the DOM, and an unknown code throws.
- **The fail-loudly rule caught one:** NDAQ's "one- year" (hyphenated across a line break) as a worded duration.
- `xsi:nil="true"` facts (2-3 per filing) have no value, not zero.
52 tests (239): transformations per code, the reader's rules on fixtures (scale and sign, nested facts,
continuation chains, a missing continuation throws, nil), and the four real filings (counts, known figures,
cover facts, statements, labels).

**Cross-checked against EDGAR's extracted instance (2026-09-28, the user found the file).** EDGAR publishes each
filing's facts extracted from its inline XBRL as plain XML (`msft-20260630_htm.xml`, ~11 MB). Not a replacement
for the HTML as the RAG source: it holds only tagged facts (no Item 1/1A/7 prose, no untagged tables, no
headings) and loses where each fact sits, which 1b-iii's labels need; notes come as escaped HTML strings. But it
is the SEC's own reading of the same tags, so `FilingXbrlTests.Read_MatchesEdgarsExtractedInstance` compares the
reader against it fact by fact, both ways. MSFT: same 446 contexts, 6 units, 1,869 facts; **all 1,583 numbers
equal**; every text fact equal except the SEC's cover codes, where the reader keeps the displayed name by design
("Washington"/"Nasdaq" vs "WA"/"NASDAQ" - excluded by name). It found one real bug: `ixt-sec:duryear` "2.3" is
P2Y3M18D, not P2.3Y (a fractional year becomes months and days; fixed, tested). The files are an oracle only:
`.gitignore`d (~27 MB for four), the test skips a filing without one.
**All four checked (the user downloaded the other three):** every fact now matches EDGAR on all four filings,
NFLX's browser-saved copy included. Two more rules came out of it, both fixed:
- **Fractional-year durations:** the day count is the fraction of a month times an average month (365.25 / 12
  = 30.4375 days), truncated - fitted to all seven fractional durations in the four filings (MSFT 2.3, NFLX 1.53,
  ORCL 7.58, NDAQ 2.1 / 1.5 / 3.2 / 8.4). A 30-day month got ORCL's 7.58 wrong (28D, not 29D); rounding got
  NFLX's 1.53 wrong (11D, not 10D) - MSFT's single case fitted all three rules.
- **Continued facts are the pieces concatenated with nothing added** (Inline XBRL 1.1), keeping each piece's
  own whitespace and collapsing it only after joining: ORCL's CODM description reads "assessed.We have" across
  a piece boundary (a space had been inserted), and a worded duration split as "five" + " years" must stay
  "five years" (trimming each piece first gave "fiveyears", which the fail-loudly rule caught).
The `Structured` chunks are unchanged by both (only the profile reads facts), so 1b-ii's measurement stands.

**Step 1b-ii measured (2026-09-28, `eval/structured-1b-ii/`, against `structured-1c-early`):** one "Cover Page"
chunk per filing (~100 tokens) from the cover facts - name, fiscal year, common stock symbol and exchange (paired
by context: ORCL lists "ORCL PRD" first, NDAQ four note issues), principal executive offices, state of
incorporation, auditor. Every other chunk byte-identical (948 chunks).

| | 1c-early | 1b-ii |
|---|---|---|
| Main Q1-Q24 / targeted / routing / variants, reliable | 22/24, 5/10, 3/3, 1/3 | same |
| Held-out, reliable | 10/15 | **11/15** (H11 fixed) |
| Replay main: recall@5 / MRR | 22/22, 0.784 | 22/22, **0.814** |
| Replay held-out: recall@5 / MRR | 8/13, 0.427 | **9/13, 0.495** (H11 rank 17 -> 1) |

37/40 main and 14/15 held-out answers are word for word the same; Q12, Q13 and Q22 stay right and now cite the
Cover Page; H11 answers "151 W. 42nd Street, New York, New York 10036". The risk named in the plan didn't
materialise: the profile reached the top 5 for 6 of 55 questions - the four it targets (Q12, Q13, Q22, H11)
and two employee-count questions (Q11, H12, rank 2 - both still right). No figure question: statement-filtered
questions can't retrieve it (it's narrative), and none of the unfiltered ones ranked it. H10 dropped one rank
(18 -> 19), outside the top 5 either way. Largest prompt 2,950 tokens, no truncation.

Kept out of 1b: **registration from `EntityRegistrantName` + `TradingSymbol`** replacing the hand-written
`CompanyToFiling` entry - it changes routing (`QueryIntentResolver`), not chunks, so it's its own small step
after 1b-ii, verified by the resolver returning the same filings for every existing question; and the **facts
table** - cheaper once the header map exists, but still behind step 4's go/no-go. Not needed from the files:
fact `id`s (only link targets for continuations and footnotes), `ix:footnote`/`ix:relationship` (0-23 per
filing, text visible on the page), the company taxonomy behind `link:schemaRef` (a separate file, not in the
HTML - member names are readable split, "Intelligent Cloud"), official us-gaap labels (a separate FASB download),
`xml:lang`/`order`, and the HTML `id`s and `href="#..."` anchors (filer conventions the project doesn't rely on).

**Block model (2026-09-29, before 1b-iii; the interface decision plan-review point 4 left to 1a, never recorded
there).** Starting 1b-iii, the labels turned out to be known exactly on the DOM (this table is the balance sheet,
this element sits in the Income Taxes note) but the Structured pipeline threw that away: page -> one string ->
`SectionSplitter` (patterns over lines) -> `TokenChunker` (blocks re-parsed from text) -> `FilingChunk` of four
strings. The linearizer already built rows with each value's XBRL context and concept; `ToRowBlock` flattened them
into a `<pre>` that was written into the text and parsed back by `RowBlock.TryParse`. Every label would have
needed another marker line (`#statement`, `#topic`) and another parser, and 1c-1d and step 4 need more per chunk
still (one table per chunk, two texts, facts). `EmbeddingTextBuilder`'s row-label summary was already a no-op for
Structured and Linearized - it looks for lines starting with `|`. **Decision (user): v2 ingestion gets its own
objects**; retrieval, the index, the grader and replay stay shared - that is what makes v2 comparable to v1.
- `Structured/`: `FilingBlockReader` (the page -> `TextBlock` / `TableBlock` in reading order; replaces
  `HtmlTextConverter` - a table is a block holding its `RowBlock`, its `LinearizedTable` and its element, never
  text), `StructuredSections` (blocks -> sections), `StructuredChunker` (sections -> `StructuredChunk`s, each
  listing the blocks it was built from), `StructuredFiling` (plus the XBRL).
- The rules stay v1's, shared rather than copied: `SectionSplitter.HeadingTracker` (the heading rules one line at a
  time) and `TokenChunker.Pack` (the packing rules on typed blocks, reporting block indexes; `Chunk` parses text
  and calls it). Structured has no pipe tables since 1c's first change, so the Markdown-table half of the chunker
  is v1's only.
- **Verified by reproduction, no question run:** all 948 Structured chunks regenerate byte-identical to the
  committed `chunk-review/structured/`, and Markdown (1,444) and Linearized (999) are unchanged by the shared
  refactor. The comparison caught one porting bug: the step-1a regex `[^\S ]` held a literal U+00A0 (it reads as a
  space) and lost it in the copy, so runs of ordinary spaces survived (ORCL's cover page, "Act.    Yes  ☒") and
  five extra chunks appeared; now written `[^\S ]`. A `<pre>` is a plain paragraph now (no filing has one),
  a table with text that yields no rows throws, and a pipe table can no longer be produced.
266 tests (251): the converter tests moved to the reader; new ones for sections, block membership, `Pack` and a
real-filing check that every chunk knows its blocks and every table is chunked. Next: 1b-iii's labels as fields.

**1b-iii split into three measured sub-steps (user, 2026-09-29):** a) statement type, b) note topics, c) period
labels - each changes answers a different way (the routing filter, the heading/embedding text, row content), so
each gets its own run.

**Step 1b-iii-a measured (2026-09-29, `eval/structured-1b-iii-a/`): statement type from the Statement roles.**
`Structured/StatementLabels`: a title rule names each primary Statement role's type, confirmed by a concept only
that statement presents - on all four taxonomies exactly one primary role presents each: `Assets` +
`LiabilitiesAndStockholdersEquity` (balance sheet), `NetCashProvidedByUsedInOperatingActivities`,
`EarningsPerShareBasic`, `ComprehensiveIncomeNetOfTax`, `StatementEquityComponentsAxis` (equity). A missing,
doubled or unconfirmed type fails loudly. Each role goes to the one table covering most of its presented concepts:
the 20 statements are one table each at 50-86%; no fixed threshold works (NFLX's segment table covers 53% of the
operations role, above the equity statements' 50%), but it loses the role to the statement (79%); below 40% fails
loudly. A chunk's type comes from the statement table it holds (a piece, with caption or footer), otherwise
narrative - no carry-forward from the last title. `FilingChunk.StatementType` (optional) carries it;
`FilingChunkRecords` keeps v1's detection for strategies that don't set it. Title-based section splitting stays
(it sets boundaries, not labels; 1c's one table per chunk supersedes it).
- **Tags: 947 of 948 chunks the same as v1's title patterns.** The one change is right: NFLX's
  "See accompanying notes..." page footer, followed by the next page's "NETFLIX, INC." header - no figures -
  moved from `comprehensive_income` to narrative. Chunk text unchanged.
- **Run: nothing moved.** Main 22/24, targeted 5/10, routing 3/3, variants 1/3, held-out 11/15; 39 of 40 main
  and 15 of 15 held-out answers word for word the same (Q21, the NFLX comprehensive income question - the one the
  relabelled chunk could reach - moved a full stop). Replay unchanged: main 22/22, MRR 0.814; held-out 9/13, MRR
  0.495.
So on these four filings the patterns were already right after two reviews' fixes; what 1b-iii-a buys is the
source - a new filer's statements are found from its declared roles, the NDAQ-equity kind of miss (0 chunks until
the second review) can't recur silently, and a disagreement fails at build time. It doesn't touch the routing
misses (R1, H14: the question's keywords pick the wrong statement) - that's the query side, step 2's soft labels.
278 tests.

**Step 1b-iii-b measured (2026-09-29, `eval/structured-1b-iii-b/`): note topics.** `Structured/NoteTopics` finds
each note to the financial statements from the filer's tags: a text block whose concept a Disclosure role presents
(roles for a note's parts - "(Tables)", "(Policies)", "(Details)" - excluded; the SEC's form taxonomies dei/cyd/ecd
excluded - cover, Item 1C, Item 9B), outermost only. The role title is the topic, all-capital titles (MSFT, ORCL) in
title case. A note spans its first piece to its last continuation, page breaks included. The reader gives every
block inside a note its topic; a note is a section of its own, its topic the heading's last part ("PART II > Item 8.
... > Income Taxes") - in the embedding text and the citations. Found on the way, each now a rule and a test:
- **NDAQ tags two notes as one continued fact:** "Revenue from Contracts with Customers" (Note 3) and "Deferred
  Revenue" (Note 8) are one `RevenueFromContractWithCustomerTextBlock` whose continuation chain jumps over Notes 4-7.
  A gap between pieces that holds another note's start splits the fact into runs, which take its roles in sort-code
  order (9952159, 9952164); any other count fails loudly. Notes found: MSFT 18, NDAQ 20, NFLX 14, ORCL 15.
- **NDAQ and NFLX tag a note from its title, its number left outside** ("2." + "SUMMARY OF SIGNIFICANT
  ACCOUNTING"): the heading paragraph began outside the note and split off - 32 sections of a number and a heading.
  The other gaps between notes were page furniture (a Table of Contents link, a lone non-breaking space), so
  everything between two notes now belongs to the next one.
- **The Notes' own title** ("NOTES TO CONSOLIDATED FINANCIAL STATEMENTS", plus Note 1's number or a date) became an
  8-20 token title-only chunk in every filing; it now opens Note 1 (paragraphs only between them).
948 -> 972 chunks: chunks no longer straddle two notes. Four more under 50 tokens (31 -> 35), each a note's real
last sentence (MSFT: "The dividend declared on June 10, 2026 was included in other current liabilities...") that
used to ride on the next note's start. Statement tags unchanged (the tests hold the five per filing).

| | 1b-iii-a | 1b-iii-b |
|---|---|---|
| Main Q1-Q24, reliable | 22/24 | 22/24 |
| Targeted T1-T10, reliable | 5/10 | **7/10** |
| Routing / variants | 3/3, 1/3 | same |
| Held-out, reliable | 11/15 | 11/15 |
| Replay targeted: recall@5 / MRR | 7/10, 0.455 | **8/10, 0.483** |
| Replay main / held-out: MRR | 0.814 / 0.495 | 0.814 / 0.497 |

34 of 40 main and 11 of 15 held-out answers word for word the same; most of the rest cite the more specific
heading ("... > Stockholders' Equity", "... > Employee Stock and Savings Plans"). Read per question, from the verbose
logs:
- **T6 (NDAQ Financial Technology goodwill, $7,952M) - fixed by retrieval, and by this step.** Before, the top 5
  held no goodwill table: rank 1 was an anonymous "...business segment during the year ended December 31, 2025"
  piece headed "PART IV > Financial Statements", and the note's own opening sat at the tail of the previous note's
  chunk. Now rank 1 is the chunk that opens "5. GOODWILL AND ACQUIRED INTANGIBLE ASSETS", headed "> Goodwill and
  Acquired Intangible Assets". This is the replay's +1.
- **T3 (NFLX technology and development, 16%) - fixed, but not by retrieval.** The MD&A table with the answer was in
  the context both times; an unrelated NFLX acquisition note chunk left the top 5 and the model answered instead of
  "The unit is not stated.". Counted, but it's a context-mix change, not the label's doing.
- **T5 (MSFT U.S. government securities) - wrong both times**, the investments table outside the top 5 in both; the
  decline became "The unit is not stated.". H10 likewise (declined both times).
- The held-out targets aren't reached: H6 (still $2,805M, now cited as "> Restructuring and Other Expenses"), H10
  (rank 15), H13 (16), H14 (>25, the routing miss).
290 tests.

**Step 1b-iii-c measured (2026-09-29, `eval/structured-1b-iii-c/`): period labels on roll-forward rows.**
`Structured/PeriodLabels`: a row whose tagged values all share one XBRL period, whose label and column names don't
show that period's year, in a table whose rows span more than one period, gets the period on its label - "Cash
dividends declared ($1.70 per share) (year ended May 31, 2025) — Accumulated Deficit: (4,743)". Previewed before
building: that last condition limits it to the roll-forwards (equity statements, award activity, goodwill and other
comprehensive income) - 123 rows in 16 tables; without it 145 more rows of single-period tables would repeat what
their table says. Wording from the period's length: "year ended ...", "three months ended ...", "as of ..." for an
instant, "on ..." for a one-day period (NDAQ's acquisition date). 972 -> 975 chunks (longer rows moved three splits).
**Result: main 22/24, targeted 7/10 - T9 still wrong.** Held-out 11/15, all 15 answers word for word the same; 39
of 40 main answers the same (T9). Replay: main unchanged (22/22, MRR 0.814); targeted recall@3 6 -> 7, MRR 0.483 ->
0.508 - the labelled rows rank higher; held-out MRR 0.497 -> 0.498.
- **T9 was not a retrieval or ingestion miss any more.** The question is filtered to ORCL's equity statement, all
  of whose chunks reach the model, and chunk 150 holds the labelled row. The model answered "$0" (1b-iii-b: $5,725,
  fiscal 2026's row). Read as a wording mismatch it doesn't bridge: the question says "common stock dividends" and
  "fiscal 2025"; the right row says "Cash dividends declared" and "year ended May 31, 2025"; fiscal 2026's row is
  literally "Common stock dividends ($2.00 per share)". The lookalike-line limit of review point 5.
- **Decision (user, 2026-09-29): close 1b-iii here, no T9-specific follow-up now.** Considered: "(fiscal 2025, year
  ended May 31, 2025)", the fiscal-year name from the filer's own `DocumentFiscalYearFocus` + `DocumentPeriodEndDate`
  (the plan's "fiscal calendar") - it closes the year gap, not the wording gap, and T9 is a tuned-against main-set
  question with no held-out counterpart, so a fix would be weak evidence. **First item for 1c**, where row text is
  redesigned anyway. Nothing later makes it redundant: hybrid search and reranking act before the model reads (T9's
  row is already there); 3b's answer verification would catch the "$0" (not produce $4,743); a facts table (step 4)
  would need the same question-to-fiscal-year mapping.
298 tests.

**Registration from the cover facts (2026-09-29).** v1's hand-written `QueryIntentResolver.CompanyToFiling` - the one
manual step of onboarding, whose omission ran every NFLX question unfiltered and produced a hallucinated figure - is
replaced by `Retrieval/CompanyRegistry`: each filing registers from its tagged cover facts, the registrant name
without its legal form ("MICROSOFT CORPORATION" -> "Microsoft", "Nasdaq, Inc." -> "Nasdaq") and the common stock's
`TradingSymbol` - only the common stock's: NDAQ also tags four note issues ("NDAQ29"...) and ORCL its preferred
("ORCL PRD"), the same pairing-by-context the filing profile already used (now shared, `Xbrl/CoverFacts`; profile
chunks byte-identical). All strategies use it, since routing is shared. A filing without a registrant name stops
startup; startup prints each registration. Cost: each filing is parsed at startup, ~1.1 s for the four.
- **Names match as whole words**, not substrings: v1's `Contains` would route "metadata" to a future "Meta".
- **Verified as the step was defined, no question run:** the registry built from the four filings equals v1's table
  exactly (8 names), and every one of the 55 questions resolves to the same filings under both - a test, with v1's
  table kept in it as the reference. Answers can only change through routing, so none can have moved.
- Not covered: a company questions name by a brand unlike its legal name (none of the four); the onboarding checker
  now WARNs on it, and an alias would be a rule in `CompanyRegistry`.
- **Every class of common equity, then (user's question the same day).** The first version took the first
  "common stock" title only, so a filer with several classes (Alphabet: GOOGL, GOOG) would register one ticker.
  `CoverFacts.CommonStocks` now returns every class whose title names common equity - "common stock", "common
  shares", "ordinary shares" (an ADS of one) - in cover order, never notes or preferred; the registry adds all their
  tickers and the profile lists each with its title when there are several. The fixture test for it found a bug:
  the cleaning that title-cases all-capital values ("MICROSOFT CORPORATION") also reached the ticker, so "GOOGL"
  became "Googl" - the four tickers here have at most four letters. Tickers are now only trimmed. The four filings
  register and profile exactly as before (dumps unchanged).
308 tests.

**Step 1c, planned 2026-09-29 (user: "go ahead with 1c").** Measured on the current output first. The plan's three
items: (a) the fiscal-year name on period labels, deferred here from 1b-iii-c; (b) one table per chunk - T4 still
failed ("The unit is not stated."), and 52 of 975 chunks held 2+ tables, 33 of them 2+ tables of figures (ORCL's
supplemental lease cash flows with its maturities - T4's own case); (c) the compact display text, which was mostly
the Markdown strategy's empty-cell padding (30-63% of statement tokens) - Structured's linearized rows never had it.
What's left of (c), long column names repeated on each value, is measured before it's a step. So 1c is two measured
sub-steps, like 1b-iii.

**Step 1c-a measured (2026-09-29, `eval/structured-1c-a/`): fiscal-year names on period labels.** `Xbrl/FiscalCalendar`
reads the filer's calendar from its cover tags - fiscal year `DocumentFiscalYearFocus` ends `DocumentPeriodEndDate`,
earlier years a year apart (within a week, for 52/53-week years; named from the focus, not the end date's year, so
a January year-end is named as its filer does). A year-long period label then reads "(fiscal 2025, year ended May
31, 2025)" - only where the fiscal year isn't the calendar year, since "2025" already names NDAQ's and NFLX's.
50 rows (ORCL 47, MSFT 3), chunk count unchanged (975).
- **T9 moved to the right row, still wrong:** "$0" -> "Oracle declared **$1.70 per share** in common stock dividends
  in fiscal 2025". The year gap is closed - the answer comes from fiscal 2025's "Cash dividends declared ($1.70 per
  share)" row - but the model reported the per-share figure from the row's label instead of its $4,743M total.
- Everything else identical: 39 of 40 main and 15 of 15 held-out answers the same; main 22/24, targeted 7/10,
  held-out 11/15. Replay: main and held-out unchanged; targeted recall@3/@5 unchanged, MRR 0.508 -> 0.492.
317 tests.

**Step 1c-b measured and declined (2026-09-29, `eval/structured-1c-b-declined/`): one table of figures per
chunk.** Built as a flag on the packing input (`ChunkerBlock.IsDataTable`, set on the Structured strategy's
financial tables): a data table never joined a chunk already holding one; the chunk closed before it and the short
text right before it (its "... were as follows:" lead-in, up to the 100-token caption budget) moved with it. Layout
tables were left out after the first draft - applied to every table, the rule split cover-page boxes and signature
blocks into fragments (7 new chunks under 50 tokens, 6 of them layout). Result: 975 -> 1,012 chunks, none holding
two data tables; Markdown and Linearized unchanged.
| | 1c-a | 1c-b |
|---|---|---|
| Main / targeted / routing / variants, reliable | 22/24, 7/10, 3/3, 1/3 | same |
| Held-out, reliable | 11/15 | 11/15 |
| Replay targeted: recall@1 / @5 / MRR | 3/10, 8/10, 0.492 | 2/10, 8/10, **0.425** |
| Replay held-out: recall@1 / @5 / MRR | 5/13, 9/13, 0.498 | 3/13, 9/13, **0.408** |
- **T4 closer, not fixed.** The maturities table now opens its own chunk, lead-in first, and rose from outside the
  top 8 to rank 6 - one short of the 5 the model reads. Above it: five chunks of ORCL's restructuring tables, which
  share "Oracle ... fiscal 20xx" with the question and never mention leases. A ranking miss now, not a chunking one:
  step 2's keyword score on "operating lease" is the direct fix.
- **H6: wrong -> declined** - the $2,805M lookalike became "not stated". The one better answer.
- **The cost: a table alone embeds as weaker evidence than a table with its commentary.** T2: the repurchase table
  (it had shared a chunk with the dividends table) fell from rank 1 to 3 behind MD&A prose ("During fiscal years
  2026 and 2025, we repurchased..."), and the answer took the prose's period - "year ended June 30, 2025" instead of
  the second quarter of fiscal 2025. Same figure, so the grader, which doesn't check periods, still passes it. H1, H4
  (MSFT R&D): an MD&A chunk of four small tables with commentary split; the R&D table went to rank 2-3 behind R&D
  prose, answers unchanged.
- **Decision (user, 2026-09-29): not kept** - no score gained, one answer's period worse, MRR down 14-18%; the gains
  (H6's decline, T4 closer) don't pay for it. Code reverted to 1c-a, run kept here as the record; the Structured index
  rebuilt to 1c-a. The v1 "T4 lesson" (a mixed chunk embeds as its first table) is real, but splitting tables from
  their prose costs more on these questions. T4 goes to step 2.
**1c closed**: a) kept, b) declined; c), the compact display text, was the Markdown strategy's padding - Structured's
rows never had it. What remains of it (long column names repeated on every value, ORCL's equity statement) is
display length, not a measured failure; left for 1d or answer skills if the prompt budget needs it.

**nomic-embed-text's context, checked 2026-09-29 (the user found a report of Ollama cutting embedding input at 2,048
tokens).** Ollama's GGUF declares `context_length: 2048` (the model card's 8,192 is rope scaling), and `/api/embed`
truncates by default instead of failing. Measured with `truncate: false` and Ollama's own count on the largest chunks
of each strategy: Markdown 778 tokens, Linearized 636, Structured 579 - nomic's tokenizer counts 0.85-1.08x of
cl100k's here. Nothing was ever cut; now a validated fact in the plan, with when to re-check.

**Step 1d - reviewed, measured by replay first, then built (2026-09-29, `eval/structured-1d/`).**
*The review (user: "let's review if this will help").* The plan's 1d - company and section context on each chunk's
embedding text. I predicted no effect: 54 of 55 questions name their company, so every search is filtered to one
filing, and one line added to every candidate should shift them all alike; the section path was already in the
embedding text (the heading, note topic included, since 1b-iii-b). The remaining misses pointed elsewhere instead:
table chunks losing to prose for questions naming their row label (H13 "cash and cash equivalents": the balance
sheet at rank 15 behind an acquisitions note). And `EmbeddingTextBuilder`'s row-label summary - v1's fix for exactly
that - only recognises Markdown pipe tables: for Structured and Linearized it had never fired.
*The experiment.* The replay needs only the index's `chunks` and `vec_chunks`, so a scratch script rebuilt the vectors
with other embedding texts (same chunks, same filters, embedded one at a time as the app does; its baseline vectors
matched the stored ones at cosine 1.000000):
| | today | `rows` | `labels` | `rows_co` | **`co`** |
|---|---|---|---|---|---|
| Main: recall@5 / MRR | 22/22, 0.814 | 22/22, 0.792 | 22/22, 0.792 | 22/22, 0.856 | 22/22, **0.856** |
| Targeted: recall@5 / MRR | 8/10, 0.492 | 7/10, 0.480 | 6/10, 0.509 | **10/10**, 0.703 | 9/10, **0.762** |
| Held-out: recall@5 / MRR | 9/13, 0.498 | 9/13, 0.584 | 9/13, 0.490 | 11/13, **0.729** | 11/13, 0.715 |
`rows` = + "Financial data table with rows: a, b, c." (v1's phrase); `labels` = the row lines reduced to their labels,
figures out; `co` = + one line, "Oracle Corporation (ORCL), Form 10-K for fiscal year 2026."; `rows_co` = both.
- **The row-label ideas failed**: `rows` pushed T10 out of the top 5, `labels` pushed T8 out (rank 2 -> 20), and
  neither brought a miss in - H13's chunk already contains "Cash and cash equivalents" verbatim. The figures weren't
  diluting anything; some questions match on them.
- **The company line did the work, and my prediction was wrong.** Embeddings aren't additive: a strong
  natural-language header pulls a number-heavy table chunk much closer to a question like "Oracle's ... fiscal 2026"
  than it moves prose that was already close - so tables stop losing to prose. T4 20 -> 1, H6 19 -> 3, H13 15 -> 2,
  T5 >25 -> 8, H10 15 -> 8; the only loss Q11 1 -> 2. Strongest on the held-out set, never tuned against. The known
  "contextual chunk header" effect - what the plan's source guidance recommended; the plan was right.
- **`co` over `rows_co` (user's choice, 2026-09-29):** tied within a rank on nearly everything; `rows_co` keeps T5 at
  exactly rank 5, but its labels misbehaved alone, and T5 ("U.S. government securities", a verbatim phrase) is a
  keyword case step 2's hybrid search should take anyway.
*Built.* `CoverFacts.EmbeddingContext` makes the line from the cover facts - registrant name, common-stock ticker(s),
`DocumentType`, `DocumentFiscalYearFocus` - a test holds it to the four lines the experiment typed by hand;
`FilingChunk.EmbeddingContext` carries it; `FilingChunkRecords` puts it after "search_document: ", embedding text only
(Content unchanged; v1's strategies set nothing). The rebuilt index's 975 vectors equal the experiment's `co` index
(lowest cosine 1.000000), and the replay reproduces it exactly.
*The question run - retrieval gains reach answers, and prompt v1's leak muddies the totals:*
| | 1c-a | 1d |
|---|---|---|
| Main / targeted / routing / variants, reliable | 22/24, 7/10, 3/3, 1/3 | 22/24 (Q15 resolved), 7/10, 3/3, 1/3 |
| Held-out, reliable | 11/15 | 10/15 |
- **Fixed, by retrieval as predicted:** T4 - "$3,603 million", right for the first time on any strategy; H6 -
  "$10,272 million", right for the first time (was the $2,805M lookalike).
- **Q15 (a negative): a correct decline the grader misses** - "... not present in the provided excerpts, I do not have
  the answer"; `DECLINE` lacks "do not have the answer". Resolved decline-ok by the user.
- **Lost, answer side - retrieval unchanged or better:** T6 declines with its chunk still at rank 1 (context mix, like
  T3 before); H8 and H15, both negatives, became prompt v1's "The unit is not stated." instead of clean declines; H13
  reaches the model now (rank 2) but it takes a lookalike line ($5,208,710 thousand from the cash note); H10 declines
  while stating unrelated figures - resolved declined by the user, as before.
- **Decision (user): keep 1d.** Its retrieval gain is real and converts where the model reads the right chunk; the
  losses are prompt v1's units rule leaking into declines and model misreads - answer skills (step 5) and answer
  verification (3b). **Raised for the plan (user noticed the prompt was meant to change):** answer skills was placed
  late - after ingestion and after the compact display text; ingestion is done and the display text turned out moot
  (1c), while the malformed decline keeps muddying every step's measurement. Whether to move it (or its decline
  section) ahead of step 2 is the user's call, to discuss next.
323 tests.

**Decision (user, 2026-09-29): keep the plan's order** - step 2 (hybrid search) next, answer skills stays step 5. Each
retrieval step goes on being measured against prompt v1, its decline leak read per question (as in 1d) rather than
fixed first.

**Step 2 - hybrid search: measured by replay first, then built (2026-09-30, `eval/structured-2/`).**
*The spike.* A scratch replay (no LLM, no change to the index: FTS5 in an in-memory copy of `chunks`) ranked every
question six ways from the 1d logs' filters. Keyword side: FTS5 over heading + content, `porter unicode61`, `bm25()`,
the question's content words OR-ed (stop words, four-digit years and company names dropped). Fusion: reciprocal rank
fusion, k = 60. Its `hard` column reproduced the recorded 1d replay exactly. recall@5 / MRR:
| | hard (1d) | vec | hyb | **hyb+soft1** | hyb+soft | hybP+soft |
|---|---|---|---|---|---|---|
| Main Q1-Q24 | 22/22, 0.86 | 19/22, 0.67 | 21/22, 0.77 | **22/22, 0.89** | 22/22, 0.89 | 22/22, 0.89 |
| Targeted T1-T10 | 9/10, 0.76 | 9/10, 0.76 | 10/10, 0.75 | **10/10, 0.75** | 10/10, 0.75 | 10/10, 0.78 |
| Routing R1-R3 | 0/3, 0.00 | 2/3, 0.50 | 2/3, 0.53 | **2/3, 0.28** | 2/3, 0.20 | 2/3, 0.20 |
| Held-out H1-H15 | 11/13, 0.71 | 12/13, 0.79 | 13/13, 0.90 | **13/13, 0.90** | 13/13, 0.89 | 13/13, 0.81 |
`vec` = vectors, company filter only; `hyb` = vectors + keywords; `soft1` = plus a third list, the vector ranking
within the resolved statement type (the label as a boost); `soft` = plus that list's keyword twin too; `P` = plus
two-word phrases in the keyword query.
- **The statement boost is what keeps statement questions intact:** without it Q23 fell 1 -> 11, Q2 1 -> 5, Q4 2 -> 4.
- **Phrases not kept:** T5 4 -> 2, but H6 and H13 1 -> 2.
- **One list, not two:** `soft1` beat or tied `soft` everywhere (R1 2 vs 3, R2 4 vs 5, H14 2 vs 3).
- **Candidate depth 50:** cutting each list to 25/50/100 before fusion - 50 matched the full rankings on every group and
  was slightly better on R1/R2 than 100; 25 dropped R3 to 20. A filing has 199-294 chunks.
- The held-out set was used to choose among variants, so it isn't a clean held-out check of this step - though every
  hybrid variant scored 13/13 there; the choice was settled by the main and routing sets.
*R3 checked before deciding (user).* "What was Microsoft's Intelligent Cloud segment revenue?" - the answer ($137,791M)
is in two row-line tables (segment note, key 176; MD&A, key 73). bm25 ranks key 176 2nd of 210 MSFT chunks; the vector
search ranks it 34th, behind prose about Intelligent Cloud (the segment note's text is 1st, then Item 1 and MD&A) - a
number-heavy table opening with another segment's rows. RRF rewards agreement, so a chunk strong in one list and weak
in the other lands mid-list (14th fused). The statement boost costs it only one place (MSFT has two income-statement
chunks); plain hybrid ranks it 12th. Decision (user): build `hyb+soft1`; R3 is step 2b's first target - reranking reads
question and chunk together - rather than a keyword weight tuned to one question.
*Built.* `Retrieval:Search` (`Vector` | `Hybrid`) and `Retrieval:HybridCandidates` (50) in appsettings.json; the shipped
default stays `Vector`, matching the shipped `Markdown` strategy, so v1 is unchanged - the eval build sets `Hybrid`.
- `KeywordIndex` (VectorStore/): an FTS5 external-content table `chunks_fts` over SqliteVec's `chunks` table (text not
  stored twice), created at startup if missing - no rebuild needed for an existing index, and a `--rebuild` deletes both.
  Plain `Microsoft.Data.Sqlite` (already a transitive dependency, now referenced at the same 10.0.9; no vulnerable packages).
- `KeywordQuery` (Retrieval/): the spike's query; company names come from `CompanyRegistry`.
- `RankFusion` (Retrieval/): RRF, k = 60, ties in first-seen order (as Python's stable sort, so the replay matches).
- `RagAnswerService`: per filing, the vector search (company filter only), the keyword search, and - when a statement type
  resolved - the vector search within it, each `HybridCandidates` deep, fused and cut to top-K; multi-company questions
  interleave per filing as before. `Vector` mode is the old code path, untouched.
- Output: the filter line says "boosting statement type: ..." under hybrid; `--verbose` adds "(keywords: ...)" - which
  `tools/replay_recall.py` reads to replay a hybrid run (its presence marks one), the query not re-derived.
*A side effect found in the run (user asked):* the hard filter had silently capped the model's context. `GenerationTopK`
is 5, but a filtered search can only return the statement's own chunks - 1 to 5 per statement per filing (every
comprehensive-income statement is one chunk). In 1d, 26 of 40 main questions reached the model with fewer than 5 chunks
(7 with one). Hybrid sends 5 to every question. Nothing chose the small contexts; they helped T1 and hid nothing else.
*Prompt sizes checked (user asked), from Ollama's server log:* chat prompts 2,041-2,946 tokens (median 2,562, was ~1,930),
largest + 768 output = 3,714 of 4,096; `truncated = 0` on every request, chat and embedding. The embedding window
(2,048, checked 2026-09-29) is untouched: the index wasn't re-embedded, FTS5 doesn't embed, query embeddings are <= 33
tokens.
*The question run* (`eval/structured-2/`; the replay reproduces the app's top score on all 55 questions):
| | 1d | 2 |
|---|---|---|
| Main / targeted / routing / variants, reliable | 22/24, 7/10, 3/3, 1/3 | 22/24, 7/10, 2/3, 2/3 |
| Held-out, reliable | 10/15 | **14/15** |
| Replay recall@5, MRR: main / targeted / held-out | 22/22 0.856, 9/10 0.762, 11/13 0.715 | 22/22 0.886, 10/10 0.750, 13/13 0.904 |
- **Fixed:** R1 ($9,916M) and R2 ($1,274M) - answered for the first time, the answer no longer filtered out; H14 ($1,776M,
  the same routing miss); T5 ($48,562M, the keyword side); H10 (9,525 employees, was unrelated figures); Q10 and V1 - the
  lookalike line right for the first time (2,113 and 940, not 2,114 and 942), cited from the comprehensive income
  statement; H8 and H15 decline cleanly instead of prompt v1's "The unit is not stated.".
- **Lost:** T1 - "$104,075 million", the same statement's *Comprehensive income* line, not *Other comprehensive income*
  ($2,243M); the statement is still rank 1, but the model now reads it among five chunks (two accumulated-OCI note tables)
  instead of alone. Q21 dropped "thousand" (correct, not reliable).
- **Routing's 3/3 -> 2/3 is the grader's count:** it scored 1d's three declines as reliable. In substance 0 answered -> 2
  right; R3 moved from a decline to a wrong figure (MD&A's "$31.5 billion or 30%" increase; its table at rank 12).
- **Unchanged misses:** T6 (declined -> a wrong reading, neither reliable), H13 (the cash note's lookalike line, as in 1d).
*Decision (user): keep step 2.* It holds the strict 22/24, answers the routing misses it was built for, and takes the
held-out set from 10/15 to 14/15. What it leaves is answer-side (T1, Q21, H13 - the model misreading lines it has) or
ranking (R3) - 2b and step 5. Switching the shipped defaults to Structured + Hybrid is for the end of v2. 350 tests.

**Step 2b - reranking: researched, then deferred (user, 2026-09-30).** Checked before a spike, including Microsoft Learn:
- *Microsoft's guidance* (Azure Architecture Center, "Information retrieval", "Use reranking") describes this pipeline:
  retrieve broadly (~50), merge by RRF, rerank the merged set with a model, keep the top N. It recommends reranking
  when "you ran hybrid or multiple searches" or "retrieved a large candidate set" - both true since step 2. Options:
  a cross-encoder (names `ms-marco-MiniLM-L6-v2`, faster, and `-L12-v2`, more accurate; fine-tune for specialised
  vocabulary), a language model (flexible, dearer - for when a cross-encoder isn't accurate enough), Azure's semantic
  ranker or Cohere Rerank (cloud - out for a zero-cost local tool). Rerank 20-50 candidates; cross-encoder scores
  order, they don't threshold; benchmark relevance and latency before adopting.
- *Locally:* Ollama 0.34.4 still has no rerank endpoint (`/api/rerank`, `/v1/rerank`, `/api/v1/rerank` all 404). The
  .NET path would be ONNX Runtime (`Microsoft.ML.OnnxRuntime`, not referenced yet) + `BertTokenizer` (WordPiece - in the
  stable `Microsoft.ML.Tokenizers` 2.0.0 already referenced) + an ONNX export of the cross-encoder. A Python replay
  spike needs only `tokenizers` and the model files (`onnxruntime` 1.20.1 is already installed, via markitdown).
  `llama3.1:8b` as the reranker: ~25 chunks x ~500 tokens at ~45 tokens/s = ~4-5 minutes per question - ruled out.
- *Risks a spike would measure:* MiniLM reads at most 512 WordPiece tokens and number-heavy chunks tokenize long
  (R3's row sits mid-chunk); MS MARCO is web passages, not financial tables (`bge-reranker-base` as a second candidate).
- *Planned spike, if resumed:* rerank each question's top 25 hybrid candidates (structured-2 logs) with MiniLM-L6 and
  L12; recall@5/MRR per group, time per question, truncated chunks; targets R3, T1/H13, T6/T8.
*Decision (user): defer 2b, go to step 3.* Not a dependency of any later step. What it leaves: R3 wrong (rank 12);
lookalike ranks as they are (T1, H13 are misreads with the right chunk in context - step 5's target too).

**Step 3 - calculator tool: scoped, then deferred (user, 2026-09-30).** Only 3 of the 55 questions involve arithmetic,
and two are answered from a figure the filing states (H4's "$3.1 billion", T3's "16%"); V3 ("sum up the numbers",
wrong - the model lists the three years and never adds) is the only answer a calculator would change. Measuring it
properly needs its own calculation questions first (8-10: sums, differences, % changes, a margin, a cross-company
comparison), written and reviewed before any code, with the 55 as the no-tool control. Design if resumed: offer the tool
only when code detects a requested calculation (Microsoft Learn's tool-calling guidance: "limit tool registration to
only the tools relevant for a given conversation context"; also avoids Llama 3.1 calling tools unasked); one
`calculate(expression)` evaluated in C# `decimal` by a small parser (numbers, + - * /, brackets - no eval);
`FunctionInvokingChatClient` over the Ollama client (Microsoft Learn lists Ollama as supported, streaming included);
each call printed under `--verbose`. Risk: the second round trip re-sends the prompt - ~2,946 + ~200 tool tokens + 768
output = ~3,900 of 4,096, so `num_ctx` should be set explicitly with it. *Decision (user): defer - a good-to-have for the
portfolio story, but the current failures are mostly answer-side; step 3b next.*

**Step 3b - answer verification: measured offline, then deferred (user, 2026-09-30).** The check as planned - every
figure an answer states must appear in the context it was given - replayed on all 55 `structured-2` answers: each
question's top-5 context rebuilt by the hybrid replay (which reproduces the app's scores), figures pulled from the answer
(citations, dates, years and Item/Note numbers excluded), matched against the context's numbers.
- **0 of 7 wrong answers flagged.** Every wrong answer states a figure that is in its context - they are misreadings,
  not inventions: a lookalike line (T1 "Comprehensive income" for *Other* comprehensive income; H13 the cash note's
  figure; Q2 "Total Oracle Corporation stockholders' equity"), the wrong figure from the right row (T9 the per-share
  $1.70, not the total), the wrong figure for the question (R3 MD&A's increase), a calculation not done (V3), a misread
  passage (T6).
- **0 of 48 good answers flagged** (one spike artifact - "November 2025" cut to "25" by the date pattern - aside).
- The plan's targets came from earlier runs: T9's "$0" (a figure from nothing) and prompt v2's invented R2 calculation.
  At temperature 0 with prompt v1 and today's retrieval neither occurs. Tracing is convenience, not a score change;
  `num_ctx` isn't needed while prompts peak at 3,714 of 4,096 tokens (it is if the calculator comes back).
*Decision (user): defer 3b* - a cheap guard to add if invented figures return (another model, say). Every remaining
wrong answer is a misreading of context the model has: step 5's target. **Before step 5, a fresh held-out set
(H16-H35)**, written and answered from the filings before any prompt change - prompt work is where v1 overfit (two
revisions each fixed their target and broke others), and H1-H15 already helped choose step 2's variant.

**H16-H35 written and baselined (2026-09-30, `eval/structured-2-heldout35/`).** 20 candidates drafted from the filings
(lookalike lines, per-share vs total, prior periods, statement vs note, segment rows, thousands, declines), every
expected figure found in the chunk text and in the filing's HTML with its units line; the user kept all 20 without any
being run (docs/Manual-Test-Questions.md). Graded and replayed as their own group. Baseline on the unchanged step 2 code:
- **Repeatable:** H1-H15 word for word as in `structured-2/` (H15 differed only by the old log's trailing prompt).
- **H16-H35 reliable 16/20**, replay recall@5 15/18, MRR 0.741.
- **3 of the 4 failures are retrieval:** H25 (Oracle total operating expenses - rank 11; the model gave total revenues
  instead of declining), H29 (Nasdaq Index revenue - rank 7; a wrong figure), H34 (Netflix cash for buybacks - rank 9;
  the share count and MD&A's rounded "$9.1 billion", which rounds both the cash-flow and the equity-statement figure -
  wrong, user's decision). **1 is answer-side:** H20, a decline written as prompt v1's "The unit is not stated.".
- **Passed:** all 8 lookalike-line questions, both per-share, both prior-period, all four NFLX thousands.
*What it changed:* the step 5 case rested on T1, H13 and Q2 - studied cases; on fresh questions lookalike lines weren't a
problem (8/8) and retrieval was. *Decision (user):* first a narrow step 5 change - the decline wording only (H20 now; H8,
H15, Q15, T2 in earlier runs) - then resume the 2b reranking spike, which now has measured targets (H25, H29, H34, R3 -
all at rank 7-12). The rest of step 5 waits for evidence; 20 questions is a direction, not a rule.

**Step 5a - a fixed decline form: two measured versions, the second kept (2026-09-30, `eval/structured-5a/`).** One change
to prompt v1's opening paragraph - "If the excerpts do not contain the answer, say so in one sentence instead of
guessing, without listing unrelated figures" became "... in exactly this form: "The excerpts don't contain <what the
question asks for>."". It is prompt v2's decline form (the manual pass), the one part of v2 that did what it targeted; v2
failed by changing four things at once, so this changed only that. Retrieval identical to step 2 on every question in
both runs - every difference is the prompt.
| | step 2 | first version (declined) | **kept version** |
|---|---|---|---|
| Main / targeted / routing / variants, reliable | 22/24, 7/10, 2/3, 2/3 | 22/24, 6/10, 2/3, 2/3 | 22/24, 7/10, 2/3, 2/3 |
| Held-out H1-H15 / H16-H35, reliable | 14/15, 16/20 | 14/15, 16/20 | 14/15, **17/20** |
- **First version** (`eval/structured-5a-declined/`) ended with "Do not add figures or units to it.": every decline clean
  (H20 fixed), but two NFLX answers dropped "thousand" (T10 "$96,795", H31 "$13,326,603") - the only place the prompt
  now said "do not add ... units", and both regressions were exactly that. A clause meant for declines leaked into
  figure answers.
- **Kept version** - the same without that sentence: H20 fixed ("The excerpts don't contain Microsoft's total revenue in
  fiscal year 2022."); every decline clean (H8, H15, H20, H30, Q15, Q16, V2 - none leaks "The unit is not stated.");
  T6 and H29 wrong figures -> declines (still not counted right, but no misread figure stated); T10 and H31 units back;
  no correct answer changed status or became a decline.
- The first prompt change in this project that fixed its target without moving a failure elsewhere - because it changed
  one sentence, and the leak from the first version was caught by the full run.
- The user raised whether the opening "Answer using ONLY the context excerpts" is reliable (in their manual testing a
  "don't" phrasing held better than "ONLY"). No measured sign it fails now: the 3b spike found no answer figure outside
  its context in 55 answers, and no decline trap produced a training-data figure. Not changed alongside the decline form
  (one change per run); a candidate to test on its own if a failure is found.
*Decision (user): keep the second version.* Next: resume the 2b reranking spike (targets H25, H29, H34, R3).

**Second-model run (step 0's last item) - granite4.1:3b and granite4.1:8b (2026-10-01, `eval/granite41-3b/`,
`eval/granite41-8b/`).** Step 5a's code, index and settings; only `Ollama:ChatModel` changed (a separate build folder per
model). Neither reports the `thinking` capability, so no reasoning path. The 3b was pulled by mistake for the 8b and kept
as a size comparison within one family. No prompt truncated (largest 2,960 tokens + 768 of 4,096, Ollama server log).
| reliable | llama3.1:8b (5a) | granite4.1:3b | granite4.1:8b |
|---|---|---|---|
| Main Q1-Q24 | **22/24** | 17/24 | 21/24 (+2 check) |
| Targeted T1-T10 | 7/10 | 5/10 | **9/10** |
| Routing R1-R3 | 2/3 | **3/3** | 2/3 (+1 check) |
| Variants V1-V3 | 2/3 | 1/3 | 2/3 |
| Held-out H1-H15 | **14/15** | 11/15 | 12/15 (+1 check) |
| Held-out H16-H35 | **17/20** | 14/20 | 16/20 (+1 check) |
| Time, 75 questions | ~75-85 min | ~35 min | ~2 h |
- **granite4.1:3b loses on reading, not retrieval:** wrong declines with the answer in context (Q12, Q14, Q22, Q24, V1,
  H11, H28, ...), lookalike lines (Q3's 2024 total, T9 and H6's traps, H26 - also "thousand" for millions), a dropped
  unit (T10, H17), "for the year ended" on balance-sheet dates. Its one gain is R3, a clean decline. H34's answer has a
  `�` ("$9.1�billion") - the open `Console.OutputEncoding` item, seen in real output for the first time; it also crashed
  `grade_answers.py` printing to a cp1252 console (`PYTHONIOENCODING=utf-8` works around it).
- **granite4.1:8b reads tables better than llama:** Q2 ($43,056M, the expected line - llama takes $42,508M), T2, T6
  ($7,952M - llama declines), T9 right. Losses against llama: Q23 (the 3b's line too) and H2 (effective tax rate 19.4%, the
  3b's answer too). H13, H25, H29 and H34 fail for llama as well (H25, H29, H34 are its retrieval misses). **It breaks the decline form:** Q15, Q16, R3, H8
  and H30 decline correctly, then explain or quote nearby figures (Q16 names the wrong set of filings) - `check` answers,
  to be resolved. **H25 is the first invented figure measured:** "$34,000 million" total operating expenses - in no
  context chunk; the filing's only 34,000 is a Services headcount (chunk 725), not among its 5. Step 3b's spike found 0
  of 7 wrong llama answers stated a figure outside their context.
- **Shared by all three:** T1 (the 104,075 trap) and V3 - not model-specific.
*What it shows:* llama's lookalike-line misses (Q2, T2, T9) are partly the model - a same-size model reads them right -
but granite trades them for decline-form drift and one invention, and is ~1.5x slower. Llama's remaining held-out
failures are retrieval (H25, H29, H34), which no model fixed - still the 2b spike's targets.
*Decision (user, 2026-10-01): keep `llama3.1:8b`.* The five `check` answers are left unresolved - none changes the decision.

**`Console.OutputEncoding` fixed (2026-10-01).** `Program.cs` sets `Console.OutputEncoding` and `InputEncoding` to UTF-8
(no BOM) at startup and restores the console's originals on exit (setting them changes the code page for the whole
terminal session). Before, redirected output used the console's OEM code page: a non-breaking space became a lone 0xFF
(invalid UTF-8 - 38-41 lines in every eval log, read as `�`), and characters the code page lacks were silently best-fitted
- `’` to `'`, `—` to `-`, `•` to a 0x07 control byte. Verified on one NDAQ question against `structured-5a`: the log is valid
UTF-8 with real NBSPs and curly apostrophes, the 25 retrieved chunks are identical in rank and score, and the answer
differs by one final full stop - which the pre-fix build produces too when the question is asked alone (Ollama's
prompt cache within a session), so not the fix. Consequences for the tools: `grade_answers.py`'s decline pattern now
accepts `don’t` as well as `don't` (a curly apostrophe used to arrive straight), and its console output replaces a
character it can't print instead of crashing. Every committed grade file of `structured-5a` and both granite runs
regrades identically. Logs from here on aren't byte-comparable with earlier ones on punctuation - grades and replay are
unaffected (figures, units, keywords and scores are ASCII).

**Step 2b resumed - plan re-checked against Microsoft Learn, models fetched and vetted (2026-10-01).**
- *Re-check:* Azure Architecture Center, "Information retrieval", unchanged on every point recorded above (pipeline,
  when to rerank, the two MiniLM names, order-not-threshold, benchmark relevance and latency). No reranking abstraction
  in `Microsoft.Extensions.AI` (still `IChatClient`, `IEmbeddingGenerator`, `IImageGenerator`); Microsoft's reranker
  clients (Azure Semantic Reranker beta, Cosmos DB) are cloud services. `BertTokenizer` and its pair methods
  (`BuildInputsWithSpecialTokens`, `CreateTokenTypeIdsFromSequences`) are in the stable `Microsoft.ML.Tokenizers` 2.0.0
  already referenced (checked in the DLL; Learn's pages show a preview build).
- *Added to the spike:* rerank both the top 25 and the top 50 (the guidance: start at 20-50, adjust); a passing replay
  needs a full run confirming every correct decline still declines (Learn: test negative examples too); before any
  build, Python `tokenizers` and .NET `BertTokenizer` must give identical ids on every chunk, or the spike doesn't carry
  over; the reranker's input text is a variable (chunk as the model sees it vs with step 1d's company line), measured,
  not defaulted.
- *Dropped:* `bge-reranker-base` - XLM-RoBERTa, a SentencePiece tokenizer (not `BertTokenizer`), >1 GB, several times
  slower on CPU. Only if both MiniLM models disappoint.
- *Provenance and vetting (user asked for this before anything was fetched):* only `onnx/model.onnx` + the five tokenizer
  and config files per model, by `curl` from pinned commits - no pickle (`pytorch_model.bin`), no `transformers`, no Hub
  client. Stored outside the repo in `%USERPROFILE%\models\cross-encoder\`.
  | | ms-marco-MiniLM-L6-v2 | ms-marco-MiniLM-L12-v2 |
  |---|---|---|
  | commit | `233902d25c440f23af6f7d6e94d2946bac0bee0a` | `7b0235231ca2674cb8ca8f022859a6eba2b1c968` |
  | `model.onnx` bytes | 91,011,230 | 133,743,863 |
  | `model.onnx` sha256 | `5d3e70fd0c9ff14b9b5169a51e957b7a9c74897afd0a35ce4bd318150c1d4d4a` | `2ac389ab6abe08dcbddf2c41a69b6a18853e0dca440adbc2abc1adfcee46483f` |
  | HF security scan | done, no files with issues | not run |
  | license | Apache 2.0 | Apache 2.0 |
  Every file matched Hugging Face's hash (sha256 for the models, git blob sha1 for the text files; the tokenizer files are
  identical in both repos). Both graphs, read field by field without loading them (no `onnx` package): only `ai.onnx`
  ops at opset 14, no external data, no functions, no subgraphs, exported by PyTorch 2.6.0 - which covers L12's missing
  scan. `pip install tokenizers==0.23.2` also brought 11 dependencies (huggingface-hub, hf-xet, httpx, httpcore, h11,
  anyio, fsspec, filelock, tqdm, pyyaml, colorama); OSV lists no advisories for any of them or `onnxruntime` 1.20.1;
  `pip check` clean. To undo: delete the models folder, `pip uninstall` those packages.

**Step 2b spike - measured (2026-10-01, `eval/rerank-2b-spike/`, `tools/rerank_spike.py`).** Replay only: each question's
own hybrid candidates (from the step 5a logs) scored by the cross-encoder per (question, chunk) pair, each company's list
reordered by score, merged as the app merges. `tools/replay_recall.py` was refactored into functions the spike imports -
its output byte-identical before and after on both question sets. Recall@5 (what the model reads) on the 68 answerable
questions:
| rerank the top 25 | Q | T | R | V | H1-15 | H16-35 | total | CPU s/question |
|---|---|---|---|---|---|---|---|---|
| none (step 5a) | 22/22 | 10/10 | 2/3 | 2/2 | 13/13 | 15/18 | 64/68 | - |
| **L6, company line** | 22/22 | 10/10 | 3/3 | 2/2 | 13/13 | 17/18 | **67/68** | ~1.1-2.7 |
| L12, company line | 21/22 | 10/10 | 3/3 | 2/2 | 13/13 | 17/18 | 66/68 | ~2.6-5.7 |
| L6, excerpt only | 20/22 | 9/10 | 1/3 | 2/2 | 12/13 | 15/18 | 59/68 | ~1.9-2.8 |
| L12, excerpt only | 20/22 | 9/10 | 3/3 | 2/2 | 13/13 | 16/18 | 63/68 | ~5.2-5.3 |
| L6, company + windows | 22/22 | 9/10 | 3/3 | 2/2 | 13/13 | 16/18 | 65/68 | ~1.7-1.8 |
| L12, company + windows | 21/22 | 9/10 | 3/3 | 2/2 | 13/13 | 17/18 | 65/68 | ~3.6-3.8 |
- **Targets:** R3 12 -> 4 (L6) / 1 (L12), H25 11 -> 3 / 1, H34 9 -> 5 / 4. **H29 moves in no setting** - its figure (827)
  is in 83 chunks; a lookalike-row problem, not ranking depth.
- **L6 + company line is the only setting that loses no answer from the top 5.** L12 ranks answers higher (MRR T 0.75 ->
  0.80, R 0.47 -> 0.83, H16-35 0.74 -> 0.78) but drops Q20's Oracle figure (2 -> 8); both lower H1-H15's MRR (0.90 -> 0.80
  L6, 0.86 L12) - answers that stay in the top 5 but lower in it.
- **The company line is what makes it work:** without it both models do worse than no reranking (L6 loses Q1, Q4, Q20,
  T9, R2) - step 1d's finding again, for the reranker.
- **Depth 50 never beat 25** (equal or slightly worse everywhere): 25 it is.
- **Truncation:** 30-40% of candidates exceed the 512-token window (company line + excerpt line + content: median 470
  WordPiece tokens, p90 542, max 606; ~489 fit beside a question) - only their last 0-120 tokens are cut. Checked for every
  expected figure in every chunk: none is reachable only in a cut-off tail. Smaller chunks (asked by the user) were not
  pursued: no answer is hidden by the cut, they'd change embeddings, keyword search and what fits the 5 excerpts (a full
  rebuild and re-measure), and step 1c-b's finer chunks already cost 14-18% MRR.
- **Windows (MaxP) - measured, declined.** A chunk too long for the window scored twice - its head, and its tail opened by
  the same company and excerpt lines, cut at a row boundary - keeping the higher score (the tail decided 15-23 answer
  chunks per question set; nothing truncated). MRR up (L6: T 0.75 -> 0.86, H1-15 0.80 -> 0.87, H16-35 0.64 -> 0.73), but
  the top 5 lost T9 (both models) and H34 (L6): seeing whole chunks also lifts lookalike rows elsewhere. ~1.5x the CPU.
- Timings are wall-clock and noisy (the same configuration measured 1.1 and 2.7 s); the ratios hold. Under ~6 s per
  question in every setting - ~5% of an answer's ~60-100 s.
*Decision (user): build L6 + company line, reranking the top 25, no windows* - behind a setting, off by default as Hybrid is;
first the tokenizer parity check (.NET `BertTokenizer` vs Python `tokenizers`), then a full run on all 75 questions against
step 5a: the strict 22/24 bar, every correct decline still declining. The differences are 1-3 questions of 68 - a direction;
the full run decides.

**Step 2b - build (2026-10-01).** `Retrieval:Rerank` (off by default; Hybrid only - the app refuses it with Vector), with
`RerankCandidates` 25, `RerankModelDirectory` and `RerankModelSha256`. `CrossEncoderReranker` checks the model's SHA-256
before ONNX Runtime reads it (a swapped file is refused at startup); `RagAnswerService` reranks each company's top 25
hybrid candidates and keeps its share, as the spike did; each passage is the company line (`CompanyRegistration.Context`
= step 1d's `CoverFacts.EmbeddingContext`) + the excerpt header + the chunk. `--verbose` lines read
`score=<fused> rerank=<cross-encoder> hybrid=#<rank before>`. Packages: `Microsoft.ML.OnnxRuntime` 1.30.0 (latest stable)
and `Microsoft.ML.Tokenizers` 2.0.0 (already transitive) - no vulnerable packages.
- **The tokenizer parity check found what the spike wouldn't have carried over.** `BertTokenizer` (stable 2.0.0) as
  configured from the model's own settings differed from Python's `tokenizers` on **all 73,125** (question, chunk) pairs of
  both question files: it drops line breaks (fusing "STATES\n\nSECURITIES" into "states ##se ##cu ..."), drops ASCII
  symbols Hugging Face keeps as punctuation (`|` and `$` - every table row's separators), and drops unknown symbols
  (the cover's "☒"; Hugging Face: `[UNK]`). Built as-is, the reranker would have read our tables without their
  separators - text it was never measured on. `BertPairEncoder` now does Hugging Face's BertNormalizer and
  BertPreTokenizer itself (clean, CJK, strip accents, lowercase; whitespace + ASCII/Unicode punctuation split) and uses
  `BertTokenizer` for WordPiece only: **0 of 73,125 pairs differ**, checked with the app's own source compiled in.
- **Scores:** the app's reranker (ONNX Runtime 1.30, .NET) vs the spike's (1.20.1, Python) over all 975 chunks for two
  questions: max difference 7.2e-6, top-25 order identical. ~2.1 s per 25 candidates.
- **End to end:** the app's reranked order for Q1, Q20 (two companies, interleaved) and R3 matched the spike's code
  exactly over all 25 positions - so the company lines match too. R3 answered right for the first time ($137,791
  million; its answer chunk hybrid #19 -> 1).
- Tests: 367 (17 new - the encoder's Hugging Face behaviours on a 14-word vocabulary, the model's load-time checks, the
  service's per-company rerank, passage text and cut). The scorer is faked in tests, not Moq-mocked: Castle proxies an
  internal interface into its unsigned assembly, outside `AssemblyInfo`'s strong-name-scoped grant.

**Step 2b - full run (2026-10-01, `eval/structured-2b/`).** Step 5a's eval settings plus `Rerank: true`. The main set was
stopped by Claude Code at 35 of 40 questions (the machine ran low on memory - other load, not the run: re-running the last
five, free memory never fell below 11.8 GB of 31.7), so it is in two logs - `main.log` (Q1-R1, one session) and
`main-rest.log` (R2-V3, a fresh session). Answers so far, against step 5a:
- **R3 right for the first time** - $137,791 million (its chunk hybrid #19 -> 1). **Q2** now gives $43,056M, the expected
  line (5a and every model before: $42,508M) - but see V1: a lookalike flip, not counted as a robust gain.
- **V1 now wrong** - $942M ("Comprehensive income attributable to Nasdaq") for $940M ("Comprehensive income"). Both lines
  are in one chunk, NDAQ's comprehensive income statement, **ranked #1 in both runs**; only the four excerpts around it
  changed. Deterministic: $942 in four runs, two with every model unloaded first (Ollama reuses a cached prompt prefix -
  the user's catch - so only uncached runs count as independent).
- **Which excerpt tips it - ablation** (`eval/structured-2b/v1_ablation.py`: the app's exact prompt sent to Ollama, the
  model unloaded before every call; both controls reproduced the app, $942 reranked and $940 step 5a): **the answer
  chunk alone gives $942**; the reranked five without excerpt 2 (fair value note), 3 (segment table) or 5 (Item 8
  narrative) give $940; without excerpt 4 (the MD&A "attributable to Nasdaq" table - the suspect) still $942. No excerpt
  pushes llama to the lookalike: **$942 is its default reading of that table**, and step 5a's right answer came from the
  mix of excerpts around it. V1 sits on a knife edge - counted as noise from a fragile lookalike question, not a
  regression the reranker caused; the same reading applies to Q2 the other way. The lookalike family (V1, Q10, T1, H26)
  is an answer-side problem (step 5's answer skills, or a stronger reader - granite4.1:8b read these better).
- Rule from this: **re-check a single question with the model unloaded first** (`ollama stop llama3.1:8b`).
- **Results** (main graded on `main-merged.log` - the two logs joined, 40 answers; held-out one session):
  | reliable | step 5a | **step 2b** | changes |
  |---|---|---|---|
  | Main Q1-Q24 | 22/24 | **23/24** | +Q2 |
  | Targeted T1-T10 | 7/10 | **9/10** | +T1 ($2,243M - 5a took the 104,075 lookalike), +T6 ($7,952M - 5a declined) |
  | Routing R1-R3 | 2/3 | **3/3** | +R3 |
  | Variants V1-V3 | 2/3 | 1/3 | -V1 (above) |
  | Held-out H1-H15 | 14/15 | **15/15** | +H13 ($9,033,681 thousand - the spike's truncated-tail figure), H2 held (below) |
  | Held-out H16-H35 | 17/20 | **18/20** | +H25 ($46,751M) |
  | **total** | **64/75** | **69/75** | |
  Strict main bar 23/24 (bar 22/24); every correct decline still a clean decline (Q15, Q16, V2, H8, H15, H20, H30); largest
  prompt 2,966 tokens + 768 of 4,096, nothing truncated. Still failing: H29 (Index revenue - no reranker setting moved
  it), H34 (MD&A's rounded "$9.1 billion", wrong by the earlier decision), V3 (asked-for arithmetic), V1, Q21 (as in 5a).
- **Reader decisions (user):** H2 "19.4%" **reliable** - the grader expects "19%", but 19.4% is the filing's own figure
  (the income-tax note's reconciliation, "Effective rate - 2026: 19.4%"; MD&A rounds it to 19%). T9 **not reliable** - the
  $1.70 per-share dividend plus "the total isn't in the excerpts": a partial decline (wrong in 5a too). Both recorded in
  the `.json` files (`graded`, `resolution`, `resolved_by`).
*Decision (user, 2026-10-01): keep reranking.* Every bar met, +5 reliable answers for ~2 s of CPU per question. The three
retrieval targets reached and answered (R3, H25, H13); H29 remains - a lookalike-row problem, not depth. Shipped defaults
stay Vector + Markdown, reranking off, until v2 closes; the eval build sets Structured + Hybrid + Rerank.

**Answer side: a fresh question set first (user, 2026-10-01).** After 2b, 5 of the 6 failures were misreadings of an
answer llama had in its top 5, one example of each kind (V1 lookalike, T9 per-share vs total, H34 MD&A rounding, Q21
dropped unit, V3 arithmetic), four on the studied main set and H34 on the never-tuned H16-H35 - five rules for five single
cases is how v1's prompt revisions overfit. So **A1-A27** (`tools/answer-questions.txt`, docs/Manual-Test-Questions.md):
drafted from the filings for those kinds, every figure and trap checked in chunk text and HTML, arithmetic results printed
nowhere in the filing (five drafted calculations dropped because they were); the user kept all 27 unseen. They are the
answer-side targets - a change may be chosen on them; H16-H35 stay the never-tuned check. Grader: an `A1-A27` group and
an `exact` flag (A11, A13-A17, A21-A27) so a rounded "$X.Y billion" doesn't pass where the rounding is the trap or the
answer is asked-for arithmetic (as H34 was graded); every earlier grade unchanged. `replay_recall.py` learned the set
(`expect_answer`, the inputs for arithmetic questions) - main and held-out output byte-identical.

**A1-A27 baseline (2026-10-01, `eval/answer-side-baseline/`; step 2b's eval build, reranking on): 18/27 reliable.**
| kind | reliable | misses |
|---|---|---|
| lookalike lines (A1-A7, A19) | **8/8** | - |
| per-share vs total (A8, A9) | **2/2** | - |
| units, thousands (A18-A20, A24) | **4/4** | - |
| decline (A12) | 1/1 | - |
| paid vs declared (A10, A11) | 0/2 | A10 retrieval (reranker), A11 retrieval |
| MD&A or another statement vs the statement (A13-A17) | 2/5 | A14 misread ("$55.7 billion" over the cash-flow line at #3), A15 and A17 retrieval (reranker) |
| arithmetic (A21-A27) | 3/7 | A21 listed both years without adding, A26 computed but said "$31.9 billion", A25 retrieval, A27 retrieval (reranker) |
- **The kinds the step 5 case rested on don't fail on fresh questions:** lookalike lines 8/8 (H16-H35 found the same, 8/8),
  per-share vs total 2/2, thousands 4/4. V1 and T9 stay single cases.
- **Only 3 of the 9 misses are misreadings** - A14 (MD&A rounding preferred to the statement line), A21 and A26
  (arithmetic not done, or rounded). The answer side's measured targets are arithmetic and MD&A rounding.
- **4 misses are the reranker's**, the answer in hybrid's top 5 and pushed out: A10 (rank 3 -> 10), A15 (4 -> 12), A17
  (3 -> 7), A27 (1, 2 -> 9, 20). Replay on the whole set (`rerank-replay.txt`): **recall@5 22/26 hybrid -> 18/26 reranked**
  (both L6 and L12), MRR 0.558 -> 0.398 (L6) - nothing gained. On main + held-out it went the other way (64 -> 67 of 68).
  A1-A27 lean on "how much was paid / spent" questions whose answers are cash-flow rows; MiniLM (trained on web
  passages) promotes MD&A and note prose that echoes the question's words over a statement row. A10 and A15 both have a
  cash-flow line pushed out by equity-statement and Item 5 chunks; A27's income statement lost to five MD&A chunks.
- 2 misses are retrieval either way (A11: hybrid 12, A25: 11 and 25).
*Open (for the user, 2026-10-02):* the 2b decision was made before this set existed. Measure A1-A27 end to end with
reranking off (hybrid only - the replay says 4 more answers would reach llama), then decide: keep reranking, drop it, or
spike a gentler form in replay first (fusing the reranker's rank with the hybrid rank by RRF instead of replacing the
order - cheap to replay on all four sets). The answer-side changes (arithmetic, MD&A rounding) wait for that.
- **Microsoft Learn on this (checked 2026-10-01, user's question):** Azure AI Search's semantic ranker "work[s] best on
  searchable content that is information-rich and structured as prose" (semantic search overview; the transparency note:
  "most likely to improve relevance over content that is semantically rich, such as articles and descriptions") - our
  misses are statement rows losing to MD&A and note prose. Azure HorizonDB lists "simple exact-match lookups" under
  "skip reranking"; the Databricks evaluation guide enables a reranker when it "improves metrics significantly".
  Blending is a documented pattern: AI Search applies a scoring profile after semantic ranking
  (`@search.rerankerBoostedScore`), and HorizonDB's graph RAG fuses "semantic reranking and graph traversal using RRF".
  The RAG guide: fine-tune a cross-encoder for specialised vocabulary, and benchmark several models on your own queries.
- **The reranker works as built** - tokens, scores and order match the spike (73,125 pairs; scores within 7.2e-6) - so
  this is the model's prose preference, not a defect. To confirm tomorrow: A10's and A15's cash-flow chunks are scored
  low, not mis-scored.
- **Strategy, cheapest first:** (1) A1-A27 with reranking off, end to end; (2) RRF of the reranker's rank and the hybrid
  rank, optionally weighted, replayed on all four sets - no new dependency; (3) a second model family
  (`bge-reranker-base`), only if blending isn't enough; fine-tuning not advised - ~100 questions, all evaluation sets, and
  training on them would spend them; routing exact lookups past the reranker would mean keyword rules like the ones
  behind R1 and T10 - a last resort.

**A1-A27 with reranking off (2026-10-02, `eval/answer-side-norerank/`): 21/27 reliable** against 18/27 reranked - step 2b's
eval build with only `Rerank: false` (the hybrid retrieval of step 5a). Replay's top score equals the log's `[1] score=` on
all 27.
| Q | reranked | not reranked | why |
|---|---|---|---|
| A17 NFLX long-term debt | check | **reliable** | answer back in the top 5 (rank 3; reranked 7) |
| A21 ORCL net income, two years summed | wrong | **reliable** | adds 12,443 + 17,087 = 29,530 |
| A25 NDAQ dividends + buybacks | wrong | **reliable** | adds 601 + 616 = 1,217, both from the equity note |
| A26 MSFT net income change | wrong | **reliable** | $31,917M, not "$31.9 billion" |
| A8 ORCL dividends per share | reliable | wrong | answer at rank 5, the last excerpt; took the $0.50 quarterly line for the $2.00 annual |
| A27 ORCL net margin | declined | wrong | both inputs found, divided wrong: 25.8% (17,087 / 67,357 = 25.4%) |
- **A10 and A15 are misreadings, not only retrieval:** with their cash-flow rows back at ranks 3 and 4, A10 takes the
  equity statement's declared $27,034M (the trap) and A15 a note's $16,719M. With A14 (MD&A rounding) and A27
  (arithmetic), 4 of the 6 misses have the answer in front of the model - the answer side's targets. A11 stays retrieval
  (rank 12).

**Reranker check (`eval/rerank-decision/rerank_check.py`, the question left open above):** the L6 scores recomputed in
Python match the app's logged `rerank=` on all 100 (question, chunk) pairs of A10, A15, A17 and A27 to 0.005 - the log's
two-decimal rounding. A10's and A15's cash-flow chunks are scored low, not mis-scored: the answer row is whole in the
window (token 395 and 362 of ~495), and equity-statement, Item 5 and note prose outscore it (A10: 7.29 for the equity
statement holding the declared trap, 4.66 for the cash-flow chunk). A27's income statement scores 1.73 against MD&A
percentage tables at 3.4-5.1. **A17 is truncation:** its balance-sheet row begins at token 510 of the 512 the model reads.
The spike's truncation check counts a figure as cut only when it starts past the last token read, so it reported A5 and
A24 and missed A17 - three questions of this set touch MiniLM's window, not two.

**Blend replay (`eval/rerank-decision/rerank_blend.py`, `blend-*.txt`):** the reranker's rank fused with the hybrid
rank by RRF (k = 60, `RankFusion.K` - not tuned, which would tune on the held-out sets), as two lists (blend2) or as a
fourth list in the app's own fusion (blend4); inputs are the hybrid logs without reranking (`structured-5a/` for main and
held-out, `answer-side-norerank/` for A). The hybrid and rerank rows reproduce the spike (64 -> 67 of 68) and
`rerank-replay.txt` (22 -> 18 of 26). recall@5, the 94 questions with an expected figure:
| | main (37) | H1-H15 (13) | H16-H35 (18) | A1-A27 (26) | **total** |
|---|---|---|---|---|---|
| hybrid | 36 | 13 | 15 | 22 | **86** |
| rerank (step 2b) | 37 | 13 | 17 | 18 | **85** |
| blend2 | 36 | 13 | 17 | 19 | **85** |
| blend4 | 36 | 13 | 16 | 20 | **85** |
Eight questions decide it - R3, H25, H29, H34 (reranker's) against A10, A15, A17, A27 (hybrid's) - and the two rankers
disagree so strongly on them that a blend lands their answers at rank 5-9 either way. recall@1 falls under reranking
everywhere but the variants (A1-A27: 10 -> 4).

**A second reranker family, screened (2026-10-02, `eval/rerank-decision/bge_screen.py`, `bge-screen.txt`):**
`BAAI/bge-reranker-v2-m3` (568M parameters, XLM-RoBERTa, 8,192-token window - so no truncation) on those eight
questions. Downloaded only after a safety review (user's condition): BAAI's official repo, Apache-2.0, weights as
safetensors (no pickle), a standard architecture with no `auto_map` (no remote code), pinned to commit `953dc6f6`, both
large files checked against the published SHA-256; no ONNX export from BAAI, so third-party exports were passed over and
the screen ran the official weights in PyTorch - `torch` 2.14.0 and `transformers` 5.17.0, a month and three weeks old
rather than the two-day-old latest, `--only-binary :all:`, every installed package checked on OSV (clean, except a
pre-existing PyJWT 2.14.0, GHSA-42vr-xj54-vc7v). Bar set before running: all four A answers kept in the top 5, and some of
R3/H25/H29/H34 gained. Result: better than L6 on 6 of 8 and worse on none (R3, H25, A17 at #1), but A10 6, A15 7, A27 12 -
**top 5 on 4 of 8, as hybrid's 4** - at ~38 s of CPU per question (L6 ~1 s). Bar not met; full sets not run. The packages,
model and caches were removed afterwards (user).

*Decision (user, 2026-10-02): reranking off - the eval build and v2's defaults use Hybrid without it; the code stays as the
opt-in `Retrieval:Rerank`.* Hybrid alone has the most answers in the top 5 (86 of 94); the graded gap the other way (87
vs 85 of 102 answers) is two questions, within what single excerpts decided today (A8 at rank 5, V1's knife edge). Both
rerankers prefer prose to statement rows, which most figure questions here are answered from, and reranking costs CPU
per question, a separately fetched model and the reimplemented tokenizer. Baselines from here: `structured-5a/` (64/75)
and `answer-side-norerank/` (21/27). Next: the answer side - A10/A15 paid vs declared, A14 MD&A rounding, A27 arithmetic.

**Answer side: what the model was given (2026-10-02, `eval/answer-side-screens/step5_prompts.py`).** The app's exact
prompts for the four misreadings of the rerank-off run, rebuilt (top 5 matching the log's scores). In each, the right
figure is in the context next to a plausible wrong one, and each excerpt opens with its statement's title, so a rule
could name the statements:
- **A10** (paid $26,445M): the equity statement's `Common stock cash dividends (27,034)` - declared - is excerpt 1; the
  cash flow statement's `Common stock cash dividends paid (26,445)` is excerpt 3. The model took excerpt 1 although only
  the other label says "paid" - the existing "line whose label matches the question" rule, not followed.
- **A15** (spent $22,271M): four candidates - the equity statement's two pieces (6,105 and 16,181), the repurchase-program
  note's `Total ... $16,719` (taken), and the cash flow statement's `Common stock repurchased (22,271)`.
- **A14** (55,663): MD&A prose "$55.7 billion" (excerpt 1, taken) over MD&A's own table row `(55,663)` (excerpt 4) - a
  precision choice, wrong only because A14 is graded `exact`.
- **A8** ($2.00): $2.00 is in three excerpts; the model took "In June 2026, the Board declared a quarterly cash dividend
  of ... $0.50" - after fiscal 2026 ended (May 31). One case, right in the reranked run; no rule proposed.

**Step 5 screen - a cash-flow rule (2026-10-02, `cash_rule_screen.py`, `cash-rule-screen.*`).** Before a ~2-hour full
run (user's question: is it worth it?), one sentence screened on 12 questions: "When the question asks how much cash was
paid, spent or received, use the cash flow statement's line if an excerpt contains it." - after the similar-names rule.
The app's top 5 and prompt, sent to Ollama with and without it, the model unloaded before every call; targets A10 and
A15, controls right today where the rule could misfire (Q4, Q24 - asks for the equity statement's figure - T2, T8, T10,
A9, A12, A13, A16, A25); H16-H35 not used. Bar: both targets fixed, every control kept.
- **0 of 2 fixed** - A10 still $27,034M; A15 still $16,719M, now "in cash" (the rule's word, not its line). Controls
  10/10 kept (A13 now cites a section that doesn't exist, "Item 8 > CASH FLOWS STATEMENTS"). Full run not made.
- Harness: 10 of 12 current-prompt answers word for word as logged; T8 and T10 (from `structured-5a/main.log`, run
  2026-09-30) differ in wording only - a moved full stop, a dropped "as stated in the excerpt" - same figures and grades,
  while Q4, Q24 and T2 from the same log match exactly. **Unexplained**; noted against the "temperature 0 is
  word-for-word repeatable" fact this project has relied on.

**Step 3 screen - the calculator (2026-10-02, `calculator_screen.py`, `calculator-screen.*`; user: worth it first?).**
One `calculate(expression)` tool through Ollama's native tool support, evaluated as the planned C# tool would (numbers,
+ - * /, brackets, Decimal; no eval), on the app's unchanged prompt and top 5; targets A27 and V3, controls A21-A26 (right
without a tool). Bar: both targets fixed, no control lost.
| Q | without | with the tool | |
|---|---|---|---|
| A27 | wrong | **reliable** | `(17087 / 67357) * 100` = 25.367816 -> 25.4% |
| V3 | wrong | wrong | passed 10,149,273 as `10014.9273`, then ignored the result and listed the years |
| A24 | reliable | **wrong** | passed 5,407,990 as `100407990` and 10,981,201 as `10881201` - an exact sum of wrong numbers |
| A26 | reliable | **wrong** | the tool's 31917, restated as "$31.917 billion" |
| A21, A22, A23, A25 | reliable | reliable | A22 and A25 wrote "million" into the expression (refused) and calculated themselves |
- llama called the tool on all 8 - it is not ignored - but **+1 -2**. The calculator moves the error from arithmetic to
  copying figures into the call, and at 8B the copying is no more reliable. The unit refusals are fixable; the copying
  isn't, short of code choosing the operands (the facts table, step 4). ~150-200 s per question against ~70-100; prompts
  peaked at 3,022 tokens, inside the window with the output.
*Decision (user, 2026-10-02): no step 5 rule and no calculator; close v2.* A10, A15, A14 and A8 are recorded as
llama3.1:8b misreadings that neither a prompt rule nor a tool fixed - every remaining answer-side miss has its answer in
the context, and neither lever moves the model.

Steps 2b and 3b added 2026-09-28 (user), from a review of what a full RAG system has that this one doesn't.
Considered and left out unless wanted for a demo - they add breadth but fix no measured failure: conversation
memory (follow-up questions), query decomposition beyond per-company search, an API or UI. Automated onboarding
(registering a filer from its XBRL cover facts) fits naturally into 1b.

**Step 0 progress.** Held-out set written 2026-09-28: 15 questions (H1-H15) chosen by the user from 20
drafted candidates, none run before selection; `tools/heldout-questions.txt`, expected answers in
Manual-Test-Questions.md.
Answer grader written 2026-09-28: `tools/grade_answers.py` + `tools/expected-answers.json` (all 55 questions,
keyed by question text). Validated against the four hand-graded runs (prompt v1 on both strategies, v2, v3 on
Markdown): every group total matches the hand grades - main 22/22/20/23, targeted 4/5/4/2 reliable. Two bugs
were found this way and fixed (a decline stating "$0" read as a decline; "not *explicitly* stated" missed).
What it can't settle it marks "check" instead of guessing: a decline that still states a figure (T8's $85.47,
T9's "$0", v2's invented R2 calculation). One stricter call: v1 Markdown's T3 ("a $438 million increase in
personnel costs", no percentage) grades wrong. The period isn't checked - the hand grading didn't enforce it.

**v1 baseline recorded 2026-09-28** (`eval/baseline-v1/`, tag `v1.0` code, Markdown): main 22/24 reliable,
targeted 4/10, routing 3/3, variants 1/3; **held-out 8/15**. The 40 main answers are word-for-word identical to
the 2026-09-25 run - temperature 0 is deterministic on this setup, so a later change in score is a code change.
Largest prompt 3,095 tokens, no truncation. Three "check" answers resolved by the user: T9 wrong ("$0"), H6
wrong (the $412M increase, not the $10,272M total), H10 declined. One grader fix from it: "$3.1 billion" now
counts for 3,074 million when it rounds right (H4 - the filing's own wording).
- **The held-out set caught what the main set hid.** Five of 15 held-out answers are prompt v1's malformed
  decline, "The unit is not stated." - both negatives (H8, H15) and three answerable questions (H11, H13, H14) -
  against 1 of 24 on the main set (Q15). The main set flattered prompt v1; this is evidence for v2's fixed
  decline form (prompt v2 had it) and for answer skills, not a reason to tune on H1-H15.
- **Lookups that reach the right chunk work:** H1-H3, H5, H7, H9, H12, including three traps (H3's lookalike
  945, H9's jargon label, H4's rounding).
- **Routing again:** H14 (NFLX deferred revenue) routed to the income statement on "revenue", like R1; H10's
  headcount sits in wrapped prose and wasn't retrieved.
Still to do: the second-model run (candidates `granite4.1:8b`, non-reasoning, 5.3 GB; `qwen3.5:9b`, reasoning,
6.6 GB - the user is deciding).
**Linearized on the held-out set (2026-09-28): 11/15 reliable vs Markdown's 8/15** - none of Markdown's five
"The unit is not stated." answers (H8, H11, H13-H15 became two correct declines, one right answer, one clean
decline, one wrong). Its two wrong answers take lookalike lines: H6 $2,805M (stock-based compensation within
R&D, not the $10,272M total), H13 $9,067,872 thousand (cash "and short-term investments"). H10 declines the
headcount after an unrelated, correctly labelled share count - graded declined by the user, like Markdown's H10.
Saved as `eval/baseline-v1/linearized-heldout.*`. Doesn't reopen v1's default (the
rule was the main set, where Linearized regressed on T4 and Q2), but favours building v2 on the linearizer.

**Naming (user, 2026-09-28):** v2 is a **new, third strategy, `Structured`** - Markdown and Linearized stay
frozen as the reference (markitdown with them). "Hybrid" is kept for the keyword + vector search. Steps 1a-1d
build `Structured` up, each measured against a v1 strategy.

**Step 1a done (2026-09-28): DOM parse, no markitdown.** `HtmlTextConverter` (AngleSharp) reproduces
markitdown's text shape - one paragraph per block element, links, fenced `<pre>`, markdownify's pipe tables
(made-up empty header row, merged cells padded). Tables go through `HtmlTableLinearizer`, so 1a's reference is
Linearized (the plan's "expand merged cells" / reuse-the-linearizer already decided it). Against Linearized's
chunks: same section outlines and every distinct line in all four filings; 999 chunks each, 989 word for word
(the other 10 are the same text with a boundary moved by markitdown's doubled spaces). Chunking all four takes
~7 s. Three converter bugs found by that comparison, each now a test: links whose text sits in a `<span>` came
out empty (ORCL's 131 "Table of Contents" links, and with them "Item 6. [Reserved]"), a `<div>` inside a table
cell joined words ("ExhibitNumber"), and the made-up header row was missing. Full question run skipped
(proposed - chunks near-identical); measured from 1b on.

**Step 1c's first change, made early (the user found it reviewing MSFT chunk 197):** a table the linearizer
falls back on becomes text rows, not a pipe table. MSFT's exhibit index is seven tables, one per page; the two
holding management-contract exhibits ("10.6*") read as financial - a text label beside a number (the
referenced exhibit, "10.4") - and fell back ("two values ... map to one column", "cell text lost: 'Filed
Herewith'") to pipe tables of mostly empty cells, while the other five pages came out as text rows.
`HtmlTableLinearizer.LinearizeAsText` (new, additive - Linearized's behaviour is unchanged) is used in
`Structured` instead: 504 pipe-table rows -> 0 across the four filings, 999 -> 944 chunks, outlines unchanged.
**Step 1c's second change, also early:** a text table's column names are repeated on every piece. A split
text table's continuation pieces carried rows only - MSFT chunk 198: "4.24 | Description of Securities | 10-K |
6/30/2024 | 4.26 | 7/30/2024", with no "Form" or "Exhibit" column names in sight. No filer uses `<thead>` or
`<th>`; MSFT, NFLX and ORCL mark column names only by bold text, so a text table's leading all-bold rows
(`HtmlTableLinearizer.LeadingBoldRowCount`, additive; 0 when every row is bold, as in cover-page boxes) become
the row block's context line, which TokenChunker already repeats on every piece. Checked across all text and
fallback tables: it finds the exhibit, signature and officer headers; a bold title row in a few one-chunk
tables ("Critical audit matter") is caught too, harmlessly. Effect: only the exhibit indexes change (header on
12 of MSFT's pieces instead of 7, NFLX 7 instead of 3, ORCL 8 instead of 5); every other chunk is word for word
the same. 187 tests.

**Measured 2026-09-28 (`eval/structured-1c-early/`, against Linearized):**

| | Linearized | Structured |
|---|---|---|
| Main Q1-Q24, reliable | 22/24 | 22/24 |
| Targeted / routing / variants | 5/10, 3/3, 1/3 | 5/10, 3/3, 1/3 |
| Held-out, reliable | 11/15 | **10/15** (H11) |
| Replay, main: recall@5 / MRR | 22/22, 0.784 | 22/22, 0.784 |
| Replay, held-out: recall@5 / MRR | 8/13, 0.427 | 8/13, 0.427 |

38 of 40 main and 13 of 15 held-out answers are word for word the same; Q22, T8 and H12 differ in wording
only. Largest prompt 3,101 tokens, no truncation. **H11 is the one real change, and our table change caused it:**
Linearized answered from the cover page ("151 W. 42nd Street, New York, New York 10036", rank 2), not from the
expected Item 2 sentence (rank 16-17 on both). NDAQ's cover-page address is one of the five fallback tables;
as text rows instead of a pipe table its chunk embeds differently and left the top 5, and the model, given no
address, produced prompt v1's malformed "The unit is not stated." So the held-out loss is a lucky source lost,
not the expected source ranked lower - retrieval of every expected figure is identical. Retrieval, not the
answer step, is the held-out bottleneck on every strategy: H6, H10, H11, H13 and H14 are outside the top 5
everywhere (Markdown's MRR 0.356, the linearized two 0.427) - what 1b, 1d and hybrid search target.
`replay_recall.py` now scores H1-H15 too (`expect_heldout`; H4 at the row holding both inputs).
**Decision (user, 2026-09-28): H11 accepted** - Linearized's answer came from a lucky secondary source, the
expected one reaches the top 5 on neither, and 1b-ii's filing profile targets exactly this question. The
no-regression bar stays; this one is recorded as explained, not ignored.

Small app fix noted: the app doesn't set
`Console.OutputEncoding`, so redirected logs carry the console code page for non-ASCII characters
("Management�s", a non-breaking space as 0xFF); the grader now reads logs with `errors='replace'`.

**Decisions (user, 2026-09-25, still in force):** v1 first, v2 on a branch; each step measured, stop on bad
numbers; the Q1-Q24 no-regression bar - now the strict 22/24 reliable (review point 5).

**First plan (superseded 2026-09-25, kept for the record):**

> **Why.** The hard statement filter can't reach answers outside the primary statements (segment and
> regional figures, policies, MD&A drivers), and pure RAG over tables is the weakest way to answer headline
> figures. The filings carry structure that addresses both, checked 2026-09-25 in all four:
> - **Section-level tags.** Each filing tags 67-91 blocks of text (notes, policies, schedules) as inline-XBRL
>   text blocks, ~75% under standard `us-gaap` names shared across filers - e.g. all four tag their segment
>   table `us-gaap:ScheduleOfSegmentReportingInformationBySegmentTextBlock`; three tag the revenue policy
>   `RevenueFromContractWithCustomerPolicyTextBlock`, NFLX `RevenueRecognitionPolicyTextBlock`. That labels
>   note topics filer-independently - including ORCL's unnumbered notes, which heading patterns can't.
> - **Tagged facts.** Headline figures are standard concepts; the linearizer already pairs each tagged value
>   with its row label, column label and concept (that's how the XBRL oracle works), so a label -> concept
>   mapping can be derived per filing instead of hand-written. Segment figures are dimensional, with
>   company-specific members (`msft:IntelligentCloudMember`, `ndaq:CapitalAccessPlatformsMember`, ...), whose
>   names can be turned into labels.
>
> This doesn't reverse the "linearized tables" decision to keep XBRL out of runtime: that was about aligning
> table columns (circular with the oracle) and about a general question -> concept lookup. Text-block labels
> are a separate use; the figure lookup (phase B) is the text-to-query problem that entry flagged, now scoped.
>
> **Decisions (user, 2026-09-25):**
> - **v1 first.** Manual pass and push of the current state; v2 on a branch.
> - **Phased, stop on bad numbers.** Each phase is spiked and measured (replay, both strategies, Q1-Q24 as the
>   no-regression bar) before it's built; stop after any phase whose numbers don't justify the next.
> - **Section labels as a setting on both strategies**, not a third chunking strategy - so a gain is
>   attributable to the labels, not to the chunking.
>
> **Phases:**
> - **A - XBRL section labels on chunks**, used for routing (e.g. "segment" -> the segment text block).
>   Main unknown: carrying HTML text-block regions through markitdown onto chunks.
> - **B - figure lookup from tagged facts** for headline statement items, passed to the model as cited
>   context. Unknowns: period resolution per fiscal-year end, dimensional (segment) facts.
> - **C - router** between B and retrieval; fallback to retrieval when no fact matches. Carries today's
>   misrouting risk, so measured like the statement filter.

## Follow-up: manual pass (v1) — DONE, outcome below

**What.** The user's manual pass through `docs/Manual-Test-Questions.md` (2026-09-25), Markdown strategy,
`llama3.1:8b`, temperature 0, after the pre-manual-pass fixes - each answer pasted and graded against the doc's
expected figure, filter line and citation, plus ~25 variants the user tried.

**Grading (user's decisions).** Two columns: **Correct** - the number matches the line the answer names (a right
number under a near-identical line's name is wrong); **Complete** - unit stated, period clear. Declines are a third
outcome. (An interim rule scored a missing unit as a plain fail; replaced because it lumped a presentation gap
with a genuinely wrong number.)

**Results:**

| Set | Correct | Complete | Reliable (both) | Declined | Wrong |
|---|---|---|---|---|---|
| Main (Q1-Q24) | 23/24 | 17/24 | 16/24 | 0 (Q15, Q16 negatives declined correctly) | Q10 |
| Targeted (T1-T10) | 3/10 | 2/10 | 2/10 | 6 | T5 |
| Routing (R1-R3) | all declined as expected, no substituted figure | | | 3 | 0 |

By company (main): MSFT 6/6, ORCL 4/4 reliable; NDAQ 4/5 correct, 3/5 reliable; NFLX 6/6 correct, 1/6 reliable.

**Findings (each checked against the chunks or the index, not assumed):**
- **Grounding holds.** Every negative - including real years absent from the filing (MSFT and ORCL fiscal 2023),
  which the model may know from training - and every routing test declined; R1/R2 declined with lookalike figures
  (income-statement revenue, segment revenue) in context.
- **Units were the main presentation gap:** missing on 7 of 24 main answers, all NDAQ/NFLX. Two causes: the unit
  was in the same chunk and dropped (Q17, Q19 - NFLX revenue and total assets sit on the statement's first piece,
  under "(in thousands, ...)"), or the chunk had no units line at all (Q18 key 859, Q20 key 867 - continuation
  pieces; only a statement's first piece carries units). "Add the units after the number" in the question fixed
  both, carrying "thousands" across excerpts when the statement's first piece was also retrieved (Q20 variant).
- **Two wrong figures, both near-identical lines:** Q10 stated $2,114M ("attributable to Nasdaq", the only line
  printed with "$") as total comprehensive income ($2,113M) - reproduced for 2024 ($942M vs $940M). T5 answered
  the $19,100M "U.S. government and agency securities, fair value" row from another table - a retrieval miss plus
  a lookalike label, stated with a detailed citation; the same trap as the 2026-09-24 run.
- **Q3 (NDAQ total liabilities)** first presented a subtotal sum ($18,118) as "total liabilities", then the right
  $18,821 - reproducible. Retrieval was right (the total's chunk ranked 1st); the total sits on an untitled
  continuation piece and the model started from the titled piece.
- **Targeted declines are mostly retrieval:** T2, T3, T6, T8, T9 had the answer's chunk at rank 6-25+ on Markdown;
  T1's row was in context and missed. T4 (Markdown's measured win over Linearized) and T7 passed.
- **Arithmetic is unreliable:** asked to sum three 8-digit operating cash flows, the model gave 24,785,938 and
  24,785,038 across four phrasings, never 24,784,938 - "add the numbers correctly" changed nothing. Q14's 6-digit
  difference was right. Derived figures need code (a calculator tool - v2).
- **Presentation habits:** citations by excerpt number only ("excerpt [1]", no filing/section - three of six
  headquarters variants), doubled citations, pasted pipe-table rows, the section paraphrased. Brevity instructions
  worked for "just the location", not for "just the square feet".
- **Problems follow filing layout, not onboarding history:** NDAQ was an original filing and still had the most
  issues; MSFT/ORCL share a filing-agent style, NDAQ and NFLX each differ (statements after Item 16, near-identical
  total lines, thousands).
- **Speed:** a cold question takes ~75 s (~52 s reading ~2,400 prompt tokens at ~45 tokens/s, ~20 s writing);
  repeats and overlapping questions are fast because Ollama keeps an 8 GB RAM prompt cache (up to 41 full prefix
  matches in one day). No conversation history is sent between questions.
- **Padding:** empty table cells are ~30-63% of statement-chunk tokens per filing (character-based estimate) -
  reading time and noise.

**Prompt change (shipped).** A new `SystemPrompt` answering the findings above - unit right after every figure,
the line named exactly, one filing + section citation, no pasted table rows, no unrequested arithmetic, no
unrelated figures in a decline - plus unnumbered excerpt labels ("--- Excerpt from <filing>, section <heading>
---"; the old "[n] Source: ..." label was being copied as a citation). Three variants (V1-V3) were added to the
question set. Re-run on all 40 questions, both strategies, temperature 0:

| Prompt v1 | Markdown | Linearized |
|---|---|---|
| Main (Q1-Q24), reliable | 22/24 - misses Q10, Q15 | 22/24 - misses Q2, Q10 |
| Targeted (T1-T10), correct and complete | 4/10 (T1, T4, T7, T10) | 5/10 (T1, T2, T7, T8, T10) |
| Wrong figures, all 40 | 5 (Q10, T5, T9, V1, V3) | 5 (Q2, Q10, T9, V1, V3) |
| Routing (R1-R3) | all declined | all declined |
| Time, 40 questions | 2,459 s | 2,056 s |

All seven unit failures of the pass were fixed on both; Q3's subtotal answer was gone. New failures: the
units rule leaked into declines as the whole answer ("The unit for the figures is not stated." - Q15, T2 on
Markdown; T3, T4 on Linearized); T9 answered "$0" from a line that wasn't in context. Q2 on Linearized named
"Total Oracle Corporation stockholders' equity" ($42,508M) as total stockholders' equity ($43,056M). Largest
prompt 3,103 tokens, + 768 output = 3,871 of 4,096 - no truncation; `AppSettingsTests` updated from ~3,000.

**Two further prompt revisions - tried 2026-09-28, not kept.** Measured on Markdown only:

| | v1 (shipped) | v2 | v3 |
|---|---|---|---|
| Main, reliable | 22/24 | 20/24 | 23/24 |
| Targeted, correct and complete | 4/10 | 4/10 | 2/10 |
| Wrong figures, all 40 | 5 | 5 | 6 |
| Largest prompt + output, of 4,096 | 3,871 | 3,941 | 3,984 |

- **v2** added a fixed decline form ("The excerpts don't contain <what was asked>."), "a missing line is not
  zero", and "quote the row label exactly". Declines became clean one-liners and T9 stopped answering "$0", but Q3
  became "$18,821 thousand" (NDAQ reports millions - the prompt's only unit example was "thousand"), Q18/Q21 lost
  their units, R2 invented a calculation (-$4,709M), and V3 refused the sum it was asked for. Q10 was unchanged.
- **v3** gave one example per unit and split the arithmetic rule (calculate only when asked; never calculate a
  figure asked for by name). Every main-set unit was right, but T1 declined (v1 and v2 answered it), T10 became
  "$(96,795) million" (NFLX reports thousands), R2 named a revenue line as operating income, and six main answers
  dropped the period.
- **Why v1 stays (user's decision).** Each revision fixed what it targeted and moved failures elsewhere, even at
  temperature 0 - `llama3.1:8b` is sensitive to prompt wording, and every round tuned against the same 40
  questions. The failures left aren't prompt-shaped: near-identical rows (Q10, V1), a chunk without its units line
  (NFLX continuation pieces), arithmetic (V3), keyword routing (R1-R3). They belong to v2's design (XBRL facts with
  unit and scale, a calculator tool, hybrid search) - "XBRL hybrid (v2)" above.

**Default strategy: `Markdown` stays (user's decision).** The agreed rule was to switch to Linearized only if it
didn't regress anywhere, T4 included. It ties on the main set, answers one more targeted question and runs ~16%
faster, but regressed on T4 (declined - operating and finance lease rows mixed in one chunk) and Q2 (the
lookalike equity line), both of which Markdown answers. Both strategies stay switchable.

## Follow-up: evaluation in .NET (v3) — PLANNED

**Why (user, 2026-10-02, after `v2.0`).** The evaluation is what made every v1 and v2 decision measurable, and it is
still a set of Python scripts (`tools/grade_answers.py`, `tools/replay_recall.py`) run by hand over console logs, with
the results kept as folders in `eval/`. v3 moves it into the .NET stack with Microsoft's evaluation libraries, built on
the same `Microsoft.Extensions.AI` abstractions the app uses. The app and its defaults don't change; the measured
results must come out the same.

**What the libraries offer, checked 2026-10-02** (Microsoft Learn, "The Microsoft.Extensions.AI.Evaluation libraries",
and NuGet):
- **Versions:** `Microsoft.Extensions.AI.Evaluation`, `.Reporting`, `.Quality` and the `.Console` tool are all **10.10.0
  stable** - the same release as the app's `Microsoft.Extensions.AI` 10.10.0. (Learn's API pages still showed 10.9.0.)
- **Custom evaluators** (`IEvaluator`): any scoring logic, deterministic or not, returning metrics with an
  interpretation (pass/fail). **The fit for this project:** the strict grader and the retrieval rank are exactly that.
- **Reporting** (`DiskBasedReportingConfiguration`): each question a scenario, each configuration an execution, results
  stored on disk, and an HTML report from the `aieval` dotnet tool comparing executions - what `eval/` does by hand.
- **Response caching:** re-running an evaluation reuses the model's responses while the request (prompt, model) is
  unchanged - a grader change no longer needs a two-hour model run. Caveat from the API docs: cache keys "are not
  guaranteed to be stable across releases of the library", so a package upgrade can silently force fresh responses.
- **Quality evaluators** (Groundedness, Relevance, Completeness, Equivalence, Retrieval, ...): an LLM judge scoring 1-5.
  The docs warn their prompts were tuned against GPT-4o and that results "can be especially poor when a smaller / local
  model is used". Here the judge would be `llama3.1:8b` - the model that wrote the answers - and five evaluators over
  102 questions is roughly 8 hours of CPU. A 1-5 relevance score also doesn't fit questions with one right figure.
- **NLP evaluators** (BLEU, GLEU, F1): word overlap - "$26,445 million" and "$27,034 million" score almost the same.
  Not useful for figure answers.
- **Safety evaluators:** need the Azure AI Foundry Evaluation service - not zero-cost; excluded.

**Decisions (user, 2026-10-02):**
- v3 is this, on a branch `v3`, each step measured before the next, as v2 was. Local and zero-cost stays.
- The Python tools stay until the .NET evaluators reproduce them exactly; then they're retired, not before.

**Steps:**
1. **Project and packages.** Where the evaluation lives - a separate NUnit project whose tests are `[Explicit]` (they
   need Ollama and hours), so a plain `dotnet test` stays offline and fast - is decided here. `Evaluation` and
   `.Reporting` 10.10.0 added, `dotnet list package --vulnerable --include-transitive` run after.
2. **`StrictFigureEvaluator`**, `grade_answers.py` ported: expected figure, unit, exact line, traps, `exact`, clean
   declines, `check` for a reader. **Bar: the same grade on all 1,029 graded answers in `eval/`'s 34 graded runs**
   (v1, every v2 step, both granite models; logs from before and after the console encoding fix) - offline, from the
   logged answers, against the grader's own status (not a reader's later resolution). Any difference is a bug in the
   port, found before anything else is built on it - the tokenizer's lesson (73,125 pairs, step 2b).
3. **`RetrievalRankEvaluator`:** the rank of the expected figure among the chunks the app actually retrieved, read from
   `RagAnswer.RetrievedChunks` in-process - no log replay, so `replay_recall.py`'s mirrored constants (a live
   constraint) are no longer needed for it. **Bar: the same rank as `replay_recall.py` on all 102 questions** under the
   default settings (query embeddings only; no chat model).
4. **The runner and the report.** One execution per configuration (e.g. `structured-hybrid`), one scenario per question
   (named by set, e.g. `A.A10`, which the report groups), the app's chat client taken from the reporting configuration
   so responses are cached, an HTML report from `aieval`. **Bar: a full run on the defaults reproduces
   `eval/structured-5a/` and `eval/answer-side-norerank/` grade for grade** (temperature 0; the unexplained wording
   drift of T8/T10 noted in "Answer side: what the model was given" is wording only and grades the same).
5. **Optional - a grounding evaluator:** every figure an answer states appears in its context (step 3b's check,
   measured offline in v2, never built).
6. **Optional - a local-judge spike:** do the Quality evaluators, judged by `llama3.1:8b`, agree with the strict grader?
   Measured on answers already graded (contexts rebuilt by the same retrieval), not assumed from the docs either way.
   Agreement, CPU time per answer, and where they disagree.

**Risks noted:** cache-key instability across library versions (above); the evaluation must call the app exactly as the
console does (`RagAnswerService`, same settings), or it measures something else; reporting stores responses and results
on disk - where, and whether any of it is committed, is decided in step 4.

**Step 1 done (2026-10-02): the project.** `RagFilingExplorer.Local.Evaluation` (user's choice of three: a separate NUnit
project, over the existing test project or a console runner), in the solution; the app references no evaluation
package - its only change is an `InternalsVisibleTo` grant. Packages `Microsoft.Extensions.AI.Evaluation` and
`.Reporting` 10.10.0, checked before adding: published by Microsoft, no OSV advisories, and their only dependencies are
`Microsoft.Extensions.AI(.Abstractions)` 10.10.0, which the app already uses. `dotnet list package --vulnerable
--include-transitive`: none, for the app and the new project. Two setup tests (the app's internals visible; a disk-based
reporting configuration built offline, no model). `dotnet test` runs both projects offline - 372 + 2, ~10 s.

**Step 2 done (2026-10-02): the strict grader in .NET - 1,029 of 1,029.** `Grading/StrictGrader` ports
`grade_answers.py`'s `grade()` rule for rule; `Evaluators/StrictFigureEvaluator` wraps it as an `IEvaluator` (one string
metric, "Strict grade": the status as value, the note as reason; reliable and decline-ok Good, check Inconclusive,
the rest failed), the expected answer passed as an `EvaluationContext`. `ExpectedAnswer` loads
`tools/expected-answers.json`.
- **The oracle, checked first.** The stored grades in `eval/` were written over two weeks while the grader changed; regraded
  with today's `grade_answers.py`, 1,018 of 1,029 matched as stored and 11 didn't - each a reader's resolution written
  over the status, the grader's own verdict kept in a `graded` field (H2's "19.4%", H10, T9, Q15 ...). Reading `graded`
  where present: **1,029 of 1,029**. So the committed files are the oracle - no new fixture.
- **`GraderParityTests`:** every grading in `eval/` (any JSON whose results carry id, status, note and answer - 34 runs),
  one test case per run, status *and* note compared, offline, in every `dotnet test`. **All 34 pass: identical on 1,029
  answers.** Checked that it can fail: changing one note's wording ("trap" -> "trap:") failed 29 of the 34 runs, each
  naming the answer and both grades.
- **One place the languages differ, handled:** Python's `round(x, n)` rounds the double's exact binary value half to
  even (55.85 is stored as 55.8500000000000014 -> 55.9); `Math.Round` scales by a power of ten first and can disagree at
  such points. `PythonRound` does the exact-value rounding with `BigInteger`; six cases pinned against Python's own
  output. The regexes needed no translation (Unicode `\d`/`\w`/`\b` and `$` behave alike); `re.match` became `\G`.
- **Not ported:** `answers_from_log` (splitting a console log) - step 4 takes answers from the app in-process.
- **Noted for step 4:** the reporting library's cached responses expire after 14 days by default (Microsoft's
  tutorial), so a long-lived baseline needs `timeToLiveForCacheEntries` set or a fresh run.
46 evaluation tests (2 setup, 34 parity, 6 rounding, 3 evaluator, 1 coverage) + 372 unit tests.

**Step 3 done (2026-10-02): the retrieval rank in .NET - 102 of 102, in-process.** Three parts:
- **3a - one list of chunk expectations.** `replay_recall.py` kept what marks each answer in a chunk ("(601)", every input
  of an arithmetic question) as three dictionaries keyed by line number; they're now `chunk_expect` on each question in
  `tools/expected-answers.json`, which the replay reads by question text. Replay output **byte-identical** before and
  after on five logs (v2's three baselines, a v1 Markdown run, a reranked run).
- **3b - the app's wiring in one place.** `AppComposition` (in the app) holds what `Program.cs` did inline - find the
  filings, register companies, Ollama clients, open the collection, keyword index, reranker, thinking check, answer
  service - so the evaluation opens the app exactly as the console does (`OpenExistingIndexAsync`: never builds an
  index; a missing or stale one is the console's own startup error). Building stays in `Program.cs`. Behaviour-neutral,
  checked by repeating the day's smoke run: startup lines, filters and all 25 ranked chunks with their scores identical,
  R1's answer word for word.
- **3c - `Evaluators/RetrievalRankEvaluator`:** `rank_of()` ported (`RankOf`: the first chunk holding each
  `chunk_expect` string, the last of them; null if one is missing), as an `IEvaluator` - a numeric metric "Answer rank",
  passed within `Retrieval:GenerationTopK` (read from the settings, not repeated). **`RetrievalParityTests`** (`[Explicit]`:
  Ollama and the index) asks all 102 questions in-process with the shipped settings and a chat client that throws if
  called, and compares with the replay's output on the v2 baselines (committed: `eval/v3-retrieval-parity/`): **every
  rank and every first chunk's fused score (to the replay's four decimals) identical**, main, held-out and answer-side.
  An off-by-one in `RankOf` fails all three, question by question. (The replay labels main-set lines by position - its
  "Q14" is Q17 - so lines are matched, not labels.)
- **Temperature 0 is no longer word-for-word repeatable here.** The smoke run's Q1, with retrieval - so the prompt -
  identical and the model unloaded before each run, came back in the other of two wordings it has given (this time
  word for word the `structured-5a` answer of 2026-09-30). That is what the morning's unexplained T8/T10 drift was. The
  figures and grades don't move; the wording does. "A changed answer means a changed input", relied on since v1, now
  holds for grades, not for text - and step 4's bar is grades.
53 evaluation tests offline (+ the explicit parity check) + 372 unit tests.

**Step 4 done (2026-10-02): the evaluation run, stored and reported - v2 reproduced, one model variation.**
`Running/EvaluationRunner` asks every question through the app (`AppComposition`, shipped settings: Structured, Hybrid,
no reranking), one scenario per question (`Main.Q1`, `HeldOut.H16`, `AnswerSide.A10`), the answer through that
scenario's caching chat client (the service built once; a delegating client switched per scenario), graded and ranked by
both evaluators, stored with `DiskBasedReportingConfiguration` under `eval/v3-runs/` (results committed; the response
cache gitignored - its keys aren't stable across library versions), cache entries kept a year (the library's default is
14 days), and `report-<execution>.html` written from the stored results with `HtmlReportWriter` - **the user's choice
over the `aieval` tool** (one package less, the report written by the run that made the results; `aieval` to be tried
if the report isn't liked). `EvaluationRunTests` [Explicit] runs it; `EVAL_EXECUTION` names a run, `EVAL_ONLY` limits it.
- **Smoke run** (Q1, R1, A10): grades and ranks as the baselines, 3.5 min; repeated from the cache: 10 s, identical.
- **Full run, `structured-hybrid-v3-baseline`:** the tool's two-hour limit stopped it after 101 of 102 (A27 missing); not
  restarted automatically. Re-run with the same execution name (user): the 101 answered from the cache, A27 asked -
  **1 min 47 s**. Main **33/40**, held-out **31/35**, answer-side **20/27**; the answer in the top 5 for 36, 28 and 22 -
  the hybrid replay's numbers exactly. **101 of 102 grades equal the v2 baselines.** (The summary's "2 min" is the
  re-run's time; the first pass took about two hours.)
- **A16 differs, and it isn't wording:** the baseline answered "$616 million" (reliable); this run, with retrieval and
  so the prompt identical (the answer chunk at rank 18 in both), added "an additional $4 million of accrued excise tax
  ... the total cash spent ... is $620 million" - a listed trap, so `check`. Temperature 0 on this setup varies the
  content of an answer, not only its wording (step 3's finding was too mild).
- *Decision (user, 2026-10-02): option 2 of three - count the evaluation as reproduced and record A16 as model
  variation,* over keeping the bar strict (a hunt inside Ollama) or measuring the variation first (two hours; worth doing
  before comparing models). Every difference comes from the model's answer: the evaluators are held exact by the parity
  tests (1,029 grades, 102 ranks). So `EvaluationRunTests` now reports a difference from the baselines as an NUnit
  warning, listed in the summary, not a failure.

**The project's history in the report (2026-10-02, user's question: "how can we add the other runs?").** Only v3 runs
were in the store, so the report showed one execution. `Running/HistoricRunImporter` brings the **main line** (user's
choice over adding the five side experiments) into it: v1's baseline and each kept v2 step, each an execution dated when
it ran, each answer a scenario named as the v3 run names it, re-graded by `StrictFigureEvaluator` - the import stops if
a grade differs from the one the run was given; none did, on all 749 answers. Sets of one configuration run on different
days are one execution (step 2: main + the 35-question held-out run; 5a: + `answer-side-norerank`; 2b: + `answer-side-baseline`).
**Strict grade only, no rank:** the rank needs the chunks each run retrieved, and those indexes were rebuilt as v2 went
on - a replay against today's index would give precise-looking wrong numbers. `HistoricRunImportTests` [Explicit]
(offline; it writes into `eval/v3-runs/`) runs the import; `report.html` is written from every execution - by the
import and by every evaluation run from now on - next to each run's own `report-<execution>.html`.

**Step 5 reshaped (user, 2026-10-02): which excerpt the model's answer came from - done 2026-10-05, outcome below.** The
report shows the grade and where the right answer ranked, not what the model read its figure from - which is how today's
misreadings (A10's declared $27,034M over the cash flow statement's $26,445M) were found, by hand. The planned grounding
evaluator does it for every question:
- **Input:** the five excerpts the model was given - the top `GenerationTopK` of the retrieved list, exactly the prompt's
  - and the answer.
- **For each figure the answer states** (the strict grader's `StatedFigures`: years, citation text and the question's own
  numbers skipped): every excerpt holding it - its position, statement type and section - and the line it sits on. A
  figure in two excerpts lists both; which one the model read can't be known.
- **A figure in none of them** is flagged - step 3b's check, never built: an invented number, or an asked-for calculation
  (A21-A27, which list their inputs in `chunk_expect`, so the two are told apart).
- **No figure stated** (a text answer - "Austin" - or a decline): reported as such; matching the expected text instead is
  an option to decide then.
- **Only for runs of the new evaluation** - the imported v1/v2 runs have no excerpts. `structured-hybrid-v3-baseline` gets
  it from a re-run with the same execution name: answers from the cache, retrieval recomputed, about two minutes, no
  model call.
Bar to set when it's built: on the v3 baseline, A10 traced to its equity-statement line, and no flagged figure that
isn't an asked-for calculation or a real invention (v2's step 3b replay found none among 7 wrong answers).

**Step 5 done (2026-10-05): which excerpt each answer's figure came from - bar met.** `Evaluators/FigureSourceEvaluator`,
a third evaluator in every run, one string metric "Figure source":
- **What it reads:** the five excerpts the prompt held (`RetrievedChunks.Take(GenerationTopK)`) and the answer's figures -
  the strict grader's `StatedFigures` (now `internal`, unchanged: GraderParityTests still 1,029/1,029), **less the day of
  a date**. "December 31" made 31 a figure in 34 of the 102 baseline answers, found in nearly every excerpt - the grader
  keeps it (grade_answers.py's rule), only the trace drops a 1-2 digit number right after a month name. Every other small
  number left is a real figure (H2's 19%, T3's 16%, H4's 9%, R3's 30%).
- **What it reports:** per figure, every excerpt holding it - position in the prompt, statement type, section - and the
  first line holding it (+N more), a long line shown as a 160-character window around the figure (a narrative paragraph
  is one line; cut from its start, A11's, H13's and H34's figures fell off the end). Numbers match as the grader
  tokenizes them, so "(26,445)" holds 26,445; a figure restated in other units isn't found.
- **Statuses:** `traced` (every figure in an excerpt); `calculated` (a figure in none, on a question whose chunk_expect
  lists two or more inputs and not the expected figure - H4, A21-A27); `untraced` (a figure in none otherwise - failed,
  listed in the run summary); `no figure stated` (a text answer or a decline, not matched against the expected text -
  user, 2026-10-05). V3 ("sum up the numbers") marks one figure, so a computed total there would show as untraced.
- **Excerpt metadata:** `RetrievedChunksContext` now carries `RetrievedExcerpt`s (filing, heading, statement type,
  content); the stored context is still each chunk's text.

**The v3 baseline, re-run under its own name** (answers from the cache, retrieval recomputed, 19 s): all 102 stored
results identical to the committed ones but for the new metric - same answers, grades, ranks. Main 34 traced / 6 no
figure; held-out 29 / 6; answer side 19 traced, 7 calculated, 1 no figure. **No untraced figure.** A10's $27,034M traced
to excerpt 1, the equity statement's "Retained earnings > Common stock cash dividends" (dividends declared) - the bar. Every
wrong answer's figure is in its excerpts - v2's step 3b finding, now from every run: A14 and H34 (MD&A sentences), A8 and
A11 (MD&A dividend paragraph), A15 (a stockholders' equity note's total), H13 (the next column of the same row), T1 and T9
(a lookalike line in the right statement), A16's computed 620 (616 + 4) also printed in the equity statement (excerpt 5).
A27's wrong 25.9% is `calculated`, its inputs traced. 70 offline evaluation tests (17 new).

**Variance measurement - planned and built, run pending (2026-10-05; user: run at the end of the day).** Since step 3,
temperature 0 hasn't repeated word for word (Q1's two wordings), and step 4's A16 changed *content* - an unrequested
"$620 million" - with retrieval, so the prompt, identical. Before any model or prompt comparison, how much an answer moves
on its own has to be known: otherwise a one-grade difference can't be read as a change.
- **Harness:** `EVAL_NO_CACHE=1` asks the model afresh and caches nothing (the cache is keyed by scenario, so a new
  execution would otherwise replay the baseline); `EVAL_SETS` limits a run to sets; `EVAL_UNLOAD=1` unloads the chat model
  before every question (OllamaSharp's `RequestModelUnloadAsync`), so no question starts from a prompt prefix Ollama still
  holds. `Running/VarianceComparison` reads any stored executions and classes each question by its worst pair: identical,
  wording (same figures and grade), figures (a figure added or dropped, same grade) or grade; a curly apostrophe reads as a
  straight one (runs logged before the 2026-10-01 encoding fix have only straight ones) and a date's day isn't a figure
  (the figure trace's rule, shared). `tools/run-variance.ps1` builds once, runs the passes with `--no-build`, then the
  comparison into `eval/v3-runs/<prefix>.txt`; started detached, past the two-hour tool limit. Checked without the model:
  a zero-pass run (build, comparison, logs) and its failure path.
- **A first, free data point** (`eval/v3-runs/variance-v2-5a-vs-v3-baseline.txt`): the same configuration on 2026-09-30
  (main, held-out) / 2026-10-02 morning (answer side) against the v3 baseline (2026-10-02 evening): **89/102 identical,
  11 wording, 1 figures, 1 grade** (A16). The figures case is A27's wrong percentage, 25.8% then 25.9% - an asked-for
  calculation that came out differently. 9 of the 11 wording changes are answer-side (27 questions), 2 main + held-out (75).
- **How it will be read:** a question whose grade differs in any pass is *unstable*; their count per set is the noise
  floor - a later comparison that moves fewer grades than that on a set isn't evidence of a change, and grades are
  compared on the stable questions. If unload passes repeat word for word where loaded ones don't, prefix reuse is the
  cause, and the evaluation could unload to get repeatability back (at a reload per question).
- **Open, for the user at run time:** scope (recommended: all 102, two passes, ~4 h) and whether to add unload passes
  (two are needed to say whether they repeat). A smoke run first (`-Passes 1 -Only Q1,A16 -Prefix variance-smoke`, its
  results folder deleted after).

**Report order reversed: newest first (user, 2026-10-05).** Opened in a new tab, `report.html` showed v1's 55 questions -
no answer side, no H16-H35, no rank or figure source - and looked like it had lost them. Nothing was lost (821 results, 12
executions, data identical to Friday's but for step 5's metric): the report opens on the first execution in its data and
lists the picker in that same order, both fixed by `HtmlReportWriter`'s input, with no URL parameter, and remembers a pick
only in that browser tab (sessionStorage). Oldest first (2026-10-02) therefore opened every new tab on v1. Now newest
first - it opens on the latest run; the History tab and the Comparison defaults sort runs by date themselves, unchanged.
`ReportWriteTests` rewrites `report.html` from the stored results without a model call. Checked by rendering it headless
(Edge `--dump-dom`): opens on `structured-hybrid-v3-baseline`, 102 cases.

**Step 6 - the local-judge spike: designed and built, run pending (2026-10-05; user: after the variance run, same night).**
What the Quality package (10.10.0, read from the package itself) means here:
- **Which evaluators map onto a metric we have:** Equivalence (answer vs a ground truth) against the strict grade;
  Groundedness (answer vs the excerpts) against the figure source; Retrieval (excerpt relevance and order) against the
  answer rank; Completeness overlaps Equivalence. Relevance, Fluency, Coherence score style, not figures; the other four
  are agent-focused or experimental. **Chosen (user):** Equivalence first (phase A, ~25 s a question, ~45 min for 102),
  Groundedness (~2 min a question) only if A is worth following; Retrieval not - the rank already measures it exactly.
  **Judge (user):** `llama3.1:8b`, the model that wrote the answers - self-judging, stated with the result.
- **The context window:** the Groundedness prompt with our five excerpts is ~4.5k tokens, Retrieval's ~6k - past
  Ollama's default 4,096, where the oldest tokens (the judge's instructions) would be dropped silently. No evaluator sets
  it, so `JudgeContextChatClient` adds `num_ctx` 8,192 (`OllamaOption.NumCtx`) - only while the evaluators run, so the
  app's answers keep the default (a variance pass must measure the app). It sits inside the response cache: checked on
  two questions that the baseline's answers are still cache hits, so a judge run judges exactly the baseline's answers.
- **Output format:** Equivalence asks for a bare 1-5 integer; the others for `<S0>` thoughts, `<S1>` explanation, `<S2>`
  score. A reply the library can't read is an error diagnostic - counted in the report, part of what's measured.
- **Ground truth:** built from `expected-answers.json` - "$26,445 million", "$3.64", "19%", a fact's strings, a decline
  for a negative. A routing test's is its figure; the strict grade also passes a decline there, so those disagreements
  are by design and marked.
- **How it will be read:** agreement with the strict grade at the library's own verdict (a metric interpreted as
  failed), every score tabulated against strict pass/fail so no threshold is chosen afterwards, every disagreement listed
  with its answer and ground truth, unread replies counted, judge time per call. Expectation stated now: step 5 found
  every wrong answer is a misreading of a figure in its context, so Groundedness should rate those grounded - it can't
  see this project's failures; whether Equivalence catches a wrong figure ($27,034M vs $26,445M) is the open question.
- **Built:** `Judging/JudgeSetup` (evaluators, contexts, ground truth, the context client), `Judging/JudgeAgreement`
  (the report), `EVAL_JUDGE` on the run, `tools/run-judge.ps1`; 15 offline tests.

**Step 6 smoke test (2026-10-05, Q1 and A10):** `llama3.1:8b` doesn't reply in Equivalence's format - asked for a bare
integer, it wrote a sentence both times ("...Therefore, the Equivalence score is 4."; "...I would rate the Equivalence
metric as 4 stars."), so the library read no score and recorded an error. Run as shipped, phase A would most likely
measure only that. **Decided (user): count both** - the agreement report keeps the library's reading (unread replies
counted), and also recovers the score from the reply's words (`JudgeSetup.RecoverScore`: "score is N", "as N", "N stars",
"N/5", the last phrase wins), labelled as recovered. A recovered score's verdict is asked of the library itself - the
evaluator run against a stub client replying with exactly that score - so it gets the verdict a readable reply would;
pinned by a test (10.10.0: 1-3 fail, 4-5 pass, both evaluators). The smoke run already shows the content problem: A10's
$27,034M against the expected $26,445M - "a slight difference in the amount" - scored 4, a pass for a wrong figure.
~900 prompt tokens, 8-34 s a call. 102 offline evaluation tests.

**The report explains a failed answer (user, 2026-10-05).** A10's report showed only the grader's note "trap 27,034" - the
rest of the story was spread over three metrics. Now, deterministically, without changing a grade:
- **`trap_why`** in `tools/expected-answers.json`: what each of the 137 traps (70 questions) is - "dividends declared
  (equity statement), not paid (cash flow statement)", "FY2025", "basic". 82 extracted from `docs/Manual-Test-Questions.md`'s
  tables, the main/targeted/variant sets' from its prose, A21-A27's "an input of the asked-for calculation"; six the doc
  doesn't explain from the filing line they sit on (H3 11,974 FY2025; H6 2,805 R&D's stock-based compensation, 9,860
  FY2025; T5 41,142 total fair value in the unrealized-losses table; T8 85.47 the full year's average) and Q3's 18,118
  from this log (a subtotal sum the model produced - not in the filing). Inserted as text, keeping the file's layout;
  every other field checked unchanged; `grade_answers.py` re-grades `structured-5a/main.log` identically (40/40).
  A test fails if a trap is added without its reason.
- **Strict grade's verdict in words** (`StrictGradeExplanation`): "States 27,034 (...) instead of the expected 26,445
  million."; the answer's own figures first ("States 25.9 instead of the expected 25.4%; also states 17,087 (an input ...)");
  declines, missing units, lookalikes each worded. The metric's reason stays the grader's note (parity).
- **Where the expected figure was** (Figure source, for an answer that doesn't pass): its excerpt and line - a
  misreading; "not printed as such - derived from <chunk_expect markers> (excerpt N)" - a calculation (V3, A27), never a
  retrieval miss when its inputs were given; "retrieval missed it" only when the markers aren't in the excerpts - on the
  baseline exactly A11, H25, H29, H34, R3, the answers ranked outside the top 5.
Baseline refreshed from the cache: answers, grades, ranks unchanged. 117 offline evaluation tests.

**Report layout: "Why this score?" and Metadata, no "What this measures?" (user, 2026-10-05).** The report shows a
metric's interpretation reason under "Why this score?"/"Why this failed?", its own reason under "What this measures?", and
its metadata as a Name/Value table (read from the report's code). The trace, the grader's note and the rank sentence were
reasons, so they showed as "What this measures?" - a misleading label - and A10's figure source read as two unlabelled
lines. Now every evaluator's reason is a fixed description of what it measures (user: the section is the report's own -
fill it with what its label says), the explanation is the interpretation's reason (Figure source: "A
misreading - every figure is in an excerpt: 27,034 from excerpt 1 (equity_statement); the expected 26,445 was in excerpt
3 (cash_flow_statement)."), the details are metadata ("Stated 27,034", "Expected 26,445", "Grader note" - word for word,
parity unaffected). Backward compatible: the stored format is the library's (metadata was always there, empty), the
cache holds only answers, and comparisons read values, not reasons; the two readers of the old reason
(`StrictFigureEvaluator.GraderNote`, `FigureSourceEvaluator.TraceText`) read metadata first and fall back to the reason
unless it is the description, so the imported v1/v2 runs still read (they keep the old layout on screen). Baseline
refreshed: 102 results, answers, grades, notes, ranks and statuses unchanged. 120 offline evaluation tests.

**Guards for new questions (user, 2026-10-05).** `ExpectedAnswersTests` (offline, every `dotnet test`): every trap has its
`trap_why`; every answerable question has `expect` and `chunk_expect` (without one, its rank read "Not scored: a negative"
and the figure source never said where the answer was - silently); no negative has a `chunk_expect`; every question in the
question files has an entry (the run would stop at it). And a question set with no v2 baseline is summarized without the
comparison instead of failing the summary after every question was asked (`BaselineGrades` was a dictionary lookup that
threw). The README's evaluation section lists what a new question needs. 124 offline evaluation tests.

**What else the library offers, and tags (user, 2026-10-05).** Checked against the Reporting API and the report's code:
in use - custom evaluators, numeric and string metrics with interpretations, metadata, the disk store, response caching,
the HTML report (History, Comparison). Not used: tags (shown per case, filter chips); iterations (a scenario asked N
times within one execution, "passed 2/3" and a min-max spread - the library's own form of the variance measurement);
diagnostics (info/warning/error per metric); the conversation view (renders every stored message, system included, as
Markdown - the only place a link is clickable); chat details (tokens, latency - stored, not shown by the HTML report);
`JsonReportWriter`; the `aieval` tool (reports, cache and result cleanup); boolean metrics. Deliberately not: the other
Quality evaluators, NLP, Safety and Azure storage (cost), telemetry. Linking an excerpt to its filing: MSFT and ORCL tag
every figure with an element id (A10's 26,445 is `F_fc2525fa-...`), NDAQ and NFLX don't - a text fragment would be the
fallback, untested on local files. **Built: tags** - `kind:<kind>`, `calculation`, `has traps`, `company:<ticker>` and
`statement:<type>` from the app's own routing (`ResolveFilings`, `ResolveStatementType`, as RagAnswerService calls them).
Passed to `CreateScenarioRunAsync` by name - the parameter before them is `additionalCachingKeys`. Baseline refreshed:
102/102 cache hits, results unchanged. The tags already show A10 routed to `statement:none` (no statement keyword in "cash
... pay ... dividends"). **Later, each its own step (user):** the prompt in the conversation view with filing links,
diagnostics, iterations. 127 offline evaluation tests.

**Variance measurement - done (2026-10-05, `eval/v3-runs/structured-hybrid-v3-variance.txt`).** All 102 questions asked
afresh twice (`structured-hybrid-v3-variance-1`, `-2`; 113 and 116 min), compared with the v3 baseline (answered
2026-10-02):
- **Pass 1 vs pass 2: 102/102 word for word, 102/102 grades.** On one setup, `llama3.1:8b` at temperature 0 repeats exactly.
- **Baseline vs either pass: 88/102 identical text, 100/102 grades** - 10 wording only; 2 figures (A27 25.9% -> 25.8%, still
  wrong; T7 adds a second figure, still reliable); 2 grades: **A21** reliable -> wrong (lists both years' net income, no
  longer adds them: answer-side 20/27 -> 19/27) and **T6** declined -> wrong (the Adenza-goodwill trap, 5,933; a fail either
  way). Retrieval identical; no untraced figure in either pass.
- **Why the baseline differs - a different Ollama build.** Ollama updated itself from 0.35.0 to 0.35.1 on 2026-10-05 at
  08:10 (downloaded 2026-10-02 15:06, installed at the next start - `%LOCALAPPDATA%\Ollama\app*.log`); the baseline was
  answered on 0.35.0, both passes on 0.35.1. 0.35.1's notes list "Updated llama.cpp" - ollama/ollama#18652 moved llama.cpp
  from b11081 to b11232 (151 upstream commits), among them "ggml-cpu: tiled mul_mat for k-quants" (ggml-org/llama.cpp#27851);
  the local `llama3.1:8b` is Q4_K_M, a k-quant. Tiling changes the order of floating-point sums, so the last digits of each
  layer's output; at temperature 0 the most likely next token is taken, so where two are nearly tied the answer can take
  the other path. The most likely cause, not proven (that would need both builds side by side). Earlier unexplained drift -
  Q1's two wordings and the T8/T10 changes found 2026-10-02 against runs of 2026-09-30 - also crosses an Ollama update
  (2026-10-01 08:05-08:08, server.log's routes.go line moving 2005 -> 2099); A16's change between two runs on 2026-10-02
  does not, and stays unexplained.
- **How to read results from now on:** within one Ollama build, the noise floor is 0 - any changed grade is a changed
  input. Across builds, a set can move by a question (A21) - compare grades only on the same build; record it with a run.
  Sources: github.com/ollama/ollama/releases/tag/v0.35.1, github.com/ollama/ollama/pull/18652,
  github.com/ggml-org/llama.cpp/compare/b11081...b11232.

**Step 6 - the local judge: done, kept alongside the strict grade - not as the grade (2026-10-05, `eval/v3-runs/judge-structured-hybrid-v3-judge-equivalence.txt`).**
Equivalence, judged by `llama3.1:8b`, over the baseline's 102 cached answers: 13 min, ~8 s a call.
- **Format:** the library read 15 of 102 replies - llama answers "...Therefore, the Equivalence metric should be 5." instead
  of the bare integer asked for. Recovered from the words, 101 of 102 have a score (the recovery pattern widened after
  reading the run's replies - "metric should be 5", "metric value is 5", a leading "4 "; a reply cut off before its score
  stays unread: T7, which repeats the answer).
- **Agreement 87/101, all disagreements one way:** the judge passes **14 of the 18 strict failures** it scored and fails no
  strict pass. It scores a wrong figure as close: A10's $27,034M against $26,445M - 4, "a slight difference in the amount";
  H25's $67,357M (total revenues) against $46,751M operating expenses - 5; A27's 25.9% against 25.4% - 5, "only a slight
  difference". What the strict grade exists to catch, this judge can't see - Microsoft's docs warn the prompts are tuned
  for GPT-4o and "especially poor" with small local models; this measures it. Not the grade - kept as a second, similarity view (`Graders: both`, below).
- **Why the library failed on the format, and the score given to it alone (user):** Equivalence's parser takes the trimmed
  reply as the value (`TryParseEvaluationResponseWithValue` -> `TryParseValue`, dotnet/extensions), its prompt asks for "a
  single integer value ... no other text", and its docs name one tested model, GPT-4o. The tag-based evaluators read only
  `<S2>`, and the JSON-based ones re-ask the model to "Fix the following JSON object" - Equivalence has neither. So
  `ScoreOnlyEquivalenceEvaluator` wraps it: a reply that writes its score in words reaches it as the score alone, and the
  library parses and interprets it as its own (`MinimumPassingScore = 4.0`, confirmed in the source); the metric keeps the
  judge's sentence ("Judge reply") and "Score taken from the reply's words: yes/no"; a reply with no score (T7) still
  fails to parse. Re-run from the cache (204/204 calls cached): the report shows 101 scores, the agreement unchanged.
- **Groundedness (phase B) not run** (recommended): the figure source already shows every wrong answer's figure is in its
  excerpts - "grounded" - and Equivalence shows the judge doesn't weigh figures; ~3.5 h to confirm both.
138 offline evaluation tests.

**Ollama version tag (user, 2026-10-05).** Every case is now tagged `ollama:<version>` - the build serving the run, asked
of Ollama once at the start (OllamaSharp `GetVersionAsync`). A tag can only name the build that ran the execution: an answer
replayed from the response cache was produced by the build that first answered it, so the tag is exact for fresh runs
(`EVAL_NO_CACHE`) and names the running build for cached ones. The four v3 executions were tagged after the fact (user),
from Ollama's logs: `structured-hybrid-v3-baseline` `ollama:0.35.0` (answered 2026-10-02; later refreshes replayed the
cache), both variance passes `ollama:0.35.1`, the judge run `ollama:0.35.1` (the judge's build; its answers are the
baseline's, from the cache). Inserted as text into each stored result's tags - every other field checked unchanged;
reports rebuilt (`ReportWriteTests` now also rewrites each run's own report). The imported v1/v2 runs stay untagged:
their builds weren't recorded. 139 offline evaluation tests.

**Graders configurable, and one configuration style: evalsettings.json (user, 2026-10-05).** The judge stays (user: "I still
want to keep both") - next to the strict grade, not instead of it. A run chooses its graders: `strict` (default), `judge`
or `both`; answer rank and figure source always run. The run's options had been environment variables (`EVAL_EXECUTION`,
`EVAL_NO_CACHE`, ...) while the app used appsettings.json; the user asked for one style. Now
`RagFilingExplorer.Local.Evaluation/evalsettings.json` holds every option (section `Evaluation`), loaded by
`EvaluationSettings` like AppSettings: every key required, checked against the raw configuration, values validated at load
(an unknown grader, judge or set, `UnloadEachQuestion` without `NoCache`, judges asked for with none listed - each named in
the error). An environment variable overrides a key for one run with the same name (`Evaluation__NoCache=true`, .NET's
configuration layering) - how run-variance.ps1 and run-judge.ps1 set a pass; each clears inherited `Evaluation__*` first,
since the two can run in one process. Lists are comma-separated strings, not JSON arrays: an environment override replaces
array items by position, so a two-item list overridden with one item would quietly keep the second. A run without the
strict grade counts the judge's passes in its summary and skips the v2 comparison; the agreement report refuses it by
name (it needs both); the variance comparison shows its grade as "-". Checked end to end on cached questions, no model
call: the judge script ("graders both", "Equivalence passes 1/1"), a judge-only run (no strict grade, no baseline
comparison), the variance comparison. 152 offline evaluation tests.

**The prompt in the report's conversation view (user, 2026-10-05; listed for later with the tags, built now).** Each result
stores the conversation the report renders: the app's system prompt exactly as sent - `RagAnswer.Prompt`, a new field
carrying the messages the chat model received (an additive app change; behaviour unchanged, one app test checks the field
equals what the chat client got) - then the five excerpts, each with the app's header and the filing's name linked to
`../../data/<filing>.html` (the reports sit in `eval/v3-runs/`), the text in a code block so Markdown doesn't run table rows
together, then the question. The app sends excerpts and question in one user message; they're split so the question is
the last message, the only one Microsoft's evaluators read as the request (`TryGetUserRequest`: the last message if its
role is User, dotnet/extensions `ChatMessageExtensions.cs`) - checked: the judge run refreshed with all 204 calls from the
cache and the same agreement (87/101). Links go to the filing, not the figure: MSFT and ORCL tag figures with element ids,
NDAQ and NFLX don't - figure-level links stay a later step. The baseline and judge runs refreshed from the cache (results
unchanged; the baseline's `ollama:0.35.0` re-applied after the refresh tagged the running build); the variance passes keep
no conversation - their fresh answers weren't cached. 373 unit tests, 156 offline evaluation tests.

**Screen: tell the model what the excerpts' order means (user, 2026-10-05) - planned before running.** The prompt sends the
five excerpts best-ranked first but says nothing of it. The variant: one sentence added to the system prompt ("The
excerpts are ordered by retrieval relevance - the first is the closest match to the question, by how similar its text is
to the question. Relevance is similarity, not proof that an excerpt holds the answer.") and each header gains "(relevance
rank N of 5)"; retrieval, excerpts and question otherwise the app's. Targets: A10, A14, A15 (the right figure sits in a
lower-ranked excerpt than the misread one) and T6 (declined, the answer in excerpt 1); controls, passing today: Q1, Q4,
Q10, Q24 (asks for dividends declared - A10's reverse), A1, A2, A9, A13, H6, H16, R1, T7. Reference: the current prompt on
Ollama 0.35.1 - each question also sent unchanged through the same path, which must reproduce the variance pass word for
word. Expectation stated: no gain - relevance is topical similarity, the lookalike usually ranks higher, and most
misreadings sit inside one excerpt. **Bar: a full run only if at least one target is fixed and no control lost.**
**Outcome (`eval/relevance-hint-screen/results.txt`, 37 min): bar not met - 0/4 targets fixed, 0/12 controls lost; no full
run.** A10, A14 and A15 answered word for word as before - the model reads the first plausible line, whatever it's told of
the order; T6 moved from the 5,933 trap to a clean decline (still a fail); 6 controls changed wording only; the rank was
never repeated in an answer. The unchanged prompt reproduced the variance pass on 15/16 - R1 differed by one comma ("May 31,
2026, were"), same figure and grade: the first difference seen within one Ollama build, with questions sent in another order
(prefix reuse is a candidate; not tested). Within a build, "repeats exactly" holds for grades and figures, not always to the
comma.

**Screen: Groundedness (user, 2026-10-05) - planned before running.** Microsoft's GroundednessEvaluator, judged by
`llama3.1:8b`, on the baseline's cached answers, as a screen instead of a ~3.5 h full run: `Graders: both`, `Judges:
groundedness`, execution `groundedness-screen`. 18 questions: the 11 wrong-figure answers (A8, A10, A11, A14, A15, H13, H25,
H34, Q2, T1, T9), 4 correct figures (Q1, A1, A13, H16), 2 facts (Q12 "Austin", Q13 "Ernst & Young") and a correct decline
(Q15). Its prompt reads only the question (no history), the excerpts as context and the answer, with a 800-token reply
cap - inside the judge's 8,192 context. Expectation stated: the wrong answers score high - every figure they state is in
their excerpts (figure source, 0 untraced in 306 answers). **Bar: a full run only if it fails (score below 4) at least 3
of the 11 wrong-figure answers and no correct one.**
**Outcome (`eval/v3-runs/judge-groundedness-screen.txt`, 31 min, ~103 s a call): bar not met - 0 of the 11 wrong-figure
answers failed; no full run.** Every answer scored 4 or 5 - wrong ones 5x4, 6x5; correct ones 2x4, 5x5. The judge calls a
wrong figure correct: A10's $27,034M (declared) - 4, "providing the correct amount of cash paid in common stock dividends";
H25's $67,357M (total revenues, given as operating expenses) - 5, "a direct and accurate answer ... fully correct and
complete". All 18 replies were readable (Groundedness reads its score from `<S2>` tags, which llama follows). As expected:
the wrong figures are in the excerpts, so "grounded" holds; the line is wrong. Groundedness stays available
(`Judges: groundedness`) but isn't run by default.

**Closing v3: comment review, and .NET the source of truth (user, 2026-10-06).** The comments were read against the code,
as at the end of v2: eight stale ones fixed (wording the variance measurement superseded - temperature 0 repeats within
one Ollama build; "spike" for the judge now kept alongside; the judge context explained by a Retrieval judge never
offered) and the run summary's "top 5" now read from `Retrieval:GenerationTopK`. Then, from the report: the Answer rank's
"tools/replay_recall.py's rank, ported and held to it" read as if the script still ran - nothing in the evaluation runs
either Python tool; the parity tests compare with their committed output. **Decided (user): the .NET evaluators are the
source of truth; the Python files stay, unchanged, as the record of what was ported** - this replaces the plan's "then
they're retired" (v3 plan, above). A grading rule changes in `StrictGrader` only; `GraderParityTests` still holds it to
every v1/v2 grade, so a deliberate change is a decision recorded here with the answers it regrades. The replay mirrors
v2's retrieval and is no longer kept in step (the plan's live constraint reworded). Both evaluators' descriptions now say
"ported from ... and matched to it" (1,029 grades, 102 ranks). Runs stored before keep the earlier description as their
reason: `StrictFigureEvaluator.GraderNote` recognises it, or every reliable answer of the v3 baseline would show it as a
grader note (a test reads the stored baseline). 373 unit tests, 157 offline evaluation tests.
**Comments: what and why, never when or who (user, 2026-10-06).** Most of the review's fixes were comments carrying
history (dates, "(user, ...)", step numbers, measured counts, before/after stories) that a later measurement had moved past,
repeating this log. A rule in CLAUDE.md now keeps history here; the evaluation project's comments were rewritten to it
(each keeps its reason; test comments naming the real answer they use stay). The app's comments are left for later.
**The app's comments rewritten to the rule too (user, 2026-10-06, after v3.0).** Same approach as the evaluation project: each
keeps its reason, written as the rule it serves ("otherwise X happens"), and loses the step labels, dates, rank/recall
numbers and "used to... confirmed..." stories; facts about the filings that justify a rule stay. Two comments pointed to
README sections the trim removed - now to the Design-FAQ and this log. 41 files, comments only (every changed line is a
comment; no rebuild needed).

**Code review before going public (user, 2026-10-06).** A review of the app, the evaluation and the run scripts (`/code-review
high`) found ten issues, each checked against the code before fixing; none affected the shipped defaults' results.
- The index manifest hashed only `data/*.html`; `Structured` also reads each filer's taxonomy, so an edited `.xsd` or
  linkbase left a stale index trusted. Under `Structured` the manifest now records every `.xsd`/`.xml` in `data/` except
  EDGAR's `_htm.xml` test files - the existing index rebuilt once; its chunk dumps came out identical.
- A run reusing a stored run's name overwrote only the questions it asked again; the rest stayed and every reader took
  them as part of the run. Not auto-deleted (a smoke run under the baseline's name would wipe it): the runner lists them,
  the summary and a test warning say so, and the docs no longer claim the name "replaces that run".
- A mistyped `Only` id asked nothing and passed - now an error naming it (also for an id outside the sets run).
- Ollama's version was asked before the readiness check, so a stopped Ollama gave a stack trace - order swapped.
- `FigureSourceEvaluator` threw on an expected value holding two numbers ("2025-2030") - now matched as text.
- Judges with `NoCache` refused: the judge's larger context window makes Ollama reload the model around every answer.
- A variance comparison of a run without the strict grade counted every question as a grade change - now skipped.
- Windows PowerShell deletes a variable set to `''`, so the scripts' "all sets / every question" fell back to
  evalsettings.json silently - all sets are passed explicitly, and a non-empty `Only` in the file stops the script. The
  scripts' shared helpers moved to `tools/eval-common.ps1`; `run-variance.ps1` no longer takes `-Graders`.
- Comments with measured counts or step labels that the earlier cleanup missed.
A second review of the chunking, page-reader and XBRL code followed; its findings are below.
375 unit tests, 166 offline evaluation tests.

**Second review - chunking, page reader, XBRL (2026-10-06): recorded, to fix next (user).** Checked against the code:
1. `TokenChunker.SplitOversizedTable`: a 3-12 line oversized Markdown table with no data-looking or label row is read as
   all header and yields no pieces - table and title dropped silently (Markdown strategy). Fix: fall back to the 2-line
   header; regression test.
2. `StatementLabels`: a combined "Operations and Comprehensive Income" role maps to comprehensive_income only, so
   income_statement is missing and the build stops (new filers).
3. `CoverFacts.ShortName`: a leading "The" is kept ("The Coca-Cola"), so questions don't match - unfiltered search.
4. `CoverFacts.Clean`: all-caps names title-cased ("KPMG LLP" -> "Kpmg LLP", "AT&T INC." -> "At&T Inc.").
5. `SectionSplitter` (Structured): sections start at v1's title patterns; a missed title can merge two statements
   (build stops) or orphan a title. Fix: start a section at every labelled statement table; chunk dumps must stay identical.
6. `IxTransformations.DurationFromWords`: "twenty-five years" -> P5Y, "1.5 years" -> P5Y, silently.
7. `FilingBlockReader`: a note start/end inside a table or link is missed - wrong or leaking note topic.
8. `HtmlTableLinearizer.ReadRow`: rowspan ignored when placing cells; 66 uses in the filings, all at row edges, so no
   effect today. Planned as a known limitation, not a fix.
9. `FilingBlockReader`: ordered lists numbered 2, 4, ... (Index() counts whitespace nodes); no filing uses <ol> today.
10. Comments: step labels/counts in XbrlModel, StatementLabels, IxTransformations, HtmlTableLinearizer,
    LinearizedChunkingStrategy; HtmlTableLinearizer's "contextRef for the test oracle only" is wrong (PeriodLabels uses it).
Plan: fix 1-4, 6, 9, 10 with tests; 5 and 7 with the chunk dumps checked identical; record 8 as a limitation.

**Second review - fixed (2026-10-06).** 1-7 and 9 fixed with regression tests (each fails without its fix), 10 the
comments, 8 documented on `HtmlTableLinearizer` as a known limitation (rowspan not expanded; every rowspan in the four
filings sits at a row's edge). Every strategy's chunk dumps regenerated for the four filings: `Structured` and
`Linearized` byte-identical; `Markdown` gained two chunks for NFLX - a page of its exhibit index (Exhibits 101 and 104
with the table header) that #1 had silently dropped since v1. So #1 was a shipped bug, not only a latent one; the dump is
updated, and `rag.markdown.db` lacks those two chunks until rebuilt (`--rebuild` with `Chunking:Strategy` = Markdown).
The fixes for 2-4 and 6 change nothing on the four filings; they protect a new filer. 393 unit tests, 166 offline
evaluation tests.
**#8 fixed too (user: not just recorded).** `HtmlTableLinearizer` now lays out a table as the HTML table model does: a
rowspan cell holds its columns in the rows below, whose cells start after it (`ReadRows`, replacing the row-by-row
`ReadRow`). A test with a rowspan header cell mid-row: without the fix the table fell back to text rows (its header
text lost to misplacement - the content guard caught it; with no text lost the values would have gone under the wrong
year silently), with it every value sits under its own header. All three strategies' chunk dumps byte-identical on the
four filings - their 66 rowspans all sit at a row's edge. 394 unit tests.
**Both reviews' fixes measured: no answer changed (2026-10-06, `eval/v3-runs/review-fixes-vs-variance.txt`).** All 102
questions asked afresh (`structured-hybrid-v3-review-fixes`, NoCache, 112 min, Ollama 0.35.1) on the rebuilt index and
compared with the two fresh variance passes on the same build: 102/102 answers word for word identical, 102/102 grades,
and every question's answer rank the same - retrieval and answers both unchanged by the fixes. Against the v2 baseline
the same three as before (T6, A16, A21 - the Ollama-update differences). Main 33/40, held-out 31/35, answer-side 19/27,
no untraced figure.
**`rag.markdown.db` rebuilt (2026-10-06):** 1,446 chunks (NFLX 390 - the two recovered exhibit-index chunks included).
Smoke: "Which exhibit number in Netflix's 10-K is the cover page formatted in Inline XBRL?" under `Markdown` answers
"104" citing PART IV > Exhibit Index - a line only that recovered page holds. Default path smoke (Q1, H2, A10) replayed
from the cache unchanged - same prompts as before the fixes.

---

## Follow-up: over-engineering audit (ponytail) - DONE (branch `ponytail-cleanup`), outcome below

**Context (user, 2026-10-07, after v3.0):** the `ponytail` Claude Code plugin's `/ponytail-audit` was run over the repo
as a read-only trial, to see what an over-engineering audit finds before deciding whether to use the plugin. It listed
17 findings. Four were the project's deliberate records and options and stay: the opt-in reranker, the v1 chunking
strategies, the one-off evaluation code kept as explicit tests (`HistoricRunImporter`, `RelevanceHintScreenTests`), and
the Python tools and `LinearizeSpike`. `Chunking:TokenizerModel` stays (it costs nothing and sits in the manifest), and
so does `VectorStore:UpsertBatchSize` (user): it is the switch for the day a SqliteVec build fixes multi-record upserts.
The rest is done in five commits (A-E), each checked against real output, not only the unit tests: chunk dumps and
embedding texts byte-identical, evaluation replays hitting the response cache (every prompt byte-identical), reports
regenerated identical.

**A - packages.** `coverlet.collector` removed from the tests (nothing collects coverage) and the explicit
`Microsoft.Extensions.Configuration` reference from the app (`.Configuration.Json` 10.0.12 depends on it at the same
version, so it still resolves to 10.0.12). Build clean, 394 + 166 tests, no vulnerable packages.

**B - chunking code.** One `MarkItDownConverter.ReadFiling` (read + `DetectEncoding`) for every reader of a filing:
the three strategies, `CompanyRegistry`, four tests and `LinearizeSpike` had each decoded it themselves. Being
synchronous, it made `StructuredChunkingStrategy.ReadAsync` a plain `Read(FileInfo)`. `MarkdownChunkingStrategy.ChunkText`
is the section-to-chunk loop the Linearized strategy had copied; `ChunkingStrategies.Create` makes the tokenizer once;
`EmbeddingTextBuilder.ExtractRowLabels` is one LINQ query (its `HashSet` kept as the filter - `Enumerable.Distinct`
doesn't promise first-seen order, and the order is in the embedding text); `FilingChunkRecords` builds the embedding
text in one expression. Checked with a throwaway snapshot of everything chunking produces, before and after, for all
three strategies over the four filings - every section, chunk, statement type, embedding text and company
registration: byte-identical (Structured 975 chunks, Markdown 1,446, Linearized 999 - the committed dumps' counts). So
no index needs rebuilding. 394 + 166 tests.

**C - async LINQ.** Four `await foreach` loops that only collected or counted became .NET 10's built-in
`System.Linq.AsyncEnumerable` (in the shared framework, no package): `ToListAsync` in `RagAnswerService.SearchAsync` and
`EvaluationRunner.WriteReportAsync`, `ToHashSetAsync` in `EvaluationRunner.EarlierScenariosAsync`, `CountAsync` in
`IndexBuilder`'s after-upsert check. Checked on Ollama 0.40.0 (auto-updated from 0.35.1 since the v3 runs): the full
evaluation replayed from the cache - all 102 questions in 0-2 s each, so every prompt was byte-identical to the one
cached - and its stored results matched `structured-hybrid-v3-baseline` question by question: prompts, answers, strict
grades, answer ranks and figure sources with all their diagnostics (the only difference, every metric's description, is
the rewording the baseline was deliberately not refreshed for). Every report regenerated identical but for its
`createdAt`. The replay was then deleted. `IndexBuilder`'s count runs only on an index build, not rebuilt for this.

**D - settings.** One required-key check for both settings files: `AppSettings.EnsureKeysPresent(configuration, type,
prefix, fileName)`, on the existing `LeafKeys` reflection walk, which now skips read-only properties (computed from the
others - `EvaluationSettings.UsesStrictGrade`, `JudgeNames`, ...); `EvaluationSettings.From` had its own copy. Same
message as before ("evalsettings.json is missing required key(s): Evaluation:Judges, ..."). `Evaluation:Graders` is an
enum (`Graders`: Strict, Judge, Both), like `Chunking:Strategy` and `Retrieval:Search`: the binder ignores case and
fails a typo at load, so its hand validation and lower-casing went. The typo's error is now .NET's ("Failed to convert
configuration value 'judges' at 'Evaluation:Graders' to type '...Graders'") - it names the key and value but no longer
lists the choices, as the app's enum settings already do. `VectorStore:UpsertBatchSize` stays (user): the switch for a
SqliteVec build that fixes multi-record upserts. Checked: 394 + 166 tests (the settings tests on the enum); the app
started on the current index; an evaluation started with `Evaluation__Graders=STRICT` (Q1 from the cache, reliable,
rank 1) and failed at load with `judges`.

**E - evaluation code.** `EvaluationRunner.LatestResultsAsync` - the latest iteration of each question in a stored run,
failing if there is none - replaces the identical loop `JudgeAgreement.LoadAsync` and `VarianceComparison.LoadAsync`
each had (`GroupBy` + `MaxBy` keep the old order and its first-wins tie). Checked: the five stored comparison reports
(the three variance comparisons and both judge agreements) regenerated from the stored results before the change and
after it - identical to the committed files both times. 394 + 166 tests.

**Outcome.** 11 of the audit's 17 findings done in A-E (the other six kept, above); behaviour checked unchanged at
every step - chunking output, every prompt, answer, grade, rank and figure source, and every stored report. The
audit's four largest findings were the project's deliberate records and options, which bears on using the plugin as a
standing mode rather than on demand - the user's call.
