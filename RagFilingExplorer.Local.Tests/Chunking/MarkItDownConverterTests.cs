using System.Text;
using RagFilingExplorer.Local.Chunking;

namespace RagFilingExplorer.Local.Tests.Chunking;

/// <summary>
/// The regex-stripping and source-encoding-detection logic is unit tested here - the rest of
/// MarkItDownConverter shells out to the "markitdown" CLI, which is a real integration point (an
/// external Python process), not something meaningfully covered by a mock. StripIxHeader's regex is
/// what caused the real 40,000-character XBRL garbage-blob bug when it was missing; DetectEncoding is
/// what caused a real, silent content-corruption bug when it didn't exist yet - reading a
/// windows-1252-declared filing (a browser-saved copy, unlike the raw-downloaded EDGAR HTML the other
/// filings use) as UTF-8 replaced every non-ASCII character with U+FFFD before markitdown ever saw it.
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

    // Regression coverage for the actual bug: NFLX-10K-2025.html declares (and is genuinely encoded
    // as) windows-1252, unlike the raw EDGAR HTML for the other three filings, which declares no
    // charset at all. Reading it as UTF-8 unconditionally silently replaced every non-ASCII character
    // with U+FFFD - 700+ of them in NFLX's chunks.
    [Test]
    public void DetectEncoding_MetaHttpEquivCharsetDeclaration_ReturnsThatEncoding()
    {
        byte[] windows1252Bytes = Encoding.GetEncoding("windows-1252").GetBytes(
            "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=windows-1252\"></head></html>");

        Encoding result = MarkItDownConverter.DetectEncoding(windows1252Bytes);

        Assert.That(result.WebName, Is.EqualTo("windows-1252"));
    }

    [Test]
    public void DetectEncoding_Html5MetaCharsetDeclaration_ReturnsThatEncoding()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("<html><head><meta charset=\"utf-8\"></head></html>");

        Encoding result = MarkItDownConverter.DetectEncoding(bytes);

        Assert.That(result.WebName, Is.EqualTo("utf-8"));
    }

    [Test]
    public void DetectEncoding_NoCharsetDeclared_DefaultsToUtf8()
    {
        // Matches MSFT/ORCL/NDAQ's raw EDGAR HTML - none of them declare a charset at all.
        byte[] bytes = Encoding.UTF8.GetBytes("<html><head></head><body>Real content.</body></html>");

        Encoding result = MarkItDownConverter.DetectEncoding(bytes);

        Assert.That(result.WebName, Is.EqualTo("utf-8"));
    }

    [Test]
    public void DetectEncoding_Utf8Bom_ReturnsUtf8RegardlessOfAnyMetaTag()
    {
        byte[] bomBytes = [0xEF, 0xBB, 0xBF];
        byte[] contentBytes = Encoding.ASCII.GetBytes(
            "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=windows-1252\"></head></html>");
        byte[] bytes = [.. bomBytes, .. contentBytes];

        Encoding result = MarkItDownConverter.DetectEncoding(bytes);

        Assert.That(result.WebName, Is.EqualTo("utf-8"));
    }

    [Test]
    public void DetectEncoding_UnrecognizedCharsetName_FallsBackToUtf8()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("<html><head><meta charset=\"not-a-real-charset\"></head></html>");

        Encoding result = MarkItDownConverter.DetectEncoding(bytes);

        Assert.That(result.WebName, Is.EqualTo("utf-8"));
    }
}
