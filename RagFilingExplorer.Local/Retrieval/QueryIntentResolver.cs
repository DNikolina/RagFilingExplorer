namespace RagFilingExplorer.Local.Retrieval;

/// <summary>
/// Resolves a user's question two ways before <see cref="RagAnswerService"/> acts on it: to a statement-type
/// filter via <see cref="ResolveStatementType"/> (the company filter is <see cref="CompanyRegistry"/>'s, built
/// from the filings' cover facts), and to a reasoning-worthiness signal via <see cref="RequiresSynthesis"/>.
///
/// Metadata filtering (Microsoft's own retrieval-quality guidance ranks this above chunk-size/text
/// tweaks) directly targets the cross-company and cross-statement contamination seen repeatedly in
/// Step 7 testing (e.g. an MSFT-specific question pulling in ORCL chunks, or a single filing's many
/// similarly-shaped "Item 15" tables burying the right one). <c>ResolveStatementType</c> requires exactly
/// one statement type to act - zero or ambiguous matches resolve to null, leaving that dimension
/// unfiltered rather than guessing.
///
/// <c>RequiresSynthesis</c> has different semantics on purpose: it's an any-match keyword check (not
/// exactly-one), used to decide whether a reasoning model's "thinking" phase is worth its cost for this
/// question - see its own doc comment and <c>RagAnswerService.AskAsync</c>.
/// </summary>
internal static class QueryIntentResolver
{
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
