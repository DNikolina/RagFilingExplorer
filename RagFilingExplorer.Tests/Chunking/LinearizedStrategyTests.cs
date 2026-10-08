using Microsoft.ML.Tokenizers;
using RagFilingExplorer.Local.Chunking;

namespace RagFilingExplorer.Tests.Chunking;

/// <summary>
/// The pieces that carry a linearized table from the HTML to a chunk: RowBlock's format, the HTML
/// rewrite, SectionSplitter skipping fenced rows, and TokenChunker splitting a row block.
/// </summary>
[TestFixture]
public class LinearizedStrategyTests
{
    private Tokenizer _tokenizer = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");

    private static string Fenced(RowBlock block) => $"{RowBlock.Fence}\n{block.Format()}\n{RowBlock.Fence}";

    [Test]
    public void RowBlock_FormatThenParse_RoundTrips()
    {
        RowBlock original = new("(In millions) Year Ended June 30,", ["Net income — 2026: $133,749", "Other — 2025: 5"]);

        RowBlock? parsed = RowBlock.TryParse(Fenced(original));

        Assert.That(parsed, Is.Not.Null);
        Assert.That(parsed!.Context, Is.EqualTo(original.Context));
        Assert.That(parsed.Rows, Is.EqualTo(original.Rows));
    }

    [Test]
    public void RowBlock_TryParse_IgnoresAnyOtherBlock()
    {
        Assert.That(RowBlock.TryParse("| a | b |\n| --- | --- |"), Is.Null);
        Assert.That(RowBlock.TryParse("```\nsome code\n```"), Is.Null);
    }

    [Test]
    public void LinearizeTables_ReplacesLinearizableTables_AndLeavesFallbacksAsHtml()
    {
        string html = "<html><body>"
            + "<table><tr><td></td><td>2026</td></tr><tr><td>Revenue</td><td>100</td></tr></table>"
            + "<table><tr><td></td><td></td><td>2026</td><td></td><td></td></tr><tr><td>Revenue</td><td></td><td></td><td>100</td><td>200</td></tr></table>"
            + "</body></html>";

        string rewritten = LinearizedChunkingStrategy.LinearizeTables(html);

        Assert.That(rewritten, Does.Contain("<pre>#rows\nRevenue — 2026: 100</pre>"));
        Assert.That(rewritten.Split("<table>").Length - 1, Is.EqualTo(1), "the ambiguous table stays a table");
    }

    // A linearized table of contents reads "PART II" / "Item 7. — Page: ..." - as headings, the first
    // "PART II" would have been taken as the real one and the actual Part II boundary ignored as a repeat.
    [Test]
    public void SectionSplitter_FencedRows_AreNeverHeadings()
    {
        string markdown = "PART I\n\nItem 1. Business\n\nText.\n\n```\n#rows\nPART II\nItem 7. — Page: MD&A / 30\n```\n\nPART II\n\nItem 7. MD&A\n\nMore text.\n";

        List<DocumentSection> sections = SectionSplitter.Split(markdown);

        Assert.That(sections.Select(s => s.Heading), Is.EqualTo(new[] { "PART I > Item 1. Business", "PART II > Item 7. MD&A" }));
        Assert.That(sections[0].Body, Does.Contain("Item 7. — Page: MD&A / 30"));
    }

    // An oversized row block splits between rows only, and every piece repeats the lead-in (statement
    // title, units) and the context line - the context MSFT's comprehensive income total lacked on its
    // second Markdown piece.
    [Test]
    public void TokenChunker_OversizedRowBlock_EveryPieceRepeatsTitleAndContext_AndNoRowIsSplit()
    {
        List<string> rows = Enumerable.Range(1, 40).Select(i => $"Line item number {i} — 2026: ${i},000 | 2025: ${i},100").ToList();
        string body = "COMPREHENSIVE INCOME STATEMENTS\n\n(In millions)\n\n" + Fenced(new RowBlock("Year Ended June 30,", rows));

        List<(string Content, int Tokens)> chunks = TokenChunker.Chunk(body, _tokenizer, maxTokens: 150, overlapTokens: 0);

        Assert.That(chunks, Has.Count.GreaterThan(2));
        foreach ((string content, _) in chunks)
        {
            Assert.That(content, Does.StartWith("COMPREHENSIVE INCOME STATEMENTS\n\n(In millions)\n\nYear Ended June 30,\n"));
        }

        List<string> emitted = chunks.SelectMany(c => c.Content.Split('\n')).Where(l => l.StartsWith("Line item")).ToList();
        Assert.That(emitted, Is.EqualTo(rows), "every row exactly once, whole, in order");
    }

    // A row block that fits on its own but not after its short lead-in takes the lead-in along, rather than
    // leaving the statement title behind as a title-only chunk.
    [Test]
    public void TokenChunker_RowBlockNotFittingAfterItsTitle_TakesTheTitleAlong()
    {
        List<string> rows = Enumerable.Range(1, 8).Select(i => $"Line item number {i} — 2026: ${i},000").ToList();
        string block = Fenced(new RowBlock(null, rows));
        int blockTokens = _tokenizer.CountTokens(string.Join('\n', rows));
        string body = "BALANCE SHEETS\n\n" + block;

        List<(string Content, int Tokens)> chunks = TokenChunker.Chunk(body, _tokenizer, maxTokens: blockTokens + 2, overlapTokens: 0);

        Assert.That(chunks.Any(c => c.Content.Trim() == "BALANCE SHEETS"), Is.False, "no title-only chunk");
        Assert.That(chunks.All(c => c.Content.StartsWith("BALANCE SHEETS")), Is.True);
    }

    [Test]
    public void ChunkingStrategies_CreatesTheConfiguredStrategy()
    {
        ChunkingSettings settings = new() { Strategy = ChunkingStrategyKind.Linearized, TokenizerModel = "gpt-4", MaxTokensPerChunk = 500, OverlapTokens = 50 };

        Assert.That(ChunkingStrategies.Create(settings), Is.InstanceOf<LinearizedChunkingStrategy>());
        Assert.That(ChunkingStrategies.FileName(ChunkingStrategyKind.Linearized), Is.EqualTo("linearized"));
    }
}
