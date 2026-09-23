# Manual test questions

Step 7 in [Implementation_Plan.md](Implementation_Plan.md) verified 6 questions (3 number-lookups, one
per filing; one prose fact; one prose description; one negative test) and got 6/6 after the
statement-type filtering fix. This is a second, broader round for manual testing before publishing -
it deliberately covers statement types and companies the original 6 didn't touch, especially
`comprehensive_income`, which only just got a keyword mapping in `QueryIntentResolver`.

Netflix (`NFLX-10K-2025.html`) was added later, alongside MSFT/ORCL/NDAQ - see Implementation_Plan.md's
"Follow-up: onboarding a new filer (NFLX)" for the three real bugs that surfaced onboarding it. Its
questions (17-20 below) are the regression check for that work.

Every expected answer below was pulled directly from `chunk-review/*.chunks.txt` (the actual converted
filing text, not memory/prior knowledge), same as Step 7's own verification method. The "Source"
line is where to check the figure yourself if an answer looks off.

For each question, also check that the printed `(filtering to ..., statement type: ...)` line
matches the expected filter - a wrong filter that happens to still retrieve the right chunk is a
result worth flagging even if the final answer is correct.

## Balance sheet

**1. What were Microsoft's total assets?**
Expected: **$758,376 million** (FY2026; $619,003 million FY2025).
Source: `MSFT-10K-2026.chunks.txt:2031`. Filter: `MSFT-10K-2026.html`, `balance_sheet`.

**2. What was Oracle's total stockholders' equity?**
Expected: **$43,056 million** (FY2026; $20,969 million FY2025).
Source: `ORCL-10K-2026.chunks.txt:2475`. Filter: `ORCL-10K-2026.html`, `balance_sheet`.
(Careful: ORCL's chunks also contain an unrelated "Total assets" fair-value table around line 3209 -
a wrong retrieval landing there instead of the real balance sheet is worth noting.)

**3. What were Nasdaq's total liabilities?**
Expected: **$18,821 million** (2025; $19,195 million 2024).
Source: `NDAQ-10K-2025.chunks.txt:12440`. Filter: `NDAQ-10K-2025.html`, `balance_sheet`.

## Cash flow statement

**4. What was Microsoft's net cash from operations?**
Expected: **$182,935 million** (FY2026; $136,162 million FY2025; $118,548 million FY2024).
Source: `MSFT-10K-2026.chunks.txt:2127`. Filter: `MSFT-10K-2026.html`, `cash_flow_statement`.

**5. What was Oracle's net cash provided by operating activities?**
Expected: **$31,977 million** (FY2026; $20,821 million FY2025; $18,673 million FY2024).
Source: `ORCL-10K-2026.chunks.txt:2770`. Filter: `ORCL-10K-2026.html`, `cash_flow_statement`.

**6. What was Nasdaq's net cash provided by operating activities in 2025?**
Expected: **$2,255 million** (2025; $1,939 million 2024; $1,696 million 2023).
Source: `NDAQ-10K-2025.chunks.txt:12681`. Filter: `NDAQ-10K-2025.html`, `cash_flow_statement`.

## Equity statement

**7. What was Microsoft's total stockholders' equity?**
Expected: **$442,387 million** (FY2026; $343,479 million FY2025; $268,477 million FY2024).
Source: `MSFT-10K-2026.chunks.txt:2239`. Filter: `MSFT-10K-2026.html`, `equity_statement`.

## Comprehensive income (new filter - watch this one closely)

**8. What was Microsoft's comprehensive income?**
Expected: **$133,812 million** (FY2026; $104,075 million FY2025; $88,889 million FY2024).
Source: `MSFT-10K-2026.chunks.txt:1986`. Filter: `MSFT-10K-2026.html`, `comprehensive_income`.

**9. What was Oracle's comprehensive income?**
Expected: **$16,882 million** (FY2026; $12,700 million FY2025; $10,557 million FY2024).
Source: `ORCL-10K-2026.chunks.txt:2577`. Filter: `ORCL-10K-2026.html`, `comprehensive_income`.

**10. What was Nasdaq's total comprehensive income for 2025?**
Expected: **$2,113 million** (or **$2,114 million** "attributable to Nasdaq" - either figure, if cited
correctly to the right line, counts as correct; 2024 was $940M / $942M).
Source: `NDAQ-10K-2025.chunks.txt:12549-12551`. Filter: `NDAQ-10K-2025.html`, `comprehensive_income`.

## Prose facts (not previously tested)

**11. How many people did Microsoft employ, and where?**
Expected: **approximately 223,000 full-time employees as of June 30, 2026** - 121,000 in the U.S.,
102,000 internationally.
Source: `MSFT-10K-2026.chunks.txt:487`. No statement-type filter expected (narrative chunk).

**12. Where is Oracle headquartered?**
Expected: **Austin, Texas** (address of principal executive offices).
Source: `ORCL-10K-2026.chunks.txt:35`. No statement-type filter expected.

**13. Who is Nasdaq's independent registered public accounting firm?**
Expected: **Ernst & Young LLP**.
Source: `NDAQ-10K-2025.chunks.txt:11414`. No statement-type filter expected.

## Netflix (added later - regression check for the onboarding fixes)

**17. What was Netflix's total revenue for fiscal year 2025?**
Expected: **$45,183,036 thousand** (~$45.18 billion; FY2024: $39,000,966 thousand; FY2023:
$33,723,297 thousand).
Source: `NFLX-10K-2025.chunks.txt` (Consolidated Statements of Operations). Filter:
`NFLX-10K-2025.html`, `income_statement`.
Note: the citation will say `PART IV > Item 16. Form 10-K Summary`, not `Item 8` - that's expected,
not a bug. Netflix's actual financial statement pages are physically attached later in the converted
document than its `Item 8` heading line, so `SectionSplitter` attributes them to whichever Item
heading came last in document order (the same "Item 8 is a stub, the real pages live elsewhere"
pattern already seen with ORCL's Item 15). See Implementation_Plan.md's "Follow-up: onboarding a new
filer (NFLX)" for the full explanation - and don't mistake this label for a sign the wrong number came
back; check the dollar figure itself.

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
Expected: **Los Gatos, California**.
No statement-type filter expected (narrative chunk).

## Edge cases

**14. Compare Microsoft's and Oracle's total revenue.**
Two companies named -> `QueryIntentResolver.ResolveFiling` should return `null` (ambiguous), so this
should run **unfiltered** - worth confirming the `(filtering to ...)` line does NOT appear, and that
the answer still correctly cites both MSFT ($331,839M) and ORCL ($67,357M) figures rather than only
whichever chunk happened to rank first. This question also matches `RequiresSynthesis` (contains
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
