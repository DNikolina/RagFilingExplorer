using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace RagFilingExplorer.Local.Chunking;

/// <summary>
/// Splits a section's body text into token-bounded chunks. Paragraphs are packed greedily up to
/// <paramref name="maxTokens"/>-1; a run of contiguous Markdown table rows is treated as one atomic
/// block so a table isn't sliced mid-row-group. If a single block (paragraph or table) exceeds
/// maxTokens on its own, it's split further - for tables, by repeating the header + separator row
/// on every split piece, so a chunk with a number always keeps the column/fiscal-year labels next to it.
/// </summary>
internal static class TokenChunker
{
    private static readonly Regex CommaGroupedNumberRegex = new(@"\d{1,3}(,\d{3})+", RegexOptions.Compiled);

    public static List<(string Content, int Tokens)> Chunk(string body, Tokenizer tokenizer, int maxTokens, int overlapTokens)
    {
        List<string> blocks = SplitIntoBlocks(body);
        List<(string Content, int Tokens)> chunks = new();
        List<string> current = new();
        int currentTokens = 0;

        void FlushCurrent()
        {
            if (current.Count == 0)
            {
                return;
            }

            string content = string.Join("\n\n", current);
            chunks.Add((content, tokenizer.CountTokens(content)));
        }

        foreach (string block in blocks)
        {
            bool isTable = block.TrimStart().StartsWith('|');
            int blockTokens = tokenizer.CountTokens(block);

            if (blockTokens > maxTokens)
            {
                FlushCurrent();
                current.Clear();
                currentTokens = 0;

                IEnumerable<string> pieces = isTable
                    ? SplitOversizedTable(block.Split('\n').ToList(), tokenizer, maxTokens)
                    : SplitOversizedText(block, tokenizer, maxTokens);

                foreach (string piece in pieces)
                {
                    chunks.Add((piece, tokenizer.CountTokens(piece)));
                }

                continue;
            }

            if (currentTokens + blockTokens > maxTokens && current.Count > 0)
            {
                FlushCurrent();

                // Light overlap: carry the previous chunk's last block forward, unless it's a table
                // (never duplicate a whole table) or too big to count as "overlap".
                string lastBlock = current[^1];
                bool lastIsTable = lastBlock.TrimStart().StartsWith('|');
                current.Clear();
                currentTokens = 0;

                if (!lastIsTable)
                {
                    int lastTokens = tokenizer.CountTokens(lastBlock);
                    if (lastTokens <= overlapTokens)
                    {
                        current.Add(lastBlock);
                        currentTokens = lastTokens;
                    }
                }
            }

            current.Add(block);
            currentTokens += blockTokens;
        }

        FlushCurrent();
        return chunks;
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

    private static IEnumerable<string> SplitOversizedTable(List<string> lines, Tokenizer tokenizer, int maxTokens)
    {
        if (lines.Count < 3)
        {
            yield return string.Join('\n', lines);
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

        string header = string.Join('\n', lines.Take(headerEnd));
        int headerTokens = tokenizer.CountTokens(header);
        List<string> currentRows = new();
        int currentTokens = headerTokens;

        // Financial tables use "label-only" rows (e.g. "Revenue:", "Cost of revenue:") to group the
        // rows that follow. If a split falls between such a row and its numbers, the numbers lose
        // their label - so the nearest one is carried into the next piece along with the header.
        string? currentRowGroupLabel = null;

        for (int i = headerEnd; i < lines.Count; i++)
        {
            string row = lines[i];
            int rowTokens = tokenizer.CountTokens(row);

            if (currentTokens + rowTokens > maxTokens && currentRows.Count > 0)
            {
                yield return BuildTablePiece(header, currentRowGroupLabel, currentRows);
                currentRows.Clear();
                currentTokens = headerTokens + (currentRowGroupLabel is null ? 0 : tokenizer.CountTokens(currentRowGroupLabel));
            }

            currentRows.Add(row);
            currentTokens += rowTokens;

            if (IsRowGroupLabel(row))
            {
                currentRowGroupLabel = row;
            }
        }

        if (currentRows.Count > 0)
        {
            yield return BuildTablePiece(header, currentRowGroupLabel, currentRows);
        }
    }

    private static string BuildTablePiece(string header, string? rowGroupLabel, List<string> rows)
    {
        bool needsLabel = rowGroupLabel is not null && (rows.Count == 0 || rows[0] != rowGroupLabel);
        string prefix = needsLabel ? header + "\n" + rowGroupLabel : header;
        return prefix + "\n" + string.Join('\n', rows);
    }

    private static bool IsRowGroupLabel(string row)
    {
        string[] cells = row.Trim().Trim('|').Split('|');
        return cells.Length > 1 && cells[0].Trim().Length > 0 && cells.Skip(1).All(string.IsNullOrWhiteSpace);
    }

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
