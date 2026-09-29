using AngleSharp.Html.Parser;
using RagFilingExplorer.Local.Structured;

namespace RagFilingExplorer.Local.Tests.Structured;

/// <summary>
/// Sections from blocks, by v1's heading rules (SectionSplitter.HeadingTracker - its rules are tested in
/// SectionSplitterTests): these cover what's specific to blocks - which lines a paragraph keeps, where a
/// paragraph splits between sections, and that tables are never read for headings.
/// </summary>
[TestFixture]
public class StructuredSectionsTests
{
    private static List<StructuredSection> Split(string body) =>
        StructuredSections.Split(FilingBlockReader.Read(new HtmlParser().ParseDocument($"<html><body>{body}</body></html>")));

    [Test]
    public void Split_HeadingAndPageNumberLines_AreDroppedAndHeadTheirSection()
    {
        List<StructuredSection> sections = Split("<p>Cover</p><p>PART I</p><p>Item 1. Business</p><p>We make software.</p><p>12</p><p>More.</p>");

        Assert.That(sections.Select(s => s.Heading), Is.EqualTo(new[] { "(no heading)", "PART I > Item 1. Business" }));
        Assert.That(sections[1].Blocks.Select(b => b.Text), Is.EqualTo(new[] { "We make software.", "More." }));
    }

    [Test]
    public void Split_StatementTitle_StartsASectionAndStaysInIt()
    {
        List<StructuredSection> sections = Split("<p>Item 8. Financial Statements</p><p>Report of auditors.</p><p>BALANCE SHEETS</p><p>(In millions)</p>");

        Assert.That(sections, Has.Count.EqualTo(2));
        Assert.That(sections.Select(s => s.Heading).Distinct(), Is.EqualTo(new[] { "Item 8. Financial Statements" }));
        Assert.That(sections[1].Blocks.Select(b => b.Text), Is.EqualTo(new[] { "BALANCE SHEETS", "(In millions)" }));
    }

    // A heading inside a paragraph (lines joined by <br>) splits it: the text before belongs to the section that
    // ends, as it did when the page was one text.
    [Test]
    public void Split_HeadingMidParagraph_SplitsTheParagraphBetweenSections()
    {
        List<StructuredSection> sections = Split("<p>Item 1. Business</p><p>Last line of Item 1.<br/>Item 2. Properties<br/>First line of Item 2.</p>");

        Assert.That(sections.Select(s => (s.Heading, string.Join("|", s.Blocks.Select(b => b.Text)))), Is.EqualTo(new[]
        {
            ("Item 1. Business", "Last line of Item 1."),
            ("Item 2. Properties", "First line of Item 2."),
        }));
    }

    // A table of contents linearizes to "PART I" / "Item 1. Business — Page: 3" rows; read as headings, the TOC
    // would take "PART I" (a Part counts only the first time) and every later Part heading would be lost.
    [Test]
    public void Split_TableOfContentsRows_AreNotHeadings()
    {
        List<StructuredSection> sections = Split("<table><tr><td>PART I</td></tr><tr><td>Item 1. Business</td><td>3</td></tr></table>"
            + "<p>PART I</p><p>Item 1. Business</p><p>Body.</p>");

        Assert.That(sections.Select(s => s.Heading), Is.EqualTo(new[] { "(no heading)", "PART I > Item 1. Business" }));
        Assert.That(sections[0].Blocks.Single(), Is.InstanceOf<TableBlock>());
    }
}
