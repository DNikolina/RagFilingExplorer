using RagFilingExplorer.Local.Chunking;

namespace RagFilingExplorer.Tests.Chunking;

[TestFixture]
public class SectionSplitterTests
{
    // NFLX and NDAQ place their financial statements after "Item 16. Form 10-K Summary - None.", so every
    // statement, auditor's report and Note (249 NFLX chunks, 110 NDAQ) was headed "Item 16. Form 10-K
    // Summary" - in the embedding text and in the model's citations. ORCL's exhibit index and every
    // filer's signatures landed there too.
    [TestCase("INDEX TO FINANCIAL STATEMENTS", "PART IV > Financial Statements")] // NFLX
    [TestCase("INDEX TO CONSOLIDATED FINANCIAL STATEMENTS", "PART IV > Financial Statements")] // NDAQ
    [TestCase("SIGNATURES", "PART IV > Signatures")]
    [TestCase("EXHIBIT INDEX", "PART IV > Exhibit Index")] // NFLX
    [TestCase("INDEX OF EXHIBITS", "PART IV > Exhibit Index")] // ORCL
    public void Split_BackMatterTitleAfterLastItem_StartsItsOwnHeading(string title, string expectedHeading)
    {
        string markdown = $"PART IV\nItem 16. Form 10-K Summary\nNone.\n{title}\nBack matter text.\n";

        List<DocumentSection> sections = SectionSplitter.Split(markdown);

        Assert.That(sections.Select(s => s.Heading), Is.EqualTo(new[] { "PART IV > Item 16. Form 10-K Summary", expectedHeading }));
        Assert.That(sections[0].Body, Is.EqualTo("None."));
        Assert.That(sections[1].Body, Is.EqualTo("Back matter text."));
    }

    // Only Part IV holds back matter: an index to the financial statements inside Item 8 is already
    // headed correctly by Item 8.
    [Test]
    public void Split_BackMatterTitleBeforePartIV_StaysUnderItsItem()
    {
        string markdown = "PART II\nItem 8. Financial Statements\nINDEX TO FINANCIAL STATEMENTS\nReport of Independent Registered Public Accounting Firm\n";

        List<DocumentSection> sections = SectionSplitter.Split(markdown);

        Assert.That(sections, Has.Count.EqualTo(1));
        Assert.That(sections[0].Heading, Is.EqualTo("PART II > Item 8. Financial Statements"));
        Assert.That(sections[0].Body, Does.StartWith("INDEX TO FINANCIAL STATEMENTS"));
    }

    [Test]
    public void Split_SinglePartAndItem_ProducesOneSectionWithCombinedHeading()
    {
        string markdown = "PART I\nItem 1. Business\nWe make software.\n";

        List<DocumentSection> sections = SectionSplitter.Split(markdown);

        Assert.That(sections, Has.Count.EqualTo(1));
        Assert.That(sections[0].Heading, Is.EqualTo("PART I > Item 1. Business"));
        Assert.That(sections[0].Body, Is.EqualTo("We make software."));
    }

    // Regression coverage for a real bug found onboarding NFLX-10K-2025.html: 21 of its 22 Item
    // headings have no space between the period and the title ("Item 1.Business"), unlike every other
    // filer seen so far. A stricter \.\s+ requirement silently lost every one of these as a section
    // boundary, collapsing the whole filing into 4 giant PART-only sections with no Item-level heading.
    [Test]
    public void Split_ItemHeadingWithNoSpaceAfterPeriod_IsStillTreatedAsABoundary()
    {
        string markdown = "PART I\nItem 1.Business\nWe stream video.\n";

        List<DocumentSection> sections = SectionSplitter.Split(markdown);

        Assert.That(sections, Has.Count.EqualTo(1));
        Assert.That(sections[0].Heading, Is.EqualTo("PART I > Item 1. Business"));
        Assert.That(sections[0].Body, Is.EqualTo("We stream video."));
    }

    [Test]
    public void Split_RepeatedPartHeader_OnlyFirstOccurrenceStartsNewSection()
    {
        // Simulates MSFT repeating "PART I" as a running per-page header - the second occurrence
        // must not be treated as a new section boundary, or every page would fragment the outline.
        string markdown = "PART I\nItem 1. Business\nFirst page text.\nPART I\nSecond page text.\n";

        List<DocumentSection> sections = SectionSplitter.Split(markdown);

        Assert.That(sections, Has.Count.EqualTo(1));
        Assert.That(sections[0].Heading, Is.EqualTo("PART I > Item 1. Business"));
        Assert.That(sections[0].Body, Does.Contain("First page text."));
        Assert.That(sections[0].Body, Does.Contain("Second page text."));
        Assert.That(sections[0].Body, Does.Not.Contain("PART I"));
    }

    [Test]
    public void Split_BareItemNoise_IsDroppedAndNotTreatedAsBoundary()
    {
        // A bare "Item 1" (no period, no title) is running page-header noise, not a real boundary.
        string markdown = "PART I\nItem 1. Business\nSome text.\nItem 1\nMore text.\n";

        List<DocumentSection> sections = SectionSplitter.Split(markdown);

        Assert.That(sections, Has.Count.EqualTo(1));
        Assert.That(sections[0].Body, Does.Not.Contain("Item 1\n"));
        Assert.That(sections[0].Body, Does.Contain("Some text."));
        Assert.That(sections[0].Body, Does.Contain("More text."));
    }

    [TestCase("\\_\\_\\_\\_\\_\\_\\_\\_")] // escaped decorative underscore rule
    [TestCase("___________")] // unescaped decorative underscore rule
    [TestCase("42")] // lone page number
    [TestCase("---")] // thematic break
    public void Split_NoiseLine_IsDroppedFromBody(string noiseLine)
    {
        string markdown = $"PART I\nItem 1. Business\nBefore.\n{noiseLine}\nAfter.\n";

        List<DocumentSection> sections = SectionSplitter.Split(markdown);

        Assert.That(sections, Has.Count.EqualTo(1));
        Assert.That(sections[0].Body, Does.Not.Contain(noiseLine));
        Assert.That(sections[0].Body, Does.Contain("Before."));
        Assert.That(sections[0].Body, Does.Contain("After."));
    }

    [Test]
    public void Split_BodyBeforeFirstHeading_UsesNoHeadingPlaceholder()
    {
        string markdown = "Some preamble text before any Item or Part heading.\n";

        List<DocumentSection> sections = SectionSplitter.Split(markdown);

        Assert.That(sections, Has.Count.EqualTo(1));
        Assert.That(sections[0].Heading, Is.EqualTo("(no heading)"));
    }

    [Test]
    public void Split_TwoHeadingsWithNoBodyBetween_EmitsNoSectionForTheEmptyOne()
    {
        string markdown = "PART I\nItem 1. Business\nItem 2. Properties\nReal content under Item 2.\n";

        List<DocumentSection> sections = SectionSplitter.Split(markdown);

        Assert.That(sections, Has.Count.EqualTo(1));
        Assert.That(sections[0].Heading, Is.EqualTo("PART I > Item 2. Properties"));
    }

    // Regression coverage for the mid-chunk-title tagging problem: a statement title must start a new
    // section (same heading), so the chunk it opens gets the right StatementType from its first line and
    // the previous page's footer stays with the statement it belongs to.
    [Test]
    public void Split_StatementTitle_StartsNewSectionUnderSameHeading()
    {
        string markdown = "PART II\nItem 8. Financial Statements\nSee accompanying notes.\nCONSOLIDATED BALANCE SHEETS\n| Total assets | 100 |\n";

        List<DocumentSection> sections = SectionSplitter.Split(markdown);

        Assert.That(sections, Has.Count.EqualTo(2));
        Assert.That(sections.Select(s => s.Heading), Is.All.EqualTo("PART II > Item 8. Financial Statements"));
        Assert.That(sections[0].Body, Is.EqualTo("See accompanying notes."));
        Assert.That(sections[1].Body, Does.StartWith("CONSOLIDATED BALANCE SHEETS"));
    }

    [Test]
    public void Split_NotesBoundary_AlsoStartsNewSection()
    {
        string markdown = "PART II\nItem 8. Financial Statements\n| Total equity | 5 |\nNOTES TO FINANCIAL STATEMENTS\nNote 1 text.\n";

        List<DocumentSection> sections = SectionSplitter.Split(markdown);

        Assert.That(sections, Has.Count.EqualTo(2));
        Assert.That(sections[1].Body, Does.StartWith("NOTES TO FINANCIAL STATEMENTS"));
    }

    [TestCase("F-3")]
    [TestCase("F-12")]
    public void Split_FinancialStatementPageMarker_IsDropped(string marker)
    {
        List<DocumentSection> sections = SectionSplitter.Split($"PART I\nItem 1. Business\nText.\n{marker}\nMore text.\n");

        Assert.That(sections[0].Body, Does.Not.Contain(marker));
    }

    [Test]
    public void Split_LineMerelyStartingWithPageMarkerPattern_IsKept()
    {
        List<DocumentSection> sections = SectionSplitter.Split("PART I\nItem 1. Business\nForm F-3 registration statement.\n");

        Assert.That(sections[0].Body, Does.Contain("Form F-3 registration statement."));
    }
}
