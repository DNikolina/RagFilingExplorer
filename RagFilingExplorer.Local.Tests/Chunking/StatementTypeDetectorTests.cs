using RagFilingExplorer.Local.Chunking;

namespace RagFilingExplorer.Local.Tests.Chunking;

/// <summary>
/// Regression coverage for two distinct real bugs found in this detector, both via direct inspection
/// of what it actually tagged rather than assuming the regexes were correct:
/// (1) an earlier version made "STATEMENTS" fully optional, which caused bare, unrelated subsection
/// headings (e.g. a lone "OPERATIONS" line in MSFT's business narrative) to be mistagged as
/// financial-statement titles - 96 consecutive chunks in one case, confirmed by direct SQL inspection
/// of rag.db; and (2) the carried-forward statement type had no reset boundary for leaving the primary
/// statements and entering the Notes section, so whichever type was detected last leaked across every
/// subsequent chunk for the rest of the filing - fixed by <see cref="StatementTypeDetector.IsNotesToFinancialStatementsBoundary"/>.
/// See StatementTypeDetector's own doc comments for the full history of both.
/// </summary>
[TestFixture]
public class StatementTypeDetectorTests
{
    // Real title conventions observed across the three filers in this project.
    [TestCase("CONSOLIDATED STATEMENTS OF OPERATIONS", "income_statement")] // ORCL
    [TestCase("INCOME STATEMENTS", "income_statement")] // MSFT
    [TestCase("Consolidated Statements of Income", "income_statement")] // NDAQ
    [TestCase("CONSOLIDATED BALANCE SHEETS", "balance_sheet")] // ORCL
    [TestCase("BALANCE SHEETS", "balance_sheet")] // MSFT
    [TestCase("CONSOLIDATED STATEMENTS OF CASH FLOWS", "cash_flow_statement")] // ORCL
    [TestCase("CASH FLOWS STATEMENTS", "cash_flow_statement")] // MSFT
    [TestCase("CONSOLIDATED STATEMENTS OF STOCKHOLDERS' EQUITY", "equity_statement")] // ORCL
    [TestCase("STOCKHOLDERS' EQUITY STATEMENTS", "equity_statement")] // MSFT
    [TestCase("STATEMENTS OF COMPREHENSIVE INCOME", "comprehensive_income")]
    [TestCase("COMPREHENSIVE INCOME STATEMENTS", "comprehensive_income")]
    [TestCase("consolidated statements of operations", "income_statement")] // case-insensitivity
    public void Detect_RealTitleLine_ReturnsExpectedType(string line, string expected)
    {
        Assert.That(StatementTypeDetector.Detect(line), Is.EqualTo(expected));
    }

    // Confirmed false positives from the bug this session found and fixed - bare subsection headings
    // that happen to contain a statement-type keyword but are not statement titles.
    [TestCase("OPERATIONS")] // MSFT business-section subheading; mistagged 96 chunks
    [TestCase("Cash Flows")] // MD&A narrative subsection heading; mistagged 14 chunks
    [TestCase("COMPREHENSIVE INCOME")] // bare, no "STATEMENTS"
    [TestCase("INCOME")]
    public void Detect_BareKeywordLine_ReturnsNull(string line)
    {
        Assert.That(StatementTypeDetector.Detect(line), Is.Null);
    }

    [Test]
    public void Detect_EmptyLine_ReturnsNull()
    {
        Assert.That(StatementTypeDetector.Detect(string.Empty), Is.Null);
    }

    [Test]
    public void Detect_LineOverSixtyCharacters_ReturnsNull()
    {
        string longLine = "CONSOLIDATED STATEMENTS OF OPERATIONS" + new string('x', 30);
        Assert.That(StatementTypeDetector.Detect(longLine), Is.Null);
    }

    // Regression coverage for a second, distinct carry-forward bug found via a --verbose retrieval
    // dump: with no reset boundary, the last statement type detected (conventionally the equity
    // statement, the final one of the five) leaked across every Note-to-financial-statements chunk -
    // and beyond, into later Items - for the rest of the filing, burying the real per-statement figures
    // among hundreds of unrelated chunks that all shared the same (wrong) StatementType tag.
    [TestCase("NOTES TO FINANCIAL STATEMENTS")] // MSFT
    [TestCase("NOTES TO CONSOLIDATED FINANCIAL STATEMENTS")] // ORCL
    [TestCase("Notes to Consolidated Financial Statements")] // NDAQ
    public void IsNotesToFinancialStatementsBoundary_RealTitleLine_ReturnsTrue(string line)
    {
        Assert.That(StatementTypeDetector.IsNotesToFinancialStatementsBoundary(line), Is.True);
    }

    [TestCase("NOTE 15 — STOCKHOLDERS' EQUITY")] // an individual note, not the notes section title itself
    [TestCase("CONSOLIDATED STATEMENTS OF OPERATIONS")]
    [TestCase("")]
    public void IsNotesToFinancialStatementsBoundary_NonBoundaryLine_ReturnsFalse(string line)
    {
        Assert.That(StatementTypeDetector.IsNotesToFinancialStatementsBoundary(line), Is.False);
    }
}
