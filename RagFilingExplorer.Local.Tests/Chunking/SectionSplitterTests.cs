using RagFilingExplorer.Local.Chunking;

namespace RagFilingExplorer.Local.Tests.Chunking;

[TestFixture]
public class SectionSplitterTests
{
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
}
