using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Structured;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Tests.Structured;

/// <summary>
/// Notes to the financial statements from the filer's text-block tags and Disclosure roles. Rules on
/// small fixtures; the four real filings (offline, from data/) for what was measured.
/// </summary>
[TestFixture]
public class NoteTopicsTests
{
    private static readonly string[] Filings = ["MSFT-10K-2026.html", "NDAQ-10K-2025.html", "NFLX-10K-2025.html", "ORCL-10K-2026.html"];

    // Two notes and a part-role block ("(Tables)") inside the first; a form-taxonomy block (cyd:) outside them.
    private const string Page =
        "<html><body><div style=\"display:none\"><ix:header><xbrli:context id=\"c\"><xbrli:period><xbrli:instant>2026-06-30</xbrli:instant></xbrli:period></xbrli:context></ix:header></div>"
        + "<ix:nonNumeric name=\"cyd:CybersecurityRiskBoardOfDirectorsOversightTextBlock\" contextRef=\"c\" escape=\"true\"><p>The Board oversees risk.</p></ix:nonNumeric>"
        + "<p>NOTES TO FINANCIAL STATEMENTS</p>"
        + "<p>1.</p><ix:nonNumeric name=\"us-gaap:IncomeTaxDisclosureTextBlock\" contextRef=\"c\" escape=\"true\" continuedAt=\"k1\"><p>INCOME TAXES</p><p>Our tax rate was 18%.</p>"
        + "<ix:nonNumeric name=\"us-gaap:ScheduleOfComponentsOfIncomeTaxExpenseBenefitTableTextBlock\" contextRef=\"c\" escape=\"true\"><p>Components follow.</p></ix:nonNumeric></ix:nonNumeric>"
        + "<p>[Table of Contents](#toc)</p><ix:continuation id=\"k1\"><p>Deferred taxes are recorded.</p></ix:continuation>"
        + "<p>2.</p><ix:nonNumeric name=\"us-gaap:DebtDisclosureTextBlock\" contextRef=\"c\" escape=\"true\"><p>DEBT</p><p>We issued notes.</p></ix:nonNumeric>"
        + "<p>Report of Independent Registered Public Accounting Firm</p></body></html>";

    private static XbrlTaxonomy Taxonomy() => new(
        [
            new XbrlRole("r/tax", "995567", "Disclosure", "INCOME TAXES"),
            new XbrlRole("r/taxt", "995568", "Disclosure", "INCOME TAXES (Tables)"),
            new XbrlRole("r/debt", "995557", "Disclosure", "DEBT"),
            new XbrlRole("r/cyd", "990001", "Disclosure", "Cybersecurity Risk Management"),
        ],
        new Dictionary<string, IReadOnlySet<string>>
        {
            ["r/tax"] = new HashSet<string> { "us-gaap:IncomeTaxDisclosureTextBlock" },
            ["r/taxt"] = new HashSet<string> { "us-gaap:ScheduleOfComponentsOfIncomeTaxExpenseBenefitTableTextBlock" },
            ["r/debt"] = new HashSet<string> { "us-gaap:DebtDisclosureTextBlock" },
            ["r/cyd"] = new HashSet<string> { "cyd:CybersecurityRiskBoardOfDirectorsOversightTextBlock" },
        },
        new Dictionary<string, string>());

    private static (IDocument Page, List<NoteSpan> Notes) Find()
    {
        IDocument page = new HtmlParser().ParseDocument(Page);
        return (page, NoteTopics.Find(page, InlineXbrlReader.Read(page), Taxonomy()));
    }

    [Test]
    public void Find_OutermostNoteBlocks_WithTheirRoleTitles_FormTaxonomyAndPartRolesLeftOut()
    {
        Assert.That(Find().Notes.Select(n => n.Topic), Is.EqualTo(new[] { "Income Taxes", "Debt" }));
    }

    [Test]
    public void Read_BlocksInsideANote_IncludingItsContinuationAndThePageBreakBefore_GetItsTopic()
    {
        (IDocument page, List<NoteSpan> notes) = Find();

        List<FilingBlock> blocks = FilingBlockReader.Read(page, notes);

        Dictionary<string, string?> topicOf = blocks.ToDictionary(b => b.Text, b => b.Topic);
        Assert.That(topicOf["The Board oversees risk."], Is.Null);
        Assert.That(topicOf["Our tax rate was 18%."], Is.EqualTo("Income Taxes"));
        Assert.That(topicOf["[Table of Contents](#toc)"], Is.EqualTo("Income Taxes"), "between a note's pieces");
        Assert.That(topicOf["Deferred taxes are recorded."], Is.EqualTo("Income Taxes"));
        Assert.That(topicOf["Report of Independent Registered Public Accounting Firm"], Is.Null);
    }

    // NDAQ and NFLX tag a note from its title, leaving its number outside ("2." + "SUMMARY OF SIGNIFICANT
    // ACCOUNTING"): the heading split off as a section of its own, 32 of them. And the Notes' own title, with Note 1's
    // number, was an 8-20 token title-only chunk in every filing.
    [Test]
    public void Read_NoteNumberOutsideTheTagAndTheNotesTitle_JoinTheNoteThatFollows()
    {
        (IDocument page, List<NoteSpan> notes) = Find();

        List<FilingBlock> blocks = FilingBlockReader.Read(page, notes);

        Dictionary<string, string?> topicOf = blocks.ToDictionary(b => b.Text, b => b.Topic);
        Assert.That(topicOf["NOTES TO FINANCIAL STATEMENTS"], Is.EqualTo("Income Taxes"));
        Assert.That(topicOf["1."], Is.EqualTo("Income Taxes"));
        Assert.That(topicOf["2."], Is.EqualTo("Debt"));
    }

    [Test]
    public void Split_EachNoteIsASectionHeadedByItsTopic()
    {
        (IDocument page, List<NoteSpan> notes) = Find();

        List<StructuredSection> sections = StructuredSections.Split(FilingBlockReader.Read(page, notes));

        Assert.That(sections.Select(s => s.Heading), Is.EqualTo(new[] { "(no heading)", "(no heading) > Income Taxes", "(no heading) > Debt", "(no heading)" }));
    }

    [TestCase("INCOME TAXES", "Income Taxes")]
    [TestCase("LEASES, OTHER COMMITMENTS AND CERTAIN CONTINGENCIES", "Leases, Other Commitments and Certain Contingencies")]
    [TestCase("ACCUMULATED OTHER COMPREHENSIVE INCOME (LOSS)", "Accumulated Other Comprehensive Income (Loss)")]
    [TestCase("Nasdaq Stockholders’ Equity", "Nasdaq Stockholders’ Equity")]
    public void TopicOf_AllCapitalTitles_BecomeTitleCase(string roleTitle, string topic)
    {
        Assert.That(NoteTopics.TopicOf(roleTitle), Is.EqualTo(topic));
    }

    // Counted in the four filings. NDAQ's revenue text block is one fact over two notes, "Revenue from Contracts with
    // Customers" and "Deferred Revenue", four notes apart - split into two, named in role order.
    [TestCase("MSFT-10K-2026.html", 18)]
    [TestCase("NDAQ-10K-2025.html", 20)]
    [TestCase("NFLX-10K-2025.html", 14)]
    [TestCase("ORCL-10K-2026.html", 15)]
    public void Find_RealFiling_EveryNoteOnce(string filing, int notes)
    {
        DirectoryInfo data = new(Path.Combine(RepoPaths.FindRoot(TestContext.CurrentContext.TestDirectory).FullName, "data"));
        IDocument page = new HtmlParser().ParseDocument(MarkItDownConverter.ReadFiling(new FileInfo(Path.Combine(data.FullName, filing))));

        List<NoteSpan> found = NoteTopics.Find(page, InlineXbrlReader.Read(page), TaxonomyReader.Read(TaxonomyReader.FindForFiling(page, data)!));

        Assert.That(found, Has.Count.EqualTo(notes));
        Assert.That(found.Select(n => n.Topic).Distinct().Count(), Is.EqualTo(notes));
        if (filing.StartsWith("NDAQ"))
        {
            Assert.That(found.Select(n => n.Topic), Does.Contain("Revenue from Contracts with Customers").And.Contain("Deferred Revenue"));
        }
    }
}
