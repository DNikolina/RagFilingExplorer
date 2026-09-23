using System.Text.RegularExpressions;

namespace RagFilingExplorer.Local.Chunking;

/// <summary>
/// Detects which financial statement a chunk belongs to (income statement, balance sheet, cash flow
/// statement, equity statement, comprehensive income) from standalone title lines in the converted
/// text, via <see cref="Detect"/>, so chunks can be tagged and filtered by statement type - not just
/// by filing. <see cref="IsNotesToFinancialStatementsBoundary"/> is the other half of that: it flags
/// the line where the primary statements end and the Notes section begins, so the caller can reset a
/// carried-forward statement type there instead of letting it leak into every subsequent chunk.
///
/// The three filings in this project use three different naming conventions for the exact same
/// statements, confirmed by direct inspection - a detector tuned to only one would silently miss the
/// others:
/// - ORCL: "CONSOLIDATED STATEMENTS OF OPERATIONS", "CONSOLIDATED BALANCE SHEETS",
///   "CONSOLIDATED STATEMENTS OF CASH FLOWS", "CONSOLIDATED STATEMENTS OF STOCKHOLDERS' EQUITY"
/// - MSFT: "INCOME STATEMENTS", "BALANCE SHEETS", "CASH FLOWS STATEMENTS",
///   "STOCKHOLDERS' EQUITY STATEMENTS" - no "CONSOLIDATED" prefix at all, different word order.
/// - NDAQ: "Consolidated Statements of Income" - title case, and "Income" instead of "Operations".
/// </summary>
internal static partial class StatementTypeDetector
{
    // Detect() checks ComprehensiveIncomeRegex before IncomeStatementRegex, but - verified directly,
    // not just assumed - that ordering isn't actually load-bearing: both regexes now require
    // "STATEMENTS" to appear (see IncomeStatementRegex's and ComprehensiveIncomeRegex's own comments
    // below for why), which already makes them mutually exclusive on every convention seen.
    // IncomeStatementRegex does not match "STATEMENTS OF COMPREHENSIVE INCOME" or "COMPREHENSIVE
    // INCOME STATEMENTS" in either order - confirmed with a direct regex test, not assumed from a
    // resemblance between the two patterns. The order is kept anyway as cheap, harmless precaution.
    //
    // "BALANCE SHEETS" is intentionally NOT required to contain "STATEMENTS" - unlike every other
    // statement type, the real title never includes that word in any of the three conventions seen
    // ("CONSOLIDATED BALANCE SHEETS" / "BALANCE SHEETS"), so there's nothing stricter to require here.
    [GeneratedRegex(@"^(CONSOLIDATED\s+)?(STATEMENTS?\s+OF\s+)?BALANCE\s+SHEETS?$", RegexOptions.IgnoreCase)]
    private static partial Regex BalanceSheetRegex();

    // "STATEMENTS" required (prefix or suffix) - "Cash Flows" alone is also used as a bare MD&A
    // subsection heading (e.g. "Cash Flows" before a narrative discussion of the trends), confirmed
    // by inspecting rag.db directly: it mistagged 14 chunks inside Item 7's MD&A before the real
    // statement title appeared in Item 8.
    [GeneratedRegex(@"^(CONSOLIDATED\s+)?(STATEMENTS?\s+OF\s+CASH\s+FLOWS?|CASH\s+FLOWS?\s+STATEMENTS?)$", RegexOptions.IgnoreCase)]
    private static partial Regex CashFlowRegex();

    // Same "STATEMENTS" requirement, applied precautionarily - the real titles observed always
    // include it ("...STATEMENTS OF STOCKHOLDERS' EQUITY" / "STOCKHOLDERS' EQUITY STATEMENTS"), so
    // there's no evidence-based reason to leave it optional here either.
    [GeneratedRegex(@"^(CONSOLIDATED\s+)?(STATEMENTS?\s+OF\s+(STOCKHOLDERS|SHAREHOLDERS).{0,3}\s+EQUITY|(STOCKHOLDERS|SHAREHOLDERS).{0,3}\s+EQUITY\s+STATEMENTS?)$", RegexOptions.IgnoreCase)]
    private static partial Regex EquityRegex();

    // "STATEMENTS" must appear somewhere (prefix "STATEMENTS OF X" or suffix "X STATEMENTS") - the
    // word "STATEMENTS" was previously fully optional here to accommodate MSFT's "INCOME STATEMENTS"
    // convention, but that made a bare, unrelated line reading just "COMPREHENSIVE INCOME" match too.
    [GeneratedRegex(@"^(CONSOLIDATED\s+)?(STATEMENTS?\s+OF\s+COMPREHENSIVE\s+INCOME|COMPREHENSIVE\s+INCOME\s+STATEMENTS?)$", RegexOptions.IgnoreCase)]
    private static partial Regex ComprehensiveIncomeRegex();

    // Same fix, more critical here: without requiring "STATEMENTS" somewhere, this matched a bare
    // line reading just "OPERATIONS" - a normal business-section subheading ("Devices face
    // competition... OPERATIONS We have a global operations service center...") completely unrelated
    // to the income statement. Confirmed by inspecting rag.db directly: this false positive mistagged
    // 96 consecutive chunks (all of Items 1-7) as "income_statement" before the real title line ever
    // appeared in Item 8.
    [GeneratedRegex(@"^(CONSOLIDATED\s+)?(STATEMENTS?\s+OF\s+(OPERATIONS|INCOME)|(OPERATIONS|INCOME)\s+STATEMENTS?)$", RegexOptions.IgnoreCase)]
    private static partial Regex IncomeStatementRegex();

    // All three filings title the section right after the five primary statements "NOTES TO
    // (CONSOLIDATED) FINANCIAL STATEMENTS" - a consistent, filer-agnostic reset point. Without this,
    // the "carry the last detected type forward" logic in Program.cs's BuildRecords has nothing to
    // reset on: whichever statement type was detected last (typically the equity statement, since it's
    // conventionally the final one of the five) silently "leaks" across every Note chunk for the rest
    // of the filing - confirmed by inspecting a --verbose retrieval dump directly, where Notes chunks
    // about employee stock plans, leases, and even later Items (12, 15, 16) were all tagged
    // equity_statement, burying the real equity-statement numbers among hundreds of unrelated chunks.
    [GeneratedRegex(@"^NOTES\s+TO\s+(CONSOLIDATED\s+)?FINANCIAL\s+STATEMENTS$", RegexOptions.IgnoreCase)]
    private static partial Regex NotesToFinancialStatementsRegex();

    /// <summary>
    /// True if this line is the "Notes to (Consolidated) Financial Statements" boundary - the caller
    /// should reset any carried-forward statement type to null (narrative) here, rather than treat it
    /// as a new statement type of its own.
    /// </summary>
    public static bool IsNotesToFinancialStatementsBoundary(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length > 0 && trimmed.Length <= 60 && NotesToFinancialStatementsRegex().IsMatch(trimmed);
    }

    public static string? Detect(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 60)
        {
            return null;
        }

        if (BalanceSheetRegex().IsMatch(trimmed))
        {
            return "balance_sheet";
        }

        if (CashFlowRegex().IsMatch(trimmed))
        {
            return "cash_flow_statement";
        }

        if (EquityRegex().IsMatch(trimmed))
        {
            return "equity_statement";
        }

        if (ComprehensiveIncomeRegex().IsMatch(trimmed))
        {
            return "comprehensive_income";
        }

        if (IncomeStatementRegex().IsMatch(trimmed))
        {
            return "income_statement";
        }

        return null;
    }
}
