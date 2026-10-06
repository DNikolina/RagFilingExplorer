using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace RagFilingExplorer.Local.Chunking;

/// <summary>One block for <see cref="TokenChunker.Pack"/>: a paragraph or Markdown table (<see cref="Rows"/> null), or a
/// row block, whose <see cref="Text"/> is <see cref="RowBlock.Text"/>.</summary>
internal sealed record ChunkerBlock(string Text, RowBlock? Rows);

/// <summary>A packed chunk, and the indexes of the <see cref="ChunkerBlock"/>s it holds.</summary>
internal sealed record PackedChunk(string Content, int Tokens, IReadOnlyList<int> Blocks);

/// <summary>
/// Splits a section into token-bounded chunks - from text (<see cref="Chunk"/>, v1's strategies) or from blocks the
/// caller has already typed (<see cref="Pack"/>, the Structured strategy). Paragraphs are packed greedily up to
/// maxTokens; a table (a run of contiguous Markdown table rows, or a row block) is one atomic block, so it isn't
/// sliced mid-row-group. If a single block exceeds maxTokens on its own, it's split further - a Markdown table by
/// repeating its header rows on every piece, a row block between rows with its caption and context line repeated -
/// so a chunk with a number always keeps the column/fiscal-year labels next to it.
/// </summary>
internal static class TokenChunker
{
    // Short text at most this long is attached to a neighbouring chunk instead of becoming a near-empty
    // chunk of its own: a lead-in before an oversized table (a statement title, "(in millions)", a
    // one-line caption) rides forward on the table's first piece, and a trailing remainder right after
    // an oversized table's last piece (a statement footer, a one-line footnote) is appended to it.
    // Either can push that chunk past maxTokens by up to this much - accepted, since the budget is
    // already approximate (separators between rows and blocks aren't counted) and a tiny chunk costs a
    // whole top-5 retrieval slot.
    private const int MaxAttachedTextTokens = 100;

    private static readonly Regex CommaGroupedNumberRegex = new(@"\d{1,3}(,\d{3})+", RegexOptions.Compiled);
    private static readonly Regex PeriodEndedRegex = new(@"\b(years?|months|weeks|quarters?)\s+ended\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BareYearCellRegex = new(@"^(fiscal\s+)?(19|20)\d{2}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static List<(string Content, int Tokens)> Chunk(string body, Tokenizer tokenizer, int maxTokens, int overlapTokens) =>
        Pack(SplitIntoBlocks(body).Select(b => RowBlock.TryParse(b) is { } rows ? new ChunkerBlock(rows.Text, rows) : new ChunkerBlock(b, null)).ToList(),
                tokenizer, maxTokens, overlapTokens)
            .Select(c => (c.Content, c.Tokens))
            .ToList();

    /// <summary>
    /// A section's text split into the blocks <see cref="Pack"/> takes: paragraphs (a run of lines between blank
    /// lines) and Markdown tables; content-free tables dropped. For the Structured strategy's text blocks.
    /// </summary>
    public static List<string> SplitText(string text) => SplitIntoBlocks(text);

    /// <summary>
    /// The packing rules on blocks the caller has already typed - <see cref="Chunk"/> parses them out of text;
    /// the Structured strategy builds them from the DOM. Each chunk lists the indexes of the blocks it holds
    /// (a block carried as overlap is in two chunks; a split block in each of its pieces).
    /// </summary>
    public static List<PackedChunk> Pack(IReadOnlyList<ChunkerBlock> blocks, Tokenizer tokenizer, int maxTokens, int overlapTokens)
    {
        List<PackedChunk> chunks = new();
        List<(string Text, bool TableLike, int Index)> current = new();
        int currentTokens = 0;

        // True while the most recent chunk is the last piece of an oversized table, with nothing flushed
        // since - the only case where a trailing remainder is merged back (see the end of this method).
        bool lastChunkIsOversizedTablePiece = false;

        // Linearized tables (RowBlock) are table-like everywhere a Markdown table is: never carried as overlap,
        // never counted as a lead-in. The Markdown strategy has none, so for it this is exactly IsTableBlock.
        bool IsTableLike((string Text, bool TableLike, int Index) b) => b.TableLike;

        void FlushCurrent()
        {
            if (current.Count == 0)
            {
                return;
            }

            string content = string.Join("\n\n", current.Select(b => b.Text));
            chunks.Add(new PackedChunk(content, tokenizer.CountTokens(content), current.Select(b => b.Index).ToList()));
            lastChunkIsOversizedTablePiece = false;
        }

        for (int index = 0; index < blocks.Count; index++)
        {
            RowBlock? rowBlock = blocks[index].Rows;
            string block = blocks[index].Text;
            bool isTable = rowBlock is not null || IsTableBlock(block);
            int blockTokens = tokenizer.CountTokens(block);
            bool shortLeadIn = current.Count > 0 && currentTokens <= MaxAttachedTextTokens && !current.Any(IsTableLike);

            // A row block that doesn't fit after its short lead-in (a statement title, "(In millions)") goes
            // the oversized route too, so the lead-in travels with it as a caption instead of being flushed
            // as a title-only chunk - the caption rule below, applied to row blocks too.
            if (blockTokens > maxTokens || (rowBlock is not null && shortLeadIn && currentTokens + blockTokens > maxTokens))
            {
                // A short lead-in right before an oversized table (e.g. "CONSOLIDATED STATEMENTS OF
                // STOCKHOLDERS' EQUITY" + "For the Years Ended ...") becomes the first table piece's caption,
                // not a near-empty chunk of its own: statement titles start their own sections
                // (SectionSplitter), so a title-only chunk ranks near the top for statement questions and
                // wastes a context slot on no data.
                string? caption = null;
                List<int> pieceBlocks = [index];
                if (isTable && shortLeadIn)
                {
                    caption = string.Join("\n\n", current.Select(b => b.Text));
                    pieceBlocks.InsertRange(0, current.Select(b => b.Index));
                }
                else
                {
                    FlushCurrent();
                }

                current.Clear();
                currentTokens = 0;

                List<string> pieces = (rowBlock is not null
                    ? SplitRowBlock(rowBlock, tokenizer, maxTokens, caption)
                    : isTable
                        ? SplitOversizedTable(block.Split('\n').ToList(), tokenizer, maxTokens, caption)
                        : SplitOversizedText(block, tokenizer, maxTokens)).ToList();

                for (int p = 0; p < pieces.Count; p++)
                {
                    // The caption rides on the first piece only (SplitRowBlock repeats it, SplitOversizedTable doesn't).
                    bool hasCaption = caption is not null && (p == 0 || rowBlock is not null);
                    chunks.Add(new PackedChunk(pieces[p], tokenizer.CountTokens(pieces[p]), hasCaption ? pieceBlocks : [index]));
                }

                lastChunkIsOversizedTablePiece = isTable;
                continue;
            }

            if (currentTokens + blockTokens > maxTokens && current.Count > 0)
            {
                FlushCurrent();

                // Light overlap: carry the previous chunk's last block forward, unless it's a table
                // (never duplicate a whole table) or too big to count as "overlap".
                (string Text, bool TableLike, int Index) lastBlock = current[^1];
                current.Clear();
                currentTokens = 0;

                if (!IsTableLike(lastBlock))
                {
                    int lastTokens = tokenizer.CountTokens(lastBlock.Text);
                    if (lastTokens <= overlapTokens)
                    {
                        current.Add(lastBlock);
                        currentTokens = lastTokens;
                    }
                }
            }

            current.Add((block, isTable, index));
            currentTokens += blockTokens;
        }

        // A short trailing remainder right after an oversized table's last piece - a statement's "See
        // accompanying notes..." footer, a one-line footnote - is appended to that piece, where it belongs:
        // as a tiny chunk of its own it ranks near the top for statement questions and wastes a context
        // slot on no data. Deliberately limited to this case: the short final paragraph of an ordinary
        // narrative section is left alone.
        string remainderText = string.Join("\n\n", current.Select(b => b.Text));
        if (lastChunkIsOversizedTablePiece && current.Count > 0 && !current.Any(IsTableLike)
            && tokenizer.CountTokens(remainderText) <= MaxAttachedTextTokens)
        {
            string merged = chunks[^1].Content + "\n\n" + remainderText;
            chunks[^1] = new PackedChunk(merged, tokenizer.CountTokens(merged), [.. chunks[^1].Blocks, .. current.Select(b => b.Index)]);
        }
        else
        {
            FlushCurrent();
        }

        return chunks;
    }

    private static bool IsTableBlock(string block) => block.TrimStart().StartsWith('|');

    // Rows are atomic and self-contained, so a row block splits between any two rows. Every piece repeats
    // the caption (the statement title and units that preceded the table) and the block's context line:
    // a row says which line item and which period, but not which statement - without it, a total on a
    // later piece reaches the model unlabelled, and it answers with the titled first piece's figure
    // instead. A single row over budget is emitted alone, never cut.
    private static IEnumerable<string> SplitRowBlock(RowBlock block, Tokenizer tokenizer, int maxTokens, string? caption)
    {
        string header = (caption is null ? string.Empty : caption + "\n\n") + (block.Context is null ? string.Empty : block.Context + "\n");
        int headerTokens = tokenizer.CountTokens(header);
        List<string> piece = new();
        int pieceTokens = headerTokens;

        foreach (string row in block.Rows)
        {
            int rowTokens = tokenizer.CountTokens(row);
            if (pieceTokens + rowTokens > maxTokens && piece.Count > 0)
            {
                yield return header + string.Join('\n', piece);
                piece.Clear();
                pieceTokens = headerTokens;
            }

            piece.Add(row);
            pieceTokens += rowTokens;
        }

        if (piece.Count > 0)
        {
            yield return header + string.Join('\n', piece);
        }
    }

    private static List<string> SplitIntoBlocks(string body)
    {
        string[] lines = body.Split('\n');
        List<string> blocks = new();
        List<string> current = new();
        bool inTable = false;

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            string block = string.Join('\n', current).Trim();
            if (block.Length > 0 && !IsContentFreeTable(block))
            {
                blocks.Add(block);
            }

            current.Clear();
        }

        foreach (string line in lines)
        {
            bool isTableLine = line.TrimStart().StartsWith('|');

            if (isTableLine)
            {
                if (!inTable)
                {
                    Flush();
                    inTable = true;
                }

                current.Add(line);
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                inTable = false;
            }
            else
            {
                if (inTable)
                {
                    Flush();
                    inTable = false;
                }

                current.Add(line);
            }
        }

        Flush();
        return blocks;
    }

    private static IEnumerable<string> SplitOversizedTable(List<string> lines, Tokenizer tokenizer, int maxTokens, string? caption)
    {
        string captionPrefix = caption is null ? string.Empty : caption + "\n\n";
        if (lines.Count < 3)
        {
            yield return captionPrefix + string.Join('\n', lines);
            yield break;
        }

        // The header block is everything before the first row-group label (e.g. "Revenue:"), not
        // just the syntactic 2-line Markdown header. Financial tables from this converter carry
        // their real column headers (e.g. "Year Ended June 30, ... 2026 ... 2025 ... 2024") as an
        // ordinary body row rather than the Markdown header row, so a fixed 2-line assumption would
        // silently drop the fiscal-year labels from every split after the first.
        // Capped: if the table has no row-group-label rows at all (a plain table), we don't want to
        // consume the entire thing as "header". Also stop at the first row that looks like real data
        // (a $ amount, a comma-grouped number, or a Markdown link) - otherwise a caption-only table
        // (e.g. an exhibit index with no "Revenue:"-style labels) would have its first few data rows
        // misread as part of the repeating header.
        int headerEnd = 2;
        while (headerEnd < lines.Count && headerEnd < 12 && !IsRowGroupLabel(lines[headerEnd]) && !LooksLikeDataRow(lines[headerEnd]))
        {
            headerEnd++;
        }

        // A label-only row can still sit above the fiscal-period row: MSFT's statements put
        // "(In millions)" (text in the first cell only, so it reads as a row-group label) above
        // "Year Ended June 30, ... 2026 ... 2025 ... 2024". Stopping there would drop the years from every
        // piece after the first, and a total there would reach the model with no year and no title. So the
        // header extends through the last period row found before the first data row.
        for (int i = headerEnd; i < lines.Count && i < 12 && !LooksLikeDataRow(lines[i]); i++)
        {
            if (IsPeriodHeaderRow(lines[i]))
            {
                headerEnd = i + 1;
            }
        }

        string header = string.Join('\n', lines.Take(headerEnd));
        int headerTokens = tokenizer.CountTokens(header);
        List<string> currentRows = new();

        // The caption rides on the first piece only, so only the first piece's row budget shrinks.
        int currentTokens = headerTokens + (caption is null ? 0 : tokenizer.CountTokens(captionPrefix));

        // Financial tables use "label-only" rows (e.g. "Revenue:", "Cost of revenue:") to group the
        // rows that follow. If a split falls between such a row and its numbers, the numbers lose
        // their label - so the label in force where a piece starts is repeated at its top. That has to
        // be captured when the piece starts, not when it's emitted: reading it at emit time put each
        // piece's own last label at its top instead (NFLX's "Fair value hedges:" heading its cash flow
        // hedge rows). A "Total ..." row closes the group, so its label isn't carried past it.
        string? currentRowGroupLabel = null;
        string? pieceStartLabel = null;

        for (int i = headerEnd; i < lines.Count; i++)
        {
            string row = lines[i];
            int rowTokens = tokenizer.CountTokens(row);

            if (currentTokens + rowTokens > maxTokens && currentRows.Count > 0)
            {
                yield return captionPrefix + BuildTablePiece(header, pieceStartLabel, currentRows);
                captionPrefix = string.Empty;
                currentRows.Clear();
                pieceStartLabel = IsRowGroupLabel(row) ? null : currentRowGroupLabel;
                currentTokens = headerTokens + (pieceStartLabel is null ? 0 : tokenizer.CountTokens(pieceStartLabel));
            }

            currentRows.Add(row);
            currentTokens += rowTokens;

            if (IsRowGroupLabel(row))
            {
                currentRowGroupLabel = row;
            }
            else if (IsTotalRow(row))
            {
                currentRowGroupLabel = null;
            }
        }

        if (currentRows.Count > 0)
        {
            yield return captionPrefix + BuildTablePiece(header, pieceStartLabel, currentRows);
        }
    }

    private static string BuildTablePiece(string header, string? rowGroupLabel, List<string> rows)
    {
        string prefix = rowGroupLabel is null ? header : header + "\n" + rowGroupLabel;
        return prefix + "\n" + string.Join('\n', rows);
    }

    private static bool IsRowGroupLabel(string row)
    {
        string[] cells = row.Trim().Trim('|').Split('|');
        return cells.Length > 1 && cells[0].Trim().Length > 0 && cells.Skip(1).All(string.IsNullOrWhiteSpace);
    }

    private static bool IsTotalRow(string row)
        => row.Trim().Trim('|').TrimStart().StartsWith("Total", StringComparison.OrdinalIgnoreCase);

    // "Year Ended June 30," / "Three Months Ended", or a row carrying two or more bare years
    // ("2026 | 2025 | 2024", "Fiscal 2026 | Fiscal 2025").
    private static bool IsPeriodHeaderRow(string row)
        => PeriodEndedRegex.IsMatch(row)
           || row.Split('|').Count(cell => BareYearCellRegex.IsMatch(cell.Trim())) >= 2;

    private static bool LooksLikeDataRow(string row)
        => row.Contains('$') || row.Contains("](") || CommaGroupedNumberRegex.IsMatch(row);

    // Some filing agents draw a purely decorative divider (e.g. a bordered horizontal rule at the top
    // of the cover page) using an HTML <table> with empty cells, instead of an <hr> or empty <p> (which
    // markitdown silently drops). markitdown always emits full Markdown table syntax for any <table>
    // tag regardless of content, so these leak through as a fake "table" of blank cells. A table where
    // every row, once "|" / "-" / whitespace are stripped, has nothing left carries zero information.
    private static bool IsContentFreeTable(string block)
        => block.TrimStart().StartsWith('|')
           && block.Where(c => c != '|' && c != '-' && !char.IsWhiteSpace(c)).ToArray() is { Length: 0 };

    private static IEnumerable<string> SplitOversizedText(string text, Tokenizer tokenizer, int maxTokens)
    {
        string remaining = text;
        while (remaining.Length > 0)
        {
            int idx = tokenizer.GetIndexByTokenCount(remaining, maxTokens, out _, out _);
            if (idx <= 0)
            {
                idx = Math.Min(remaining.Length, 1);
            }

            if (idx < remaining.Length)
            {
                int breakAt = remaining.LastIndexOf(' ', Math.Max(idx - 1, 0));
                if (breakAt > 0)
                {
                    idx = breakAt;
                }
            }

            string piece = remaining[..idx].Trim();
            if (piece.Length > 0)
            {
                yield return piece;
            }

            remaining = remaining[idx..].TrimStart();
        }
    }
}
