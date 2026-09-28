# Design FAQ

Short answers to the questions a reviewer is likely to ask, each pointing to the full record in
[Decision-Log.md](Decision-Log.md) (quoted section names find the entry). Answers state decisions and
facts about the filings; the numbers that change as the project changes (test counts, chunk counts) live
in the README and [Implementation_Plan.md](Implementation_Plan.md), not here.

---

### Why not answer figures from inline XBRL directly?

Every 10-K tags its figures in inline XBRL, so a structured lookup looks simpler than RAG. It was
considered and declined for runtime use: 5-16% of facts in these four filings use company-specific
extension concepts (`ndaq:`, `orcl:`), the same line item maps to different concepts across filers
(revenue is `us-gaap:Revenues` at NFLX, `RevenueFromContractWithCustomerExcludingAssessedTax` at MSFT and
NDAQ, both at ORCL), and tag syntax varies by filing agent. Mapping a question to the right concept is a
separate text-to-query problem. XBRL is used instead as a **test oracle**: `tools/xbrl_column_check.py`
checks that the linearized tables put each value under the right column - kept out of the pipeline so the
check isn't circular. Decision-Log.md, "linearized tables as a second chunking strategy".

The filings also tag whole sections of text (notes, policies, schedules) under standard names shared across
filers, which would label chunks for routing more reliably than heading patterns, and every tagged number
carries its own unit, scale and period. v2 plans a new ingestion built on those regulated layers - a facts
table for headline figures, structure labels for every chunk - each step measured before the next.
Decision-Log.md, "XBRL hybrid (v2)".

### Why HTML as the source, not PDF - or OCR?

Tested three ways on MSFT's filing; HTML won each time.
- **A text-based PDF** (earlier trial): the income statement came out as one cohesive table, but
  conversion was ~15x slower, the cover page's securities table flattened to plain text, and the PDF still
  had zero headings and the same ambiguous MD&A sentences - so it fixed none of the hard problems.
  Decision-Log.md, "Considered and declined: PDF instead of HTML as the source format".
- **"Microsoft Print to PDF"** (tested 2026-09-25): the output has no text at all - every glyph is drawn as
  vector outlines, with no fonts - so `markitdown` extracts nothing. A text-based print of the same page
  (Edge's own PDF writer) converted, but the securities table flattened again and the 2.625% Notes row lost
  its `MSFT` trading symbol.
- **OCR** of the cover page (Windows' built-in engine, tested 2026-09-25): prose and numbers were read
  correctly, but "FORM 10-K" became "FORM IO-K", every checked box (☒) was dropped and empty boxes (☐) were
  read as "ü" - so the yes/no facts came out inverted - and the securities table split into separate
  columns. OCR is a last resort for a filing that exists only as a scan.

The HTML conversion gets every one of those details right. The two 2026-09-25 tests are recorded only here.

### Why a hand-written chunking pipeline instead of `Microsoft.Extensions.DataIngestion`?

It was the plan and was tried first. Two blockers: SEC EDGAR HTML has zero heading tags (`<h1>`-`<h6>`),
so its heading-based chunkers have nothing to split on; and its Markdown parser hardcodes Markdig's
advanced extensions, whose math extension reads dollar figures (`$1,234`) as math and crashes
(`NotSupportedException: MathInline`) - on documents that are mostly dollar figures. The pipeline instead
converts with the `markitdown` CLI and finds sections from plain-text patterns, which also avoids relying
on any one filing agent's HTML conventions. This is a property of this document family, not a verdict on
the library. Decision-Log.md, "Step 3: Chunking".

### Why does a question about revenue sometimes get "not in the context" when the figure is in the filing?

Statement routing is a **hard filter**: a question containing a financial-statement term ("revenue", "net
income", "total assets", "cash flow", ...) is searched only within that statement, so answers elsewhere -
segment tables, MD&A explanations, accounting policies - can't be retrieved. It's deliberate. Statement
filtering is what took the original test questions from 2/6 to 6/6, after search prefixes, row-label
enrichment and smaller chunks each failed to move them; removing it today would drop the core questions
with the figure in context from 22/22 to 11/22 (`Markdown`) or 15/22 (`Linearized`). A soft
filter (filtered and unfiltered results interleaved) was measured: it recovers some of those questions
but loses curated ones, and can't win the slots back because the prompt already fills most of the model's
context window. Not built. An LLM-based router was not evaluated. The planned v2 targets this gap with
XBRL section labels (see the XBRL answer above). Decision-Log.md, "retrieval quality" and
"pre-manual-pass review".

### Why no reranking or hybrid (keyword + vector) search?

Neither is available locally without building it. Ollama has no rerank endpoint, so cross-encoder
reranking is out. For hybrid search, `IKeywordHybridSearchable` exists in `Microsoft.Extensions.VectorData`,
but the SQLite connector doesn't implement it (Microsoft's connector page: "HybridSearch supported? No"),
and the connectors that do are all servers or cloud services. SQLite's FTS5 does work in the app's SQLite,
so hybrid search would mean an own FTS5 table plus rank fusion. Its feasibility was checked, not its effect:
it was listed as a follow-up when the project stopped adding scope before the manual pass, and never
measured. Decision-Log.md, "targeted questions and a rank metric" and "pre-manual-pass review".

PostgreSQL doesn't change this. Vectors need the `pgvector` extension (compiled on Windows, or Docker),
its built-in keyword ranking (`ts_rank`) isn't BM25 as FTS5's `bm25()` is, and its .NET connector
(`CommunityToolkit.VectorData.PgVector`, preview) doesn't implement hybrid search either ("HybridSearch
supported? No"). Hybrid would be the same hand-written query plus rank fusion, on a server to install and
run, where SQLite is one file. Its index (HNSW) pays off at a scale far beyond ~1,000-1,450 chunks.
Considered for v2 and declined (2026-09-28).

### Why `llama3.1:8b`?

The hardware is CPU-only (integrated graphics, no GPU acceleration), which rules out larger local models
at usable speed. It was not benchmarked against other chat models. `qwen3.5:2b` was pulled as a reference
reasoning model to build and test reasoning support, not as a replacement. Implementation_Plan.md,
"Prerequisites"; Decision-Log.md, "reasoning-model support".

### Why two chunking strategies, and why is `Markdown` still the default?

`markitdown` drops HTML colspan, so Markdown tables leave the chunker guessing which value sits under which
year. The `Linearized` strategy reads tables from the HTML instead and writes each row as a self-contained
line. On targeted table questions it put the answer in the model's context more often, but answered only
one more correctly - within noise at that sample size - because denser chunks cost one question and the
model misread another despite ranking the right chunk first. Both ship and are switchable; `Markdown`
stays the default. Decision-Log.md, "targeted questions and a rank metric".

### Why is `Retrieval.ChatTemperature` 0?

So a changed answer can be attributed to a code change rather than sampling. At 0.2, questions whose
figure was already in the context flipped between right and wrong from run to run. Decision-Log.md,
"trailing remainders, per-company search, table-piece headers".

### Why is `VectorStore.UpsertBatchSize` 1?

`CommunityToolkit.VectorData.SqliteVec` 1.0.1-preview fails any multi-record upsert with `UNIQUE constraint
failed on vec_chunks primary key` - an upstream `sqlite-vec` bug fixed in a native build the NuGet package
hasn't picked up. Swapping in the newer `vec0.dll` by hand was rejected: it would work locally and silently
regress for anyone who clones the repo. The cost is negligible: ~4-5 records/sec on this hardware, batched
or not. Decision-Log.md, "persisted vector store".

### Why wasn't a different embedding model tried?

It was planned after linearization, since that changes what gets embedded and the two interact; the
candidates' query/document templates are already recorded. It was deferred when the project stopped
adding scope before the manual pass. Models limited to 512 tokens are excluded as they stand: chunks reach
~800 tokens, so table rows would be silently truncated. Decision-Log.md, "embedding-model comparison".
