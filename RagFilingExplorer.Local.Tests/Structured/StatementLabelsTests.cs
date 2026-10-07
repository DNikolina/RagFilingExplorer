using AngleSharp.Html.Parser;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Structured;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Tests.Structured;

/// <summary>
/// Statement types from the filer's Statement roles. Rules on small fixtures; the four real filings
/// (offline, from data/) for what was measured - every role maps to one type, and each filing gets exactly its
/// five statement tables.
/// </summary>
[TestFixture]
public class StatementLabelsTests
{
    private static readonly string[] Filings = ["MSFT-10K-2026.html", "NDAQ-10K-2025.html", "NFLX-10K-2025.html", "ORCL-10K-2026.html"];
    private static readonly string[] FiveTypes = ["balance_sheet", "cash_flow_statement", "comprehensive_income", "equity_statement", "income_statement"];

    // The five roles as MSFT titles them, each presenting its check concept plus the given extras.
    private static XbrlTaxonomy Taxonomy(Dictionary<string, string[]>? extra = null, string incomeTitle = "INCOME STATEMENTS", bool incomeHasEps = true)
    {
        (string Uri, string Title, string[] Concepts)[] roles =
        [
            ("r/is", incomeTitle, incomeHasEps ? ["us-gaap:EarningsPerShareBasic", "us-gaap:Revenues", "us-gaap:NetIncomeLoss"] : ["us-gaap:Revenues"]),
            ("r/ci", "COMPREHENSIVE INCOME STATEMENTS", ["us-gaap:ComprehensiveIncomeNetOfTax", "us-gaap:NetIncomeLoss"]),
            ("r/bs", "BALANCE SHEETS", ["us-gaap:Assets", "us-gaap:LiabilitiesAndStockholdersEquity"]),
            ("r/cf", "CASH FLOWS STATEMENTS", ["us-gaap:NetCashProvidedByUsedInOperatingActivities", "us-gaap:NetIncomeLoss"]),
            ("r/eq", "STOCKHOLDERS' EQUITY STATEMENTS", ["us-gaap:StatementEquityComponentsAxis", "us-gaap:StockholdersEquity"]),
        ];
        return new XbrlTaxonomy(
            roles.Select((r, i) => new XbrlRole(r.Uri, $"99{i}", "Statement", r.Title)).Append(new XbrlRole("r/bsp", "995", "Statement", "BALANCE SHEETS (Parenthetical)")).ToList(),
            roles.ToDictionary(r => r.Uri, r => (IReadOnlySet<string>)r.Concepts.Concat(extra?.GetValueOrDefault(r.Uri) ?? []).ToHashSet()),
            new Dictionary<string, string>());
    }

    private static string Table(params string[] concepts) =>
        "<table><tr><td></td><td>2026</td></tr>" + string.Concat(concepts.Select((c, i) =>
            $"<tr><td>Line {i}</td><td><ix:nonFraction name=\"{c}\" contextRef=\"c\" unitRef=\"usd\">{i + 1}</ix:nonFraction></td></tr>")) + "</table>";

    private static List<FilingBlock> Blocks(string body) => FilingBlockReader.Read(new HtmlParser().ParseDocument($"<html><body>{body}</body></html>"));

    private static string AllStatementTables() =>
        Table("us-gaap:EarningsPerShareBasic", "us-gaap:Revenues", "us-gaap:NetIncomeLoss")
        + Table("us-gaap:ComprehensiveIncomeNetOfTax", "us-gaap:NetIncomeLoss")
        + Table("us-gaap:Assets", "us-gaap:LiabilitiesAndStockholdersEquity")
        + Table("us-gaap:NetCashProvidedByUsedInOperatingActivities", "us-gaap:NetIncomeLoss")
        + Table("us-gaap:StockholdersEquity");

    [Test]
    public void MapRoles_TitleConfirmedByConcepts_MapsEachPrimaryStatementAndSkipsParentheticals()
    {
        Dictionary<string, string> types = StatementLabels.MapRoles(Taxonomy());

        Assert.That(types.Keys, Is.EquivalentTo(new[] { "r/is", "r/ci", "r/bs", "r/cf", "r/eq" }));
        Assert.That(types["r/ci"], Is.EqualTo("comprehensive_income"), "a comprehensive income title also contains INCOME");
        Assert.That(types["r/is"], Is.EqualTo("income_statement"));
    }

    [TestCase("CONSOLIDATED STATEMENTS OF OPERATIONS")]
    [TestCase("Consolidated Statements of Income")]
    public void MapRoles_OtherFilersIncomeTitles_AreTheIncomeStatement(string title)
    {
        Assert.That(StatementLabels.MapRoles(Taxonomy(incomeTitle: title))["r/is"], Is.EqualTo("income_statement"));
    }

    // Many filers present one combined statement. Its title reads as comprehensive income, so it used to leave
    // income_statement missing and stop the build.
    [Test]
    public void MapRoles_CombinedOperationsAndComprehensiveIncome_IsTheIncomeStatement()
    {
        XbrlTaxonomy separate = Taxonomy();
        List<XbrlRole> roles = separate.Roles.Where(r => r.Uri is not ("r/is" or "r/ci")).ToList();
        roles.Insert(0, new XbrlRole("r/combined", "990", "Statement", "CONSOLIDATED STATEMENTS OF OPERATIONS AND COMPREHENSIVE INCOME"));
        Dictionary<string, IReadOnlySet<string>> presented = separate.PresentedConcepts.Where(p => p.Key is not ("r/is" or "r/ci")).ToDictionary();
        presented["r/combined"] = new HashSet<string> { "us-gaap:EarningsPerShareBasic", "us-gaap:NetIncomeLoss", "us-gaap:ComprehensiveIncomeNetOfTax" };

        Dictionary<string, string> types = StatementLabels.MapRoles(new XbrlTaxonomy(roles, presented, separate.Labels));

        Assert.That(types["r/combined"], Is.EqualTo("income_statement"));
        Assert.That(types.Values, Has.None.EqualTo("comprehensive_income"));
    }

    [Test]
    public void MapRoles_TitleNotConfirmedByItsConcepts_FailsLoudly()
    {
        Assert.Throws<InvalidOperationException>(() => StatementLabels.MapRoles(Taxonomy(incomeHasEps: false)));
    }

    [Test]
    public void MapRoles_TitleMatchingNoType_FailsLoudly()
    {
        Assert.Throws<InvalidOperationException>(() => StatementLabels.MapRoles(Taxonomy(incomeTitle: "CONSOLIDATED STATEMENTS OF RESULTS")));
    }

    // NFLX: its segment table repeats income-statement lines per segment and covers 53% of the operations role -
    // more than the equity statements cover of theirs (50%) - so it must lose its role to the statement (79%),
    // not pass a threshold.
    [Test]
    public void Label_NoteTableSharingAStatementsLines_LosesTheRoleToTheStatement()
    {
        List<FilingBlock> blocks = StatementLabels.Label(Blocks(Table("us-gaap:Revenues", "us-gaap:NetIncomeLoss") + AllStatementTables()), Taxonomy());

        List<string?> types = blocks.OfType<TableBlock>().Select(t => t.StatementType).ToList();
        Assert.That(types, Is.EqualTo(new string?[] { null, "income_statement", "comprehensive_income", "balance_sheet", "cash_flow_statement", "equity_statement" }));
    }

    [Test]
    public void Label_StatementWithNoTableCoveringIt_FailsLoudly()
    {
        string withoutBalanceSheet = Table("us-gaap:EarningsPerShareBasic", "us-gaap:Revenues", "us-gaap:NetIncomeLoss")
            + Table("us-gaap:ComprehensiveIncomeNetOfTax", "us-gaap:NetIncomeLoss")
            + Table("us-gaap:NetCashProvidedByUsedInOperatingActivities", "us-gaap:NetIncomeLoss")
            + Table("us-gaap:StockholdersEquity");

        Assert.Throws<InvalidOperationException>(() => StatementLabels.Label(Blocks(withoutBalanceSheet), Taxonomy()));
    }

    [Test]
    public void StatementType_ChunkWithAStatementTablePiece_IsThatStatement_OtherwiseNarrative()
    {
        List<FilingBlock> blocks = StatementLabels.Label(Blocks("<p>BALANCE SHEETS</p>" + AllStatementTables()), Taxonomy());
        TableBlock balanceSheet = blocks.OfType<TableBlock>().Single(t => t.StatementType == "balance_sheet");

        Assert.That(new StructuredChunk("h", "c", 1, [blocks[0], balanceSheet]).StatementType, Is.EqualTo("balance_sheet"));
        Assert.That(new StructuredChunk("h", "c", 1, [blocks[0]]).StatementType, Is.EqualTo("narrative"));
        Assert.Throws<InvalidOperationException>(() => _ = new StructuredChunk("h", "c", 1, blocks.OfType<TableBlock>().Take(2).ToList()).StatementType);
    }

    [TestCaseSource(nameof(Filings))]
    public void Read_RealFiling_LabelsExactlyItsFiveStatementTables(string filing)
    {
        FileInfo file = new(Path.Combine(RepoPaths.FindRoot(TestContext.CurrentContext.TestDirectory).FullName, "data", filing));
        StructuredChunkingStrategy strategy = new(Microsoft.ML.Tokenizers.TiktokenTokenizer.CreateForModel("gpt-4"), 500, 50);

        StructuredFiling read = strategy.Read(file);

        List<string> labelled = read.Sections.SelectMany(s => s.Blocks).OfType<TableBlock>().Select(t => t.StatementType).OfType<string>().ToList();
        Assert.That(labelled, Is.EquivalentTo(FiveTypes));
        Assert.That(read.Chunks.Select(c => c.StatementType).Distinct(), Is.EquivalentTo(FiveTypes.Append("narrative")));
    }
}
