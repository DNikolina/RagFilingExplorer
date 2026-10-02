using Microsoft.ML.Tokenizers;
using RagFilingExplorer.Local.Chunking;

namespace RagFilingExplorer.Local.Tests.Chunking;

/// <summary>
/// Uses the real GPT-4 tokenizer (offline via Microsoft.ML.Tokenizers.Data.Cl100kBase, same as
/// ChunkingStrategies.Create) rather than a fake token counter - it's fast and deterministic, so there's nothing to
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
        // Reproduces a real bug, since fixed: an earlier version repeated only the
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
    public void OversizedTable_RepeatsPeriodRow_EvenBelowALabelOnlyUnitsRow()
    {
        // MSFT's layout: "(In millions)" has text in the first cell only, so it reads as a row-group
        // label and used to end the header scan - dropping "Year Ended June 30, ... 2026 ... 2025" from
        // every piece after the first, so the comprehensive income total reached the model without years.
        const string header1 = "|  |  |  |";
        const string header2 = "| --- | --- | --- |";
        const string units = "| (In millions) |  |  |";
        const string period = "| Year Ended June 30, | 2026 | 2025 |";
        List<string> dataRows = Enumerable.Range(1, 6).Select(i => $"| Line item {i} | $ 1,00{i} | $ 90{i} |").ToList();

        string table = string.Join('\n', new[] { header1, header2, units, period }.Concat(dataRows));
        int headerTokens = _tokenizer.CountTokens(string.Join('\n', header1, header2, units, period));
        int maxTokens = headerTokens + 2 * dataRows.Max(r => _tokenizer.CountTokens(r));

        List<(string Content, int Tokens)> chunks = TokenChunker.Chunk(table, _tokenizer, maxTokens, overlapTokens: 0);

        Assert.That(chunks, Has.Count.GreaterThan(1));
        foreach ((string content, _) in chunks)
        {
            Assert.That(content, Does.Contain(period), "every split piece must keep the fiscal-period row");
        }
    }

    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public void OversizedTable_EveryRowSitsUnderItsOwnRowGroupLabel(int rowsPerPiece)
    {
        // NFLX's comprehensive income statement: the label repeated at a piece's top used to be read when
        // the piece was emitted - i.e. the piece's own *last* label ("Fair value hedges:" heading the cash
        // flow hedge rows, "Cash flow hedges:" above Net income) - and was never closed by a total, so
        // "Comprehensive income" came out under "Fair value hedges:". Checked as an invariant at several
        // budgets rather than against one exact set of split points.
        const string header1 = "| Item | 2025 | 2024 |";
        const string header2 = "| --- | --- | --- |";
        const string cashFlowLabel = "| Cash flow hedges: |  |  |";
        const string fairValueLabel = "| Fair value hedges: |  |  |";
        (string Row, string? Label)[] body =
        {
            ("| Net income | $ 10,981 | $ 8,711 |", null),
            (cashFlowLabel, null),
            ("| Net unrealized gains | $ 1,071 | $ 921 |", cashFlowLabel),
            ("| Reclassification of net gains | $ 6,896 | $ 9,679 |", cashFlowLabel),
            ("| Net change | $ 1,002 | $ 8,244 |", cashFlowLabel),
            (fairValueLabel, null),
            ("| Net change excluded | $ 9,838 | $ 7,113 |", fairValueLabel),
            ("| Total other comprehensive loss | $ 9,425 | $ 5,861 |", fairValueLabel),
            ("| Comprehensive income | $ 10,038 | $ 9,297 |", null),
        };

        string table = string.Join('\n', new[] { header1, header2 }.Concat(body.Select(b => b.Row)));
        int headerTokens = _tokenizer.CountTokens(header1 + "\n" + header2);
        int maxRowTokens = body.Max(b => _tokenizer.CountTokens(b.Row));
        int maxTokens = headerTokens + rowsPerPiece * maxRowTokens;

        List<(string Content, int Tokens)> chunks = TokenChunker.Chunk(table, _tokenizer, maxTokens, overlapTokens: 0);

        Assert.That(chunks, Has.Count.GreaterThan(1));
        foreach ((string content, _) in chunks)
        {
            string? labelAbove = null;
            foreach (string line in content.Split('\n'))
            {
                if (line == cashFlowLabel || line == fairValueLabel)
                {
                    labelAbove = line;
                    continue;
                }

                int index = Array.FindIndex(body, b => b.Row == line);
                if (index >= 0)
                {
                    Assert.That(labelAbove, Is.EqualTo(body[index].Label), $"wrong label above '{line}' in piece:\n{content}");
                }

                // Read the piece the way the chunker groups rows: a total closes the group above it.
                if (line.StartsWith("| Total", StringComparison.Ordinal))
                {
                    labelAbove = null;
                }
            }
        }
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

    private static string BigTable(int dataRows) => string.Join('\n',
        new[] { "| Item | 2026 | 2025 |", "| --- | --- | --- |" }
            .Concat(Enumerable.Range(1, dataRows).Select(i => $"| Line item number {i} | $1,{i:000} | $2,{i:000} |")));

    // Regression coverage for the near-empty title chunks: once statement titles started their own
    // sections, "TITLE" + "For the Years Ended ..." before an oversized table became a chunk of its own
    // and took a top-5 slot for statement questions. It must ride on the first table piece instead.
    [Test]
    public void ShortLeadInBeforeOversizedTable_BecomesFirstPiecesCaption_NotItsOwnChunk()
    {
        const string title = "CONSOLIDATED STATEMENTS OF STOCKHOLDERS' EQUITY";
        const string subtitle = "For the Years Ended May 31, 2026, 2025 and 2024";
        string body = string.Join("\n\n", title, subtitle, BigTable(60));
        const int maxTokens = 200;

        List<(string Content, int Tokens)> chunks = TokenChunker.Chunk(body, _tokenizer, maxTokens, overlapTokens: 50);

        Assert.That(chunks, Has.Count.GreaterThan(1), "the table must still be split");
        Assert.That(chunks[0].Content, Does.StartWith(title));
        Assert.That(chunks[0].Content, Does.Contain(subtitle).And.Contain("| Line item number 1 |"), "caption and first rows share a chunk");
        Assert.That(chunks.Skip(1).Select(c => c.Content), Has.None.Contain(title), "caption rides on the first piece only");
        Assert.That(chunks.Select(c => c.Tokens), Has.All.LessThanOrEqualTo(maxTokens), "the caption comes out of the first piece's row budget");
    }

    // Regression coverage for the tiny footer chunks: a statement's "See accompanying notes..." footer
    // after its oversized table's last piece used to become a chunk of its own and took top-5 slots.
    [Test]
    public void ShortFooterAfterOversizedTable_IsAppendedToLastTablePiece()
    {
        const string footer = "See accompanying notes to consolidated financial statements.";
        string body = string.Join("\n\n", BigTable(60), footer);

        List<(string Content, int Tokens)> chunks = TokenChunker.Chunk(body, _tokenizer, maxTokens: 200, overlapTokens: 50);

        Assert.That(chunks, Has.Count.GreaterThan(1));
        Assert.That(chunks[^1].Content, Does.StartWith("| Item |").And.EndWith(footer), "footer rides on the last table piece");
        Assert.That(chunks.Select(c => c.Content), Has.None.EqualTo(footer));
        Assert.That(chunks[^1].Tokens, Is.EqualTo(_tokenizer.CountTokens(chunks[^1].Content)), "token count reflects the merged content");
    }

    // Deliberately narrow: an ordinary narrative section's short final paragraph is left as its own
    // chunk - only a remainder right after an oversized table is merged back.
    [Test]
    public void ShortFinalParagraphAfterOrdinaryText_IsNotMerged()
    {
        string longParagraph = string.Join(' ', Enumerable.Repeat("This narrative sentence discusses results at length.", 12));
        const string shortTail = "That concludes the discussion.";
        string body = string.Join("\n\n", longParagraph, longParagraph, shortTail);
        // Fits both long paragraphs but not the tail too, so the tail starts a new chunk.
        int maxTokens = _tokenizer.CountTokens(longParagraph) * 2 + _tokenizer.CountTokens(shortTail) - 1;

        List<(string Content, int Tokens)> chunks = TokenChunker.Chunk(body, _tokenizer, maxTokens, overlapTokens: 0);

        Assert.That(chunks, Has.Count.EqualTo(2));
        Assert.That(chunks[1].Content, Is.EqualTo(shortTail));
    }

    [Test]
    public void LongParagraphAfterOversizedTable_StillGetsItsOwnChunk()
    {
        string paragraph = string.Join(' ', Enumerable.Repeat("This narrative sentence discusses results at length.", 20));
        string body = string.Join("\n\n", BigTable(60), paragraph);

        List<(string Content, int Tokens)> chunks = TokenChunker.Chunk(body, _tokenizer, maxTokens: 400, overlapTokens: 50);

        Assert.That(chunks[^1].Content, Is.EqualTo(paragraph));
    }

    [Test]
    public void LongParagraphBeforeOversizedTable_StillGetsItsOwnChunk()
    {
        string paragraph = string.Join(' ', Enumerable.Repeat("This narrative sentence discusses results at length.", 20));
        string body = string.Join("\n\n", paragraph, BigTable(60));

        List<(string Content, int Tokens)> chunks = TokenChunker.Chunk(body, _tokenizer, maxTokens: 400, overlapTokens: 50);

        Assert.That(_tokenizer.CountTokens(paragraph), Is.GreaterThan(100), "precondition: longer than the caption cap");
        Assert.That(chunks[0].Content, Is.EqualTo(paragraph));
        Assert.That(chunks[1].Content, Does.StartWith("| Item |"));
    }

    // Pack is what the Structured strategy calls with its own blocks: every chunk says which blocks it holds, so
    // labels read from a table (step 1b-iii) reach every piece of it, caption and footer included.
    [Test]
    public void Pack_SplitRowBlockWithCaptionAndFooter_EveryPieceListsTheBlocksItHolds()
    {
        RowBlock rows = new("(In millions)", Enumerable.Range(1, 40).Select(i => $"Line item {i} — 2026: {i},000").ToList());
        List<ChunkerBlock> blocks = [new("BALANCE SHEETS", null), new(rows.Text, rows), new("See accompanying notes.", null)];

        List<PackedChunk> chunks = TokenChunker.Pack(blocks, _tokenizer, maxTokens: 150, overlapTokens: 0);

        Assert.That(chunks, Has.Count.GreaterThan(2));
        Assert.That(chunks.Take(chunks.Count - 1).Select(c => c.Blocks), Is.All.EqualTo(new[] { 0, 1 }));
        Assert.That(chunks[^1].Blocks, Is.EqualTo(new[] { 0, 1, 2 }), "the footer is merged into the last piece");
    }

    [Test]
    public void Chunk_SameTextAsPack_ForParsedBlocks()
    {
        string body = string.Join("\n\n", "Intro paragraph.", BigTable(60), "Closing paragraph.");

        List<(string Content, int Tokens)> viaText = TokenChunker.Chunk(body, _tokenizer, maxTokens: 400, overlapTokens: 50);
        List<PackedChunk> viaBlocks = TokenChunker.Pack(TokenChunker.SplitText(body).Select(b => new ChunkerBlock(b, null)).ToList(), _tokenizer, 400, 50);

        Assert.That(viaBlocks.Select(c => (c.Content, c.Tokens)), Is.EqualTo(viaText));
    }
}
