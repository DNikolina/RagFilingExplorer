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

v2 does read inline XBRL - at ingestion, to label chunks, never to answer a figure. Its `Structured` strategy
parses the filing's DOM and its XBRL (matching EDGAR's own extraction fact for fact on all four filings) and takes
from it what heading patterns guess at: each statement's type from the filer's statement roles, each note's
topic, period labels on roll-forward rows, and the company itself - name and ticker from the cover-page facts, which
register a new filer and open every chunk's embedding text. A facts table answering headline figures directly
was planned as a later step, conditional on headline-figure failures remaining; they didn't (headline figures
pass), so it wasn't built. Decision-Log.md, "XBRL hybrid (v2)".

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

Under `Vector` search (v1's retrieval, still selectable), statement routing is a
**hard filter**: a question containing a financial-statement term ("revenue", "net income", "total assets",
"cash flow", ...) is searched only within that statement, so answers elsewhere - segment tables, MD&A
explanations, accounting policies - can't be retrieved. It's deliberate. Statement
filtering is what took the original test questions from 2/6 to 6/6, after search prefixes, row-label
enrichment and smaller chunks each failed to move them; removing it today would drop the core questions
with the figure in context from 22/22 to 11/22 (`Markdown`) or 15/22 (`Linearized`). A soft
filter (filtered and unfiltered results interleaved) was measured: it recovers some of those questions
but loses curated ones, and can't win the slots back because the prompt already fills most of the model's
context window. Not built. An LLM-based router was not evaluated. v2's `Hybrid` search closes the gap
differently: the statement type becomes one boosting list among three instead of a filter, so a question routed
to the wrong statement can still reach its answer - the routing misses that were impossible under the hard filter
(e.g. "deferred revenue", routed to the income statement on "revenue") are answered (see the next answer).
Decision-Log.md, "retrieval quality", "pre-manual-pass review" and "XBRL hybrid (v2)", step 2.

### How are reranking and hybrid (keyword + vector) search done?

Neither is available locally off the shelf. Ollama has no rerank endpoint, and `Microsoft.Extensions.AI` has no
reranking abstraction; Microsoft's own rerankers are cloud services. So v2 (step 2b) runs a cross-encoder in-process:
`ms-marco-MiniLM-L6-v2` (one of the two models Microsoft's RAG guidance names), ONNX Runtime, reordering each company's top 25
hybrid candidates, each read as its company line + excerpt header + chunk. It's a setting, `Retrieval:Rerank`, and
needs `Hybrid`. The model (~91 MB) is fetched separately and refused unless its SHA-256 matches. One non-obvious
part: .NET's `BertTokenizer` tokenized every one of 73,125 (question, chunk) pairs differently from the Python
library the spike measured with (it drops line breaks, `|` and `$`), so the app reimplements Hugging Face's
normalisation - checked token for token. Chosen by a replay-only spike (L6 vs L12, with and without the company line,
25 vs 50 candidates, overlapping windows for long chunks), then measured end to end: 64 -> 69 of 75 reliable answers,
every decline intact, ~2 s of CPU per question. Decision-Log.md, "Step 2b".

**It ships off.** A fresh answer-side question set then showed the other side: the reranker prefers MD&A and note
prose to statement rows, and pushed cash-flow answers out of the top 5. Across every question set, hybrid search
alone puts as many answers in the model's context as reranking or RRF blends of the two, and a larger reranker
family (bge-reranker-v2-m3) did no better on the questions they disagree on, at many times the CPU. So reranking
stays an opt-in setting, built and measured, not a default. Decision-Log.md, "A1-A27 with reranking off" onwards.

For hybrid search, `IKeywordHybridSearchable` exists in `Microsoft.Extensions.VectorData`, but the SQLite connector
doesn't implement it (Microsoft's connector page: "HybridSearch supported? No"), and the connectors that do are all
servers or cloud services.

v1 shipped without hybrid search - its feasibility was checked, never its effect. v2 built it (step 2): an SQLite
FTS5 table over the same chunks, ranked by `bm25()`, fused with the vector ranking by reciprocal rank fusion, with
the question's statement type as a third, boosting list instead of a hard filter. It's a setting,
`Retrieval:Search` (`Vector` | `Hybrid`), and `Hybrid` is the default from v2. Chosen by a replay of six variants before any code was written; on the
Structured strategy it took the held-out questions from 10/15 to 14/15 and answered the routing misses a hard
filter made impossible. Decision-Log.md, "XBRL hybrid (v2)", step 2.

PostgreSQL doesn't change this. Vectors need the `pgvector` extension (compiled on Windows, or Docker),
its built-in keyword ranking (`ts_rank`) isn't BM25 as FTS5's `bm25()` is, and its .NET connector
(`CommunityToolkit.VectorData.PgVector`, preview) doesn't implement hybrid search either ("HybridSearch
supported? No"). Hybrid would be the same hand-written query plus rank fusion, on a server to install and
run, where SQLite is one file. Its index (HNSW) pays off at a scale far beyond ~1,000-1,450 chunks.
Considered for v2 and declined (2026-09-28).

### Why `llama3.1:8b`?

The hardware is CPU-only (integrated graphics, no GPU acceleration), which rules out larger local models
at usable speed. It was kept after a measured comparison (2026-10-01) with `granite4.1:8b` and `granite4.1:3b` on
the same index, retrieval and prompt. Granite 8B read lookalike table lines better, but scored lower on the main and
held-out sets, drifted from the prompt's decline form, stated one figure found in none of its context and ran ~1.5x
slower; the 3B was lower almost everywhere. `qwen3.5:2b` was pulled as a reference reasoning model to build and test
reasoning support, not as a replacement. Implementation_Plan.md, "Prerequisites"; Decision-Log.md,
"reasoning-model support" and "Second-model run".

### Why doesn't the model use a calculator tool for arithmetic?

Because with this model it made answers worse. The model predicts digits rather than calculating, so a
`calculate(expression)` tool - evaluated exactly in code, offered through Ollama's tool support - was the obvious
fix, and it was screened before being built. `llama3.1:8b` called the tool on every arithmetic question and fixed
the one ratio it had divided wrongly, but it copied figures into the call wrongly (5,407,990 became 100407990) and
restated a correct result in a different unit, losing two answers it got right without the tool - and every
calculation took twice as long. The error moves from the arithmetic to copying the operands, which at 8B is no
more reliable. A fix needs code, not the model, to choose the operands - a facts table, not built (see the XBRL
answer). Prompt rules fared no better on the remaining misreadings: the right figure is in the model's context,
and it still picks a plausible neighbour. Decision-Log.md, "Answer side: what the model was given" onwards.

### Why three chunking strategies, and why is `Structured` the default?

`markitdown` drops HTML colspan, so Markdown tables leave the chunker guessing which value sits under which
year. v1's `Linearized` strategy reads tables from the HTML instead and writes each row as a self-contained
line. On targeted table questions it put the answer in the model's context more often, but answered only
one more correctly - within noise at that sample size - because denser chunks cost one question and the
model misread another despite ranking the right chunk first, so v1 kept `Markdown` as its default.
Decision-Log.md, "targeted questions and a rank metric".

v2's `Structured` strategy goes further: it reads the filing's DOM with no `markitdown`, keeps each table as
one block, and labels chunks from the filing's own XBRL (see the XBRL answer above). Built step by step, each
step measured against the last, and with hybrid search it answered most of the held-out questions v1 missed
(Decision-Log.md, "XBRL hybrid (v2)"). It's the default from v2. `Markdown` and `Linearized` stay, unchanged and
switchable, as v1's reference.

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
