# Implementation Plan for Claude Code: SEC 10-K RAG Tool (Phase 1 — Local/Ollama)

This is the execution-ready version of a plan already worked out and validated against Microsoft's
own docs (via the Microsoft Learn MCP connector) in a prior planning conversation. Hand this file to
Claude Code as-is.

**This covers Phase 1 only** — a fully local, zero-cost RAG tool. It's a complete, shippable project
on its own; there is no assumed follow-up phase.

---

## Ground rules

1. **Correctness over speed.** This is explicitly not a race. Do not skip steps or rush chunking/
   retrieval quality to "finish faster." If something needs more time to do right, take it.
2. **Preview packages are expected, not a bug.** `Microsoft.Extensions.DataIngestion` is genuinely
   still in preview (`--prerelease` required). Don't treat its rough edges as something to silently
   work around — try it per Step 3, and if it's genuinely not working, fall back to the simple
   splitter and say so.
3. **Report at named decision points below** (e.g. falling back from MEDI to the simple chunker)
   rather than silently switching approach — a one-line note is enough, this isn't a permission gate.

---

## Prerequisites (already complete)

- .NET SDK installed — confirmed version `10.0.400` (single SDK installed, no ambiguity to resolve)
- Ollama installed and running
- Models pulled: `nomic-embed-text` (274MB), `llama3.1:8b` (4.9GB)
- Hardware: 12th Gen Intel i7-12800H, 32GB RAM, Intel UHD integrated graphics — **CPU-only inference,
  no GPU acceleration available.** This is why `llama3.1:8b` was chosen over larger local models.
- Python 3.12 + `pip install markitdown` — added during Step 3 to shell out to the `markitdown` CLI
  for HTML→text extraction. Not part of the original plan; see Step 3's outcome below for why this
  became necessary, and Step 8 for how this affects the "zero setup" pitch.

---

## Repo structure

```
RagFilingExplorer/
├── data/                       (SEC 10-K filings)
├── RagFilingExplorer.Local/    (this build)
```

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
  lesson also applies to purely decorative markup (see Step 3's post-review finding above).
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
  treat them as reliable unless something has changed since. The MEDI-specific findings above came
  from direct experimentation against this project's actual filings, not from documentation.

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
