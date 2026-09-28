# Manual test questions

Step 7 in [Decision-Log.md](Decision-Log.md) verified 6 questions (3 number-lookups, one
per filing; one prose fact; one prose description; one negative test) and got 6/6 after the
statement-type filtering fix. This is a second, broader round for manual testing before publishing -
it deliberately covers statement types and companies the original 6 didn't touch, especially
`comprehensive_income`, which only just got a keyword mapping in `QueryIntentResolver`.

Netflix (`NFLX-10K-2025.html`) was added later, alongside MSFT/ORCL/NDAQ - see Decision-Log.md's
"Follow-up: onboarding a new filer (NFLX)" for the three real bugs that surfaced onboarding it. Its
questions (17-22 below) are the regression check for that work.

Every expected answer below was pulled directly from `chunk-review/markdown/*.chunks.txt` (the actual
converted filing text, not memory/prior knowledge), same as Step 7's own verification method. Line
references are into the `Markdown` chunking strategy's dumps - the shipped default. The "Source"
line is where to check the figure yourself if an answer looks off.

For each question, also check that the printed `(filtering to ..., statement type: ...)` line
matches the expected filter - a wrong filter that happens to still retrieve the right chunk is a
result worth flagging even if the final answer is correct.

**Grading (from the 2026-09-25 pass).** Two columns per answer: **Correct** - the number matches the line
the answer names (a right number under a near-identical line's name is wrong, as in Q10); **Complete** - the
unit is stated and the period is clear (NFLX reports in *thousands*, the others in millions). Declines are a
third outcome: correct for negative and routing tests, otherwise "declined". Results are in Decision-Log.md,
"manual pass".

## Balance sheet

**1. What were Microsoft's total assets?**
Expected: **$758,376 million** (FY2026; $619,003 million FY2025).
Source: `MSFT-10K-2026.chunks.txt:2058`. Filter: `MSFT-10K-2026.html`, `balance_sheet`.

**2. What was Oracle's total stockholders' equity?**
Expected: **$43,056 million** "Total stockholders' equity" (FY2026; $20,969 million FY2025) - or
**$42,508 million** "Total Oracle Corporation stockholders' equity" (excluding noncontrolling interests),
if cited to that line. Source: `ORCL-10K-2026.chunks.txt:2465`. Filter: `ORCL-10K-2026.html`,
`balance_sheet`.
History: this failed from the initial commit onward while "stockholders' equity" routed to the equity
statement - ORCL's is a wide roll-forward split into 15 near-identical fragments, and the one holding
the closing balance (`ORCL-10K-2026.chunks.txt:2717`) ranked 8th-9th, outside the top 5. Period-end
equity questions now route to the balance sheet's single total row (see Decision-Log.md, "equity
routing").
(Careful: ORCL's chunks also contain an unrelated "Total assets" fair-value table around line 3201 -
a wrong retrieval landing there instead of the real balance sheet is worth noting.)

**3. What were Nasdaq's total liabilities?**
Expected: **$18,821 million** (2025; $19,195 million 2024).
Source: `NDAQ-10K-2025.chunks.txt:12420`. Filter: `NDAQ-10K-2025.html`, `balance_sheet`.

## Cash flow statement

**4. What was Microsoft's net cash from operations?**
Expected: **$182,935 million** (FY2026; $136,162 million FY2025; $118,548 million FY2024).
Source: `MSFT-10K-2026.chunks.txt:2175`. Filter: `MSFT-10K-2026.html`, `cash_flow_statement`.

**5. What was Oracle's net cash provided by operating activities?**
Expected: **$31,977 million** (FY2026; $20,821 million FY2025; $18,673 million FY2024).
Source: `ORCL-10K-2026.chunks.txt:2762`. Filter: `ORCL-10K-2026.html`, `cash_flow_statement`.

**6. What was Nasdaq's net cash provided by operating activities in 2025?**
Expected: **$2,255 million** (2025; $1,939 million 2024; $1,696 million 2023).
Source: `NDAQ-10K-2025.chunks.txt:12648`. Filter: `NDAQ-10K-2025.html`, `cash_flow_statement`.

## Equity statement

**7. What was Microsoft's total stockholders' equity?**
Expected: **$442,387 million** (FY2026; $343,479 million FY2025; $268,477 million FY2024).
Source: `MSFT-10K-2026.chunks.txt:2109` (balance sheet). Filter: `MSFT-10K-2026.html`, `balance_sheet`.
(The balance sheet shows two years; the equity statement's three-year row, including FY2024, is at
`MSFT-10K-2026.chunks.txt:2335`.)

**23. What was Nasdaq's total stockholders' equity?** (added later - regression check)
Expected: **$12,227 million** "Total Nasdaq stockholders' equity" (2025; $11,191 million 2024;
$10,816 million 2023) - or **$12,232 million** "Total equity" including noncontrolling interests, if
cited to that line - but not $12,232 million labelled as "Nasdaq stockholders' equity", which an
earlier run did. Source: `NDAQ-10K-2025.chunks.txt:12431` (balance sheet). Filter: `NDAQ-10K-2025.html`,
`balance_sheet`.
Before the second review this returned "(no results)": NDAQ titles its statement "Statements of
*Changes in* Stockholders' Equity", which the detector didn't recognize, so no NDAQ chunk was tagged
`equity_statement` at all.

**24. What did Microsoft's statement of stockholders' equity show for common stock cash dividends in fiscal year 2026?**
Expected: **$27,034 million** (dividends *declared*, from the equity statement) - not $26,445 million,
which is dividends *paid*, from the cash flow statement (`MSFT-10K-2026.chunks.txt:2194`,
"Common stock cash dividends paid"). Source: `MSFT-10K-2026.chunks.txt:2300`. Filter:
`MSFT-10K-2026.html`, `equity_statement` - checks that questions about *changes* in equity still route
to the equity statement after period-end equity questions moved to the balance sheet.

## Comprehensive income (new filter - watch this one closely)

**8. What was Microsoft's comprehensive income?**
Expected: **$133,812 million** (FY2026; $104,075 million FY2025; $88,889 million FY2024).
Source: `MSFT-10K-2026.chunks.txt:2012`. Filter: `MSFT-10K-2026.html`, `comprehensive_income`.

**9. What was Oracle's comprehensive income?**
Expected: **$16,882 million** (FY2026; $12,700 million FY2025; $10,557 million FY2024).
Source: `ORCL-10K-2026.chunks.txt:2565`. Filter: `ORCL-10K-2026.html`, `comprehensive_income`.

**10. What was Nasdaq's total comprehensive income for 2025?**
Expected: **$2,113 million** (or **$2,114 million** "attributable to Nasdaq" - either figure, if cited
correctly to the right line, counts as correct; 2024 was $940M / $942M).
Source: `NDAQ-10K-2025.chunks.txt:12523-12525`. Filter: `NDAQ-10K-2025.html`, `comprehensive_income`.

## Prose facts (not previously tested)

**11. How many people did Microsoft employ, and where?**
Expected: **approximately 223,000 full-time employees as of June 30, 2026** - 121,000 in the U.S.,
102,000 internationally.
Source: `MSFT-10K-2026.chunks.txt:487`. No statement-type filter expected (narrative chunk).

**12. Where is Oracle headquartered?**
Expected: **Austin, Texas** (address of principal executive offices).
Source: `ORCL-10K-2026.chunks.txt:35` (cover page) - an answer from Item 2. Properties ("Our headquarters
facility consists of approximately 0.9 million square feet in Austin, Texas") is equally correct, and is
what the model cited in the pass. No statement-type filter expected.

**13. Who is Nasdaq's independent registered public accounting firm?**
Expected: **Ernst & Young LLP**.
Source: `NDAQ-10K-2025.chunks.txt:11412`. No statement-type filter expected.

## Netflix (added later - regression check for the onboarding fixes)

**17. What was Netflix's total revenue for fiscal year 2025?**
Expected: **$45,183,036 thousand** (~$45.18 billion; FY2024: $39,000,966 thousand; FY2023:
$33,723,297 thousand).
Source: `NFLX-10K-2025.chunks.txt` (Consolidated Statements of Operations). Filter:
`NFLX-10K-2025.html`, `income_statement`.
Note: the citation will say `PART IV > Financial Statements`, not `Item 8` - that's expected, not a bug.
Netflix's financial statement pages come after Part IV's last Item (its `Item 8` only points to them),
so `SectionSplitter` heads them by the back-matter title that opens them ("INDEX TO FINANCIAL
STATEMENTS"). NDAQ's statements are headed the same way. Before the pre-manual-pass review they were
headed `PART IV > Item 16. Form 10-K Summary` - see Decision-Log.md, "pre-manual-pass review".

**18. What was Netflix's net income for 2025?**
Expected: **$10,981,201 thousand** (FY2024: $8,711,631 thousand; FY2023: $5,407,990 thousand).
Filter: `NFLX-10K-2025.html`, `income_statement`.
This is the exact question that returned a hallucinated **$12,443** before `QueryIntentResolver` had a
`CompanyToFiling` entry for Netflix (the search ran unfiltered across all four filings) - worth
double-checking the `(filtering to NFLX-10K-2025.html, ...)` line actually appears.

**19. What were Netflix's total assets?**
Expected: **$55,596,993 thousand** (2025; $53,630,374 thousand 2024).
Filter: `NFLX-10K-2025.html`, `balance_sheet`.

**20. What was Netflix's net cash provided by operating activities?**
Expected: **$10,149,273 thousand** (2025; $7,361,364 thousand 2024; $7,274,301 thousand 2023).
Filter: `NFLX-10K-2025.html`, `cash_flow_statement`.

**21. What was Netflix's comprehensive income?**
Expected: **$10,038,657 thousand** (2025; $9,297,738 thousand 2024; $5,401,351 thousand 2023).
Filter: `NFLX-10K-2025.html`, `comprehensive_income`.

**22. Where is Netflix headquartered?**
Expected: **Los Gatos, California**. Source: the cover page or Item 2. Properties ("leased principal
properties in ... Los Gatos, California, which is the location of our corporate headquarters").
No statement-type filter expected (narrative chunk).

## Edge cases

**14. Compare Microsoft's and Oracle's total revenue.**
Two companies named -> `QueryIntentResolver.ResolveFilings` returns both filings, so this runs one
company-filtered search per company, interleaved by rank - the printed line should read `(searching
MSFT-10K-2026.html and ORCL-10K-2026.html separately, statement type: income_statement)`. The answer
should cite both MSFT ($331,839M) and ORCL ($67,357M) figures. (Before this, the question ran unfiltered
with 5 slots shared across all four filings, and ORCL's revenue chunk fell to rank 10 - see
Decision-Log.md, "trailing remainders, per-company search, table-piece headers".) This question also matches `RequiresSynthesis` (contains
"Compare") - with the shipped default (`llama3.1:8b`, no reasoning support) that's a no-op and no
`(reasoning: ...)` line should appear, but if testing against a reasoning-capable `ChatModel`
(e.g. `qwen3.5:2b`) with `Retrieval.ReasoningEffort` set above `None`, expect to see one.

**15. What was Nasdaq's total revenue in fiscal year 2030?**
Negative test - no such fiscal year exists in this filing (it only covers 2023-2025). Expected: the
model should say the context doesn't contain this information, **not** guess or extrapolate a number.

**16. What was Apple's revenue last year?**
Negative test (repeat of Step 7's original out-of-scope check, kept here for completeness) - Apple
isn't one of the filings in `data/`. Expected: an explicit "not in the provided context" style
answer, not an answer from the model's own training knowledge.

## Targeted questions (T1-T10) and routing tests (R1-R3) - added later

T1-T10 aim at what the table-chunking work claims to fix - mid-table rows, split layouts, MD&A and notes
tables - where the original questions are mostly headline totals. R1-R3 are *routing* tests: their
keyword route (`QueryIntentResolver`) excludes every chunk holding the answer, so all three miss by design
until routing changes. Lines 25-37 of `tools/manual-questions.txt`; results for both chunking strategies
are in Decision-Log.md, "targeted questions and a rank metric". "Traps" are the nearby figures a wrong
answer tends to pick. "Filter" is the route the real resolver takes (checked, not predicted).

**T1. What was Microsoft's other comprehensive income for fiscal year 2025?**
Expected: **$2,243 million**. Traps: 63 (FY2026), 753 (FY2024), 104,075 (comprehensive income).
Source: `MSFT-10K-2026.chunks.txt:2009`. Filter: `MSFT-10K-2026.html`, `comprehensive_income`.
Reading it off the row counts; computing it as comprehensive income minus net income means the row wasn't found.

**T2. How much did Microsoft spend repurchasing its shares in the second quarter of fiscal year 2025?**
Expected: **$3,500 million** (8 million shares). Traps: 5,964 (Q2 FY2026), 2,800 (Q2 FY2024).
Source: `MSFT-10K-2026.chunks.txt:4976` (the year row sits *below* Shares/Amount in the HTML). Filter: `MSFT-10K-2026.html` only.

**T3. By what percentage did Netflix's technology and development expenses change in 2025 compared to 2024?**
Expected: **+16%** (+$466,095 thousand). Traps: the raw totals 3,391,390 / 2,925,295.
Source: `NFLX-10K-2025.chunks.txt:1005` (MD&A, an amount / % pair under one header). Filter: `NFLX-10K-2025.html` only (flagged as a synthesis question - "compared").

**T4. How much are Oracle's operating lease payments due in fiscal 2028?**
Expected: **$3,603 million**. Trap: 676 (finance leases).
Source: `ORCL-10K-2026.chunks.txt:3967`. Filter: `ORCL-10K-2026.html` only.

**T5. What was the recorded basis of Microsoft's U.S. government securities as of June 30, 2026?**
Expected: **$48,562 million**. Traps: 51,487 (the June 30, 2025 table), 49,714 (adjusted cost basis),
19,100 (a different table's "U.S. government and agency securities" row).
Source: `MSFT-10K-2026.chunks.txt:2752`. Filter: `MSFT-10K-2026.html` only.

**T6. What was the goodwill balance of Nasdaq's Financial Technology segment at December 31, 2025?**
Expected: **$7,952 million**. Traps: 4,285 / 2,134 (other segments), 14,371 (total), 5,933 (goodwill
recognized in the Adenza acquisition, also "assigned to" Financial Technology).
Source: `NDAQ-10K-2025.chunks.txt:15703`. Filter: `NDAQ-10K-2025.html` only.

**T7. What was the effect of the Ireland statutory tax rate difference on Microsoft's effective tax rate in fiscal year 2026?**
Expected: **(2.6)%** - a $4,301 million reduction. Trap: 21.0% (federal statutory rate).
Source: `MSFT-10K-2026.chunks.txt:4471`. Filter: `MSFT-10K-2026.html` only.

**T8. What average price per share did Nasdaq pay for shares it repurchased in November 2025?**
Expected: **$91.47** (760,264 shares). Traps: 88.59 (October), 89.24 (December), 89.40 (quarter).
Source: `NDAQ-10K-2025.chunks.txt:7781`. Filter: `NDAQ-10K-2025.html` only.

**T9. According to Oracle's statement of stockholders' equity, how much in common stock dividends did Oracle declare in fiscal 2025?**
Expected: **$4,743 million** ($1.70 per share). Traps: 4,391 (FY2024), 5,725 (FY2026).
Source: `ORCL-10K-2026.chunks.txt:2654`. Filter: `ORCL-10K-2026.html`, `equity_statement`.
A known gap in both strategies: a roll-forward row doesn't state its fiscal year - it follows only from
the "Balances as of May 31, 2024" row above it.

**T10. In Netflix's statement of comprehensive income, how much in cash flow hedge gains was reclassified in 2024?**
Expected: **$(96,795) thousand** - net gains of about $96.8 million. Traps: 68,962 (2025), 7,113 (fair
value hedges, 2024). Source: `NFLX-10K-2025.chunks.txt:1595`. Filter: `NFLX-10K-2025.html` only - "cash
flow hedge" contains the cash-flow-statement keyword, so the two statement keywords collide and the
statement filter is dropped.

**R1. What were Oracle's current deferred revenues as of May 31, 2026?** (routing test)
Expected: **$9,916 million**. Source: `ORCL-10K-2026.chunks.txt:2433` (balance sheet). Filter:
`ORCL-10K-2026.html`, `income_statement` - "revenues" routes it there, where the figure doesn't exist.

**R2. What was the operating income of Nasdaq's Capital Access Platforms segment in 2025?** (routing test)
Expected: **$1,274 million**. Source: `NDAQ-10K-2025.chunks.txt:18799` (segment note). Filter:
`NDAQ-10K-2025.html`, `income_statement` - "operating income" routes it away from the segment note.

**R3. What was Microsoft's Intelligent Cloud segment revenue?** (routing test - added in the pre-manual-pass review)
Expected: **a decline** - "revenue" routes it to the income statement, which reports only product and
service revenue, never segments. The figure exists elsewhere: **$137,791 million** (FY2026; $106,265
million FY2025). Source: `MSFT-10K-2026.chunks.txt:1312` (MD&A) and `:5405` (segment note). Filter:
`MSFT-10K-2026.html`, `income_statement`. A decline ("the excerpts ... do not break down revenue by
segment", as observed) is the correct outcome; any figure other than $137,791 million is a failure.
It's the README's "statement routing is a hard filter" limitation - see Decision-Log.md, "pre-manual-pass review".

## Variants (V1-V3) - added from the 2026-09-25 manual pass

Lines 38-40 of `tools/manual-questions.txt`. Each reproduced a finding of the pass.

**V1. What was Nasdaq's total comprehensive income for 2024?**
Expected: **$940 million** ("Comprehensive income"). Trap: **$942 million**, the "Comprehensive income
attributable to Nasdaq" line below it - the only line printed with "$", which the model took as the total for
both 2025 (Q10) and 2024 in the pass. Source: `NDAQ-10K-2025.chunks.txt:12523-12525`. Filter:
`NDAQ-10K-2025.html`, `comprehensive_income`.

**V2. What was Microsoft's comprehensive income for 2023?**
Negative test: fiscal 2023 is a real year the model may know from training, but the FY2026 filing covers
fiscal 2024-2026 only. Expected: a decline, ideally naming the years covered - not a figure from memory.
Filter: `MSFT-10K-2026.html`, `comprehensive_income`.

**V3. What was Netflix's net cash provided by operating activities, sum up the numbers?**
Asked-for arithmetic. The three figures ($10,149,273 / $7,361,364 / $7,274,301 thousand) sum to **$24,784,938
thousand**. In the pass `llama3.1:8b` produced 24,785,938 and 24,785,038 across four phrasings, never the right
sum; the operation should be shown so an error is visible. Filter: `NFLX-10K-2025.html`, `cash_flow_statement`.

## Held-out questions (H1-H15) - v2 step 0, written 2026-09-28

`tools/heldout-questions.txt`, same order. Written before any v2 output, from topics the questions above don't
touch, and chosen by the user from 20 drafted candidates without running any of them first. **Rules: never tune
against these** - no prompt, routing or chunking change is made because of a held-out answer; they are run only to
measure, alongside the main set. Expected filters are deliberately not recorded (predicting them means studying
v1's routing). Expected answers and traps come from `chunk-review/markdown/*.chunks.txt`, like the rest of this doc.

| # | Question | Expected | Traps | Source |
|---|---|---|---|---|
| H1 | What were Microsoft's research and development expenses in fiscal year 2026? | **$35,562 million** | 32,488 (FY2025) | MSFT `:1925` |
| H2 | What was Microsoft's effective tax rate for fiscal year 2026? | **19%** | 21% (federal statutory), 18% (FY2025) | MSFT `:1566` |
| H3 | How much stock-based compensation expense did Microsoft record in fiscal year 2026? | **$12,405 million** | 945 (a same-named deferred tax asset line) | MSFT `:5218` |
| H4 | How much did Microsoft's research and development expenses increase in fiscal 2026 compared to fiscal 2025, in dollars? | **$3,074 million** (35,562 - 32,488) | 9% (the stated change) | MSFT `:1925` |
| H5 | How many full-time employees did Oracle have? | **~141,000** as of May 31, 2026 (~49,000 U.S., ~92,000 international) | - | ORCL `:459` |
| H6 | What were Oracle's total research and development expenses in fiscal 2026? | **$10,272 million** | 7,467 (MD&A, excluding stock-based compensation), 2,805 | ORCL `:2503`, `:1843` |
| H7 | What were Oracle's diluted earnings per share for fiscal 2026? | **$5.83** | 2,914 (diluted shares), 4.34 (FY2025) | ORCL `:2533` |
| H8 | What was Oracle's total revenue in fiscal 2023? | **Decline** - the filing covers fiscal 2024-2026 | a figure from training data | ORCL (no FY2023 revenue) |
| H9 | What were Nasdaq's revenues less transaction-based expenses in 2025? | **$5,249 million** | total revenues (same statement) | NDAQ `:12459` |
| H10 | How many employees did Nasdaq have at the end of 2025? | **9,525** (Dec 31, 2025) | 9,162 (2024) | NDAQ `:8825` |
| H11 | Where is Nasdaq's U.S. headquarters? | **New York, New York** | Stockholm (European HQ) | NDAQ `:7672` |
| H12 | How many full-time employees did Netflix have at the end of 2025? | **~16,000** (~10,900 in the U.S. and Canada) | - | NFLX `:242` |
| H13 | What were Netflix's cash and cash equivalents at December 31, 2025? | **$9,033,681 thousand** | 7,804,733 (2024) | NFLX `:1740` |
| H14 | What was Netflix's total deferred revenue as of December 31, 2025? | **$1,776 million** (prose, in millions) | "thousand" carried over from the statements | NFLX `:2104` |
| H15 | How many paid memberships did Netflix have at the end of 2025? | **Decline** - the filing reports no membership count | a figure from training data | NFLX (no count) |

Types: lookalike labels H3, H6, H9, H11; units H13, H14; requested calculation H4; declines H8, H15; prose facts
H2, H5, H10, H12; plain figures H1, H7. Line numbers are into the v1 Markdown dumps and will move with v2's
ingestion; the figures won't.
