using RagFilingExplorer.Local.Chunking;

namespace RagFilingExplorer.Tests.Chunking;

[TestFixture]
public class EmbeddingTextBuilderTests
{
    [Test]
    public void Build_NonTableContent_ReturnsHeadingAndContentUnchanged()
    {
        string result = EmbeddingTextBuilder.Build("PART I > Item 1. Business", "We make software.");

        Assert.That(result, Is.EqualTo("PART I > Item 1. Business\n\nWe make software."));
    }

    [Test]
    public void Build_TableWithRowLabels_IncludesLabelSummary()
    {
        string table = string.Join('\n',
            "| Item | 2026 | 2025 |",
            "| --- | --- | --- |",
            "| Total revenues | $1,234 | $1,111 |",
            "| Gross margin | $500 | $450 |");

        string result = EmbeddingTextBuilder.Build("PART II > Item 8. Financial Statements", table);

        // The label extraction has no special case for the syntactic header row - its first cell
        // ("Item") is informative text too, so it's surfaced alongside the real row-group labels.
        Assert.That(result, Does.Contain("Financial data table with rows: Item, Total revenues, Gross margin."));
        Assert.That(result, Does.Contain(table));
    }

    // Regression coverage: TokenChunker prefixes an oversized table's first piece with its caption (the
    // statement title), and a "starts with |" check skipped exactly those pieces - the ones holding the
    // headline rows. Any chunk containing a table gets the summary, caption or not.
    [TestCase("CONSOLIDATED STATEMENTS OF OPERATIONS\n\nFor the Years Ended May 31, 2026\n\n")]
    [TestCase("The following table shows revenue by segment:\n\n")]
    public void Build_LeadInTextThenTable_StillIncludesLabelSummary(string leadIn)
    {
        string content = leadIn + string.Join('\n',
            "| | 2026 | 2025 |",
            "| --- | --- | --- |",
            "| Total revenues | $67,357 | $57,399 |");

        string result = EmbeddingTextBuilder.Build("PART IV > Item 15", content);

        Assert.That(result, Does.Contain("Financial data table with rows: Total revenues."));
        Assert.That(result, Does.EndWith(content));
    }

    [Test]
    public void Build_TableWithNoExtractableLabels_FallsBackToGenericSummary()
    {
        string table = string.Join('\n',
            "| 2026 | 2025 |",
            "| --- | --- |",
            "| $1,234 | $1,111 |");

        string result = EmbeddingTextBuilder.Build("Heading", table);

        Assert.That(result, Does.Contain("Financial data table."));
        Assert.That(result, Does.Not.Contain("Financial data table with rows:"));
    }

    [Test]
    public void Build_TableWithRepeatedLabel_DeduplicatesLabel()
    {
        string table = string.Join('\n',
            "| Item | 2026 | 2025 |",
            "| --- | --- | --- |",
            "| Total revenues | $1,234 | $1,111 |",
            "| Total revenues | $1,200 | $1,000 |");

        string result = EmbeddingTextBuilder.Build("Heading", table);

        int occurrences = System.Text.RegularExpressions.Regex.Matches(result, "Total revenues").Count;
        // One in the summary line, one in the raw table content that follows - not two in the summary.
        Assert.That(occurrences, Is.EqualTo(3));
    }

    [TestCase("| -- | $100 |")] // separator-only label
    [TestCase("| 42 | $100 |")] // purely numeric label
    public void Build_SkipsNonInformativeLabels(string row)
    {
        // Blank first header cell, matching how the real converter emits financial tables (the real
        // column headers land as a later body row, not the syntactic Markdown header - see
        // TokenChunker's SplitOversizedTable doc comment) - so it doesn't itself become a spurious label.
        string table = string.Join('\n', "| | 2026 |", "| --- | --- |", row);

        string result = EmbeddingTextBuilder.Build("Heading", table);

        Assert.That(result, Does.Contain("Financial data table."));
        Assert.That(result, Does.Not.Contain("Financial data table with rows:"));
    }
}
