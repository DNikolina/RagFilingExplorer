using RagFilingExplorer.Core.Chunking;

namespace RagFilingExplorer.Tests.Chunking;

/// <summary>
/// The regex-stripping logic is unit tested here - the rest of MarkItDownConverter shells out to the "markitdown"
/// CLI, which is a real integration point (an external Python process), not something meaningfully covered by a mock.
/// StripIxHeader's regex is what caused the real 40,000-character XBRL garbage-blob bug when it was missing. Encoding
/// detection: FilingFileTests.
/// </summary>
[TestFixture]
public class MarkItDownConverterTests
{
    [Test]
    public void StripIxHeader_RemovesInlineXbrlHeaderBlock()
    {
        string html = "<html><body><ix:header><div>lots of taxonomy metadata</div></ix:header><p>Real content.</p></body></html>";

        string result = MarkItDownConverter.StripIxHeader(html);

        Assert.That(result, Does.Not.Contain("ix:header"));
        Assert.That(result, Does.Not.Contain("taxonomy metadata"));
        Assert.That(result, Does.Contain("<p>Real content.</p>"));
    }

    [Test]
    public void StripIxHeader_RemovesMultiLineHeaderBlock()
    {
        string html = "<html><body><ix:header>\n<div>line one</div>\n<div>line two</div>\n</ix:header><p>Real content.</p></body></html>";

        string result = MarkItDownConverter.StripIxHeader(html);

        Assert.That(result, Does.Not.Contain("line one"));
        Assert.That(result, Does.Not.Contain("line two"));
        Assert.That(result, Does.Contain("Real content."));
    }

    [Test]
    public void StripIxHeader_NoHeaderPresent_ReturnsHtmlUnchanged()
    {
        const string html = "<html><body><p>Real content.</p></body></html>";

        string result = MarkItDownConverter.StripIxHeader(html);

        Assert.That(result, Is.EqualTo(html));
    }
}
