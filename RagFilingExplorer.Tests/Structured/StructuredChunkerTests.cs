using AngleSharp.Html.Parser;
using Microsoft.ML.Tokenizers;
using RagFilingExplorer.Local.Structured;

namespace RagFilingExplorer.Tests.Structured;

/// <summary>
/// Chunks from a section's blocks. The packing rules are TokenChunker's (TokenChunkerTests); these check what the
/// block model adds - every chunk knows the blocks it was built from - and a rule that depends on the table block.
/// </summary>
[TestFixture]
public class StructuredChunkerTests
{
    private Tokenizer _tokenizer = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");

    private static StructuredSection Section(string body) =>
        new("Item 8", FilingBlockReader.Read(new HtmlParser().ParseDocument($"<html><body>{body}</body></html>")));

    [Test]
    public void Chunk_StatementTitleAndItsSplitTable_EveryPieceHoldsTheTitleAndTheTable()
    {
        string rows = string.Concat(Enumerable.Range(1, 40).Select(i => $"<tr><td>Line item {i}</td><td>{i},000</td></tr>"));
        StructuredSection section = Section($"<p>BALANCE SHEETS</p><table><tr><td></td><td>2026</td></tr>{rows}</table>");

        List<StructuredChunk> chunks = StructuredChunker.Chunk(section, _tokenizer, maxTokens: 150, overlapTokens: 0);

        Assert.That(chunks, Has.Count.GreaterThan(1));
        Assert.That(chunks.All(c => c.Content.StartsWith("BALANCE SHEETS\n\n")), Is.True, "the title rides on every row-block piece");
        Assert.That(chunks.All(c => c.Blocks.SequenceEqual(section.Blocks)), Is.True);
    }

    [Test]
    public void Chunk_ParagraphCarriedAsOverlap_IsInBothChunks()
    {
        string longText = string.Join(" ", Enumerable.Repeat("The company sells software and cloud services worldwide.", 12));
        StructuredSection section = Section($"<p>{longText}</p><p>Short bridge.</p><p>{longText}</p>");

        List<StructuredChunk> chunks = StructuredChunker.Chunk(section, _tokenizer, maxTokens: 150, overlapTokens: 20);

        Assert.That(chunks, Has.Count.EqualTo(2));
        Assert.That(chunks[0].Blocks, Is.EqualTo(section.Blocks.Take(2)));
        Assert.That(chunks[1].Blocks, Is.EqualTo(section.Blocks.Skip(1)));
    }

    // MSFT chunk 198: an exhibit-index continuation piece with no column names above it (FilingBlockReaderTests).
    [Test]
    public void Chunk_TextTableWithBoldHeaderRows_RepeatsThemOnEveryPiece()
    {
        string bold = "style=\"font-weight:bold\"";
        string rows = string.Concat(Enumerable.Range(1, 30).Select(i => $"<tr><td>4.{i}</td><td>Supplemental Indenture number {i} between the Company and the Trustee</td><td>8-K</td></tr>"));
        StructuredSection section = Section($"<table><tr><td><span {bold}>Exhibit Number</span></td><td><span {bold}>Exhibit Description</span></td><td><span {bold}>Form</span></td></tr>{rows}</table>");

        List<StructuredChunk> chunks = StructuredChunker.Chunk(section, _tokenizer, maxTokens: 150, overlapTokens: 0);

        Assert.That(chunks, Has.Count.GreaterThan(2));
        Assert.That(chunks.All(c => c.Content.StartsWith("Exhibit Number | Exhibit Description | Form\n")), Is.True);
    }
}
