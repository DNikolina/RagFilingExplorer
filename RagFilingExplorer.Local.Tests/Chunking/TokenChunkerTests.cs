using Microsoft.ML.Tokenizers;
using RagFilingExplorer.Local.Chunking;

namespace RagFilingExplorer.Local.Tests.Chunking;

/// <summary>
/// Uses the real GPT-4 tokenizer (offline via Microsoft.ML.Tokenizers.Data.Cl100kBase, same as
/// Program.cs) rather than a fake token counter - it's fast and deterministic, so there's nothing to
/// mock. Split-boundary tests compute their own token thresholds from the same tokenizer instance
/// instead of hand-guessing counts, so they stay correct regardless of exact tokenization details.
/// </summary>
[TestFixture]
public class TokenChunkerTests
{
    private Tokenizer _tokenizer = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");

    [Test]
    public void OversizedTable_CarriesRowGroupLabelForwardAcrossSplits()
    {
        // Reproduces the exact bug found and fixed this session: an earlier version repeated only the
        // syntactic 2-line header on each split piece, losing labels like "Cost of revenue:" for pieces
        // that don't happen to start on the label row itself.
        const string header1 = "| Item | 2026 | 2025 | 2024 |";
        const string header2 = "| --- | --- | --- | --- |";
        const string label = "| Cost of revenue: |  |  |  |";
        const string dataRow1 = "| Product cost | $500 | $450 | $400 |";
        const string dataRow2 = "| Service cost | $600 | $550 | $500 |";
        const string dataRow3 = "| License cost | $700 | $650 | $600 |";

        string table = string.Join('\n', header1, header2, label, dataRow1, dataRow2, dataRow3);

        int headerTokens = _tokenizer.CountTokens(header1 + "\n" + header2);
        int labelTokens = _tokenizer.CountTokens(label);
        int dataRow1Tokens = _tokenizer.CountTokens(dataRow1);

        // Set the budget so exactly [header, label, dataRow1] fits, forcing dataRow2 and dataRow3 into
        // later pieces that don't start on the label row.
        int maxTokens = headerTokens + labelTokens + dataRow1Tokens;

        List<(string Content, int Tokens)> chunks = TokenChunker.Chunk(table, _tokenizer, maxTokens, overlapTokens: 0);

        Assert.That(chunks, Has.Count.GreaterThanOrEqualTo(3));
        foreach ((string content, _) in chunks)
        {
            Assert.That(content, Does.Contain(header1));
            Assert.That(content, Does.Contain(label), "every split piece must carry the nearest row-group label forward");
        }

        Assert.That(chunks[1].Content, Does.Contain(dataRow2));
        Assert.That(chunks[1].Content, Does.Not.Contain(dataRow1), "dataRow1 belongs to the first piece only");
    }

    [Test]
    public void OversizedTable_DoesNotFoldRealDataRowIntoRepeatedHeader()
    {
        // Reproduces the exhibit-index bug: a table with no row-group labels, where real content
        // (a $ amount here) starts on the very first row. That row must not be mistaken for part of
        // the repeating header and duplicated onto every split piece.
        const string header1 = "| Description | Value |";
        const string header2 = "| --- | --- |";
        const string dataRow0 = "| First line item | $100 |";
        List<string> plainRows = Enumerable.Range(1, 8)
            .Select(i => $"| Line item {i} | plain description text number {i} |")
            .ToList();

        string table = string.Join('\n', new[] { header1, header2, dataRow0 }.Concat(plainRows));

        int headerTokens = _tokenizer.CountTokens(header1 + "\n" + header2);
        int dataRow0Tokens = _tokenizer.CountTokens(dataRow0);
        int firstPlainRowTokens = _tokenizer.CountTokens(plainRows[0]);

        // Force a split right after dataRow0 - just enough budget for header + dataRow0, not the next row.
        int maxTokens = headerTokens + dataRow0Tokens + (firstPlainRowTokens / 2);

        List<(string Content, int Tokens)> chunks = TokenChunker.Chunk(table, _tokenizer, maxTokens, overlapTokens: 0);

        Assert.That(chunks, Has.Count.GreaterThan(1));
        int piecesContainingDataRow0 = chunks.Count(c => c.Content.Contains("$100"));
        Assert.That(piecesContainingDataRow0, Is.EqualTo(1), "a real data row with no row-group label must not repeat as a fake header");
    }

    [Test]
    public void ContentFreeTable_ProducesNoChunks()
    {
        // A purely decorative table (e.g. a bordered blank-cell divider) - every row has nothing left
        // once "|", "-", and whitespace are stripped.
        string table = string.Join('\n', "| | |", "| --- | --- |", "| | |");

        List<(string Content, int Tokens)> chunks = TokenChunker.Chunk(table, _tokenizer, maxTokens: 500, overlapTokens: 50);

        Assert.That(chunks, Is.Empty);
    }

    [Test]
    public void TableWithBlankSpacerRowsAndRealText_IsNotDropped()
    {
        // Mirrors MSFT's signature-block tables: some blank spacer rows mixed with real names/titles.
        // The content-free filter must only trigger when EVERY row is blank, not just some.
        string table = string.Join('\n',
            "| | |",
            "| --- | --- |",
            "| | |",
            "| /s/ Jane Doe | Chief Financial Officer |",
            "| | |");

        List<(string Content, int Tokens)> chunks = TokenChunker.Chunk(table, _tokenizer, maxTokens: 500, overlapTokens: 50);

        Assert.That(chunks, Has.Count.EqualTo(1));
        Assert.That(chunks[0].Content, Does.Contain("Jane Doe"));
    }

    [Test]
    public void OversizedPlainText_SplitsAtWordBoundaries_NeverMidWord()
    {
        string[] words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima"];
        string text = string.Join(' ', words);

        List<(string Content, int Tokens)> chunks = TokenChunker.Chunk(text, _tokenizer, maxTokens: 3, overlapTokens: 0);

        Assert.That(chunks, Has.Count.GreaterThan(1));
        foreach ((string content, _) in chunks)
        {
            foreach (string token in content.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.That(words, Does.Contain(token), $"'{token}' is not a whole word from the source text - a word was split mid-token");
            }
        }
    }

    [Test]
    public void Chunk_CarriesLastBlockAsOverlap_WhenItFitsWithinOverlapBudget()
    {
        const string paragraphA = "Alpha bravo charlie.";
        const string paragraphB = "Delta echo foxtrot golf.";
        const string paragraphC = "Hotel india juliet kilo lima.";

        string body = string.Join("\n\n", paragraphA, paragraphB, paragraphC);

        int abTokens = _tokenizer.CountTokens(paragraphA) + _tokenizer.CountTokens(paragraphB);
        int bTokens = _tokenizer.CountTokens(paragraphB);
        int cTokens = _tokenizer.CountTokens(paragraphC);

        // Budget fits A+B (and fits C alone) but not A+B+C, forcing a flush before C without pushing C
        // itself into the separate "oversized single block" path. overlapTokens is generous enough that
        // paragraphB (the last block before the flush) qualifies to carry forward into the next chunk.
        int maxTokens = Math.Max(abTokens, cTokens) + 1;
        int overlapTokens = bTokens + 5;

        List<(string Content, int Tokens)> chunks = TokenChunker.Chunk(body, _tokenizer, maxTokens, overlapTokens);

        Assert.That(chunks, Has.Count.EqualTo(2));
        Assert.That(chunks[0].Content, Does.Contain(paragraphA));
        Assert.That(chunks[0].Content, Does.Contain(paragraphB));
        Assert.That(chunks[1].Content, Does.StartWith(paragraphB), "the last block before the flush should carry forward as overlap");
        Assert.That(chunks[1].Content, Does.Contain(paragraphC));
    }
}
