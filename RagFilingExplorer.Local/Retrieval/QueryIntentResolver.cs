namespace RagFilingExplorer.Local.Retrieval;

/// <summary>
/// Resolves a user's question two ways before <see cref="RagAnswerService"/> acts on it: to a metadata
/// filter (which filing, which financial statement) via <see cref="ResolveFiling"/> and
/// <see cref="ResolveStatementType"/>, and to a reasoning-worthiness signal via
/// <see cref="RequiresSynthesis"/>.
///
/// Metadata filtering (Microsoft's own retrieval-quality guidance ranks this above chunk-size/text
/// tweaks) directly targets the cross-company and cross-statement contamination seen repeatedly in
/// Step 7 testing (e.g. an MSFT-specific question pulling in ORCL chunks, or a single filing's many
/// similarly-shaped "Item 15" tables burying the right one). <c>ResolveFiling</c> and
/// <c>ResolveStatementType</c> both require exactly one match to act - zero or ambiguous (2+) matches
/// resolve to null, leaving the search unfiltered rather than guessing.
///
/// <c>RequiresSynthesis</c> has different semantics on purpose: it's an any-match keyword check (not
/// exactly-one), used to decide whether a reasoning model's "thinking" phase is worth its cost for this
/// question - see its own doc comment and <c>RagAnswerService.AskAsync</c>.
/// </summary>
internal static class QueryIntentResolver
{
    // Adding a filing to data/ is not enough on its own for company-scoped filtering to work for it -
    // it also needs an entry here, or ResolveFiling silently returns null for every question naming it
    // and the search runs unfiltered across every filing instead. Confirmed the hard way onboarding
    // NFLX-10K-2025.html: every Netflix question searched all four filings' income_statement chunks at
    // once (the exact cross-company contamination this filter exists to prevent) until this was added.
    // FindRegistrationProblems (checked at startup) now warns about exactly this.
    private static readonly Dictionary<string, string> CompanyToFiling = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft"] = "MSFT-10K-2026.html",
        ["MSFT"] = "MSFT-10K-2026.html",
        ["Oracle"] = "ORCL-10K-2026.html",
        ["ORCL"] = "ORCL-10K-2026.html",
        ["Nasdaq"] = "NDAQ-10K-2025.html",
        ["NDAQ"] = "NDAQ-10K-2025.html",
        ["Netflix"] = "NFLX-10K-2025.html",
        ["NFLX"] = "NFLX-10K-2025.html",
    };

    private static readonly Dictionary<string, string[]> StatementTypeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["income_statement"] = ["revenue", "revenues", "gross margin", "gross profit", "cost of revenue", "operating income", "operating margin", "net income", "earnings per share"],
        // A plain "(total) stockholders' equity" question is about a period-end value, which the balance
        // sheet states as one clean "Total stockholders' equity" row in every filing. It used to route to
        // the equity statement, a wide roll-forward table: ORCL's splits into 15 near-identical
        // fragments, and the one holding the closing balance ranked 8th-9th of 17, outside the top 5 -
        // so "What was Oracle's total stockholders' equity?" failed, both before and after the second
        // review's changes (confirmed by rebuilding the initial commit's index side by side). Questions
        // about *changes* in equity still go to the equity statement; ResolveStatementType's
        // longest-match rule lets "changes in stockholders' equity" win over the "stockholders' equity"
        // it contains.
        ["balance_sheet"] = ["total assets", "total liabilities", "balance sheet", "stockholders equity", "shareholders equity", "stockholders' equity", "shareholders' equity", "total equity"],
        // "cash from operations" covers MSFT's own line label ("Net cash from operations"), which none
        // of the other phrases matched - that question used to run with no statement filter at all.
        ["cash_flow_statement"] = ["cash flow", "operating activities", "financing activities", "investing activities", "cash from operations"],
        ["equity_statement"] =
        [
            "changes in stockholders equity", "changes in shareholders equity", "changes in stockholders' equity",
            "changes in shareholders' equity", "changes in equity", "statement of stockholders' equity",
            "statements of stockholders' equity", "stockholders' equity statement", "equity statement",
        ],
        ["comprehensive_income"] = ["comprehensive income", "other comprehensive income"],
    };

    // Questions that need genuine multi-step reasoning (comparing/deriving across figures) rather than
    // a single-fact lookup - used to decide when a reasoning model's "thinking" phase is actually worth
    // its cost (see RagAnswerService.AskAsync). Deliberately conservative: false negatives just mean a
    // synthesis question gets answered without extended reasoning (same as today), not a wrong filter.
    private static readonly string[] SynthesisKeywords =
    [
        "compare", "comparison", "versus", " vs ", " vs.", "difference between", "trend", "ratio",
        "calculate", "growth rate", "year-over-year", "year over year", "combined", "across",
    ];

    public static bool RequiresSynthesis(string question) =>
        SynthesisKeywords.Any(keyword => question.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    public static string? ResolveFiling(string question)
    {
        string[] matched = CompanyToFiling
            .Where(kvp => question.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Value)
            .Distinct()
            .ToArray();

        return matched.Length == 1 ? matched[0] : null;
    }

    /// <summary>
    /// Cross-checks <see cref="CompanyToFiling"/> against the filings actually present in data/, so the
    /// NFLX onboarding bug (a filing with no entry here quietly running every question unfiltered) is
    /// reported at startup instead of discovered through a hallucinated answer. Also flags entries
    /// pointing at a filing that no longer exists, which would filter a search down to zero chunks.
    /// </summary>
    public static List<string> FindRegistrationProblems(IEnumerable<string> filingNames)
    {
        HashSet<string> present = new(filingNames, StringComparer.OrdinalIgnoreCase);
        HashSet<string> registered = new(CompanyToFiling.Values, StringComparer.OrdinalIgnoreCase);
        List<string> problems = new();

        foreach (string filing in present.Where(f => !registered.Contains(f)).Order())
        {
            problems.Add($"data/{filing} has no entry in QueryIntentResolver.CompanyToFiling - questions naming "
                + "this company will search every filing unfiltered. Add its company name and ticker there.");
        }

        foreach (string filing in registered.Where(f => !present.Contains(f)).Order())
        {
            problems.Add($"QueryIntentResolver.CompanyToFiling maps to {filing}, which isn't in data/ - questions "
                + "naming that company will be filtered to a filing with no chunks.");
        }

        return problems;
    }

    // Longest match wins: a matched keyword that is part of a longer matched keyword is ignored, so
    // "changes in stockholders' equity" (equity_statement) isn't made ambiguous by the
    // "stockholders' equity" (balance_sheet) inside it. Genuinely different matches ("revenue" and
    // "total assets") still make the question ambiguous and resolve to null, as before.
    public static string? ResolveStatementType(string question)
    {
        (string Type, string Keyword)[] matches = StatementTypeKeywords
            .SelectMany(kvp => kvp.Value.Select(keyword => (Type: kvp.Key, Keyword: keyword)))
            .Where(m => question.Contains(m.Keyword, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        string[] matched = matches
            .Where(m => !matches.Any(other => other.Keyword.Length > m.Keyword.Length
                && other.Keyword.Contains(m.Keyword, StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.Type)
            .Distinct()
            .ToArray();

        return matched.Length == 1 ? matched[0] : null;
    }
}
