using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace RagFilingExplorer.Local.Chunking;

internal enum LinearizedTableKind
{
    /// <summary>Has numeric data rows, each value assigned to a column header.</summary>
    Financial,

    /// <summary>No numeric data rows (cover-page checkboxes, exhibit index, signatures) - rows as text.</summary>
    Text,

    /// <summary>Couldn't be linearized safely - see <see cref="LinearizedTable.FallbackReason"/>.</summary>
    Fallback,

    /// <summary>No text at all (decorative spacer tables).</summary>
    Empty,
}

/// <param name="ColumnKey">Identity of the column within the table ("b{header block}c{leaf}"), for checks.</param>
/// <param name="ContextRef">The inline-XBRL contextRef of a tagged value - carried through, with its concept and unit, for the test oracle only; never used for alignment.</param>
internal sealed record LinearizedValue(string ColumnKey, string ColumnLabel, string Text, string? ContextRef, string? Concept, string? Unit);

internal sealed record LinearizedRow(string? GroupLabel, string Label, IReadOnlyList<LinearizedValue> Values);

internal sealed record LinearizedTable(
    LinearizedTableKind Kind,
    string? FallbackReason,
    string? Units,
    IReadOnlyList<LinearizedRow> Rows,
    IReadOnlyList<string> TextLines);

/// <summary>
/// Turns one HTML table into self-contained lines - "Label — Column: value | Column: value" - working from
/// the HTML itself rather than markitdown's Markdown, because markitdown drops colspan (68-83 tables per
/// filing use it), which is what left Markdown rows misaligned and forced the Markdown strategy to guess.
///
/// Only layout is used: expanded colspan positions, which rows carry numbers, which carry header text.
/// Inline-XBRL tags are read only to pass their contextRef through on each value for the test oracle -
/// using them for alignment would make that check circular. A table the rules can't linearize
/// unambiguously is reported as Fallback with a reason, never guessed.
/// </summary>
internal static partial class HtmlTableLinearizer
{
    [GeneratedRegex(@"^\(?\$?\s*\(?\s*[-–—]?\s*\d[\d,]*(\.\d+)?\s*\)?\s*%?\s*\)?$")]
    private static partial Regex NumericRegex();

    [GeneratedRegex(@"^[—–-]+$")]
    private static partial Regex DashRegex();

    [GeneratedRegex(@"^(19|20)\d{2}$")]
    private static partial Regex BareYearRegex();

    [GeneratedRegex(@"^\(?\s*(in|dollars in|amounts in|\$ in)\s+(millions|thousands|billions)", RegexOptions.IgnoreCase)]
    private static partial Regex UnitsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^((as of|as at|at|for the)\s+)?(((fiscal\s+)?(years?|quarters?|months|weeks)\s+ended,?)\s+)?(january|february|march|april|may|june|july|august|september|october|november|december)\s+\d{1,2},?\s+(19|20)\d{2}:?$|^(fiscal\s+(year\s+)?)?(19|20)\d{2}:?$", RegexOptions.IgnoreCase)]
    private static partial Regex PeriodCaptionRegex();

    private sealed record Fact(string ContextRef, string Concept, string Unit);

    private sealed record Cell(int Start, int End, string Text, IReadOnlyList<Fact> Facts);

    private sealed record ValueGroup(int Start, int End, string Text, bool IsNumeric, bool IsBareYear, Fact? Fact);

    private sealed record HeaderCell(int Row, int Start, int End, string Text);

    private sealed record Leaf(string Key, int Start, int End, string Label);

    public static LinearizedTable Linearize(IHtmlTableElement table)
    {
        List<List<Cell>> rows = table.Rows.Select(ReadRow).ToList();
        List<List<Cell>> nonEmptyRows = rows.Select(r => r.Where(c => c.Text.Length > 0).ToList()).Where(r => r.Count > 0).ToList();

        if (nonEmptyRows.Count == 0)
        {
            return new LinearizedTable(LinearizedTableKind.Empty, null, null, [], []);
        }

        // Label column extent: the most common end position of the leftmost (non-numeric) cell among rows
        // that carry a real number. Header rows put their captions left of this and column headers right.
        int? labelEnd = nonEmptyRows
            .Where(r => r.Skip(1).Any(c => IsRealNumber(c.Text)) && !IsNumericText(r[0].Text))
            .GroupBy(r => r[0].End)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => (int?)g.Key)
            .FirstOrDefault();

        if (labelEnd is null)
        {
            return new LinearizedTable(LinearizedTableKind.Text, null, null, [],
                nonEmptyRows.Select(r => string.Join(" | ", r.Select(c => c.Text))).ToList());
        }

        string? units = null;
        string? groupLabel = null;
        string? periodCaption = null; // a label-column period row ("June 30, 2026") - on every row path below it
        int block = -1;
        List<HeaderCell> headerCells = new();
        List<string> tableCaptions = new(); // above every header row, e.g. "June 30, 2026" - kept for the whole table
        List<string> captions = new();      // label-column text of the current header block's rows
        List<Leaf> leaves = new();
        List<string> unconsumedHeaderRows = new(); // header rows not yet followed by a data row
        bool lastWasData = false;
        List<LinearizedRow> output = new();

        for (int rowIndex = 0; rowIndex < nonEmptyRows.Count; rowIndex++)
        {
            List<Cell> row = nonEmptyRows[rowIndex];
            // By where a cell *starts*: NDAQ's group labels ("Transaction-based expenses:") often span wider
            // than the usual label column, and by their end they read as column headers.
            string label = string.Join(" ", row.Where(c => c.Start <= labelEnd).Select(c => c.Text));
            List<Cell> valueCells = row.Where(c => c.Start > labelEnd).ToList();

            if (valueCells.Count == 0)
            {
                if (units is null && UnitsRegex().IsMatch(label))
                {
                    units = label;
                }
                else if (PeriodCaptionRegex().IsMatch(label))
                {
                    // A period in the label column heads the rows below it (MSFT's investment tables:
                    // "June 30, 2026" after the header row, then section labels) - kept on every row path
                    // rather than overwritten by the next group label.
                    periodCaption = label.TrimEnd(':');
                    groupLabel = null;
                    output.Add(new LinearizedRow(null, label, []));
                }
                else if (headerCells.Count == 0 && block < 0 && output.Count == 0)
                {
                    // Before any header or data row it captions the whole table (MSFT's investment tables
                    // open with "June 30, 2026"): folded into every column label so no row loses it. After
                    // the headers, the same shape is a group label ("Revenue:").
                    tableCaptions.Add(label);
                }
                else
                {
                    groupLabel = label;
                    output.Add(new LinearizedRow(null, label, []));
                }

                continue;
            }

            List<ValueGroup> groups = MergeFragments(valueCells);
            bool isData = groups.Any(g => g.IsNumeric && !g.IsBareYear);

            if (!isData)
            {
                // A header row. One arriving after data rows starts a new header block (stacked panels).
                if (lastWasData)
                {
                    headerCells.Clear();
                    captions.Clear();
                    leaves.Clear();
                }

                unconsumedHeaderRows.Add(string.Join(" | ", row.Select(c => c.Text)));

                if (label.Length > 0)
                {
                    if (units is null && UnitsRegex().IsMatch(label))
                    {
                        units = label;
                    }
                    else
                    {
                        captions.Add(label);
                    }
                }

                headerCells.AddRange(valueCells.Select(c => new HeaderCell(rowIndex, c.Start, c.End, c.Text)));
                leaves.Clear();
                lastWasData = false;
                continue;
            }

            if (!lastWasData && headerCells.Count > 0 && leaves.Count == 0)
            {
                block++;
                List<string> blockCaptions = [.. tableCaptions, .. captions];
                string? problem = BuildLeaves(headerCells, blockCaptions, block, leaves, ref units);
                if (problem is not null)
                {
                    return Fallback(problem);
                }
            }

            lastWasData = true;
            unconsumedHeaderRows.Clear(); // they label this row's columns

            List<LinearizedValue> values = new();
            if (leaves.Count == 0)
            {
                // No header above the data: only a single-value row is unambiguous ("Label: value").
                if (groups.Count != 1)
                {
                    return Fallback("data row with several values but no column headers");
                }

                values.Add(new LinearizedValue("b-c0", string.Join(" ", tableCaptions), groups[0].Text, groups[0].Fact?.ContextRef, groups[0].Fact?.Concept, groups[0].Fact?.Unit));
            }
            else
            {
                HashSet<string> used = new();
                foreach (ValueGroup group in groups)
                {
                    Leaf? leaf = NearestLeaf(leaves, group);
                    if (leaf is null)
                    {
                        return Fallback($"value '{group.Text}' is equidistant from two column headers");
                    }

                    string key = leaf.Key;
                    if (!used.Add(key))
                    {
                        // One header spanning two values - NFLX's "Change 2025 vs. 2024" over an amount and a
                        // percentage - is the table's own structure: both stay under it, keyed by position.
                        // Only a value merely *nearest* to an already-used header is ambiguous.
                        if (!Overlaps(leaf.Start, leaf.End, group.Start, group.End))
                        {
                            return Fallback($"two values in row '{label}' map to column '{leaf.Label}'");
                        }

                        key = $"{leaf.Key}@{group.Start}";
                        used.Add(key);
                    }

                    values.Add(new LinearizedValue(key, leaf.Label, group.Text, group.Fact?.ContextRef, group.Fact?.Concept, group.Fact?.Unit));
                }
            }

            string? path = periodCaption is null ? groupLabel : groupLabel is null ? periodCaption : $"{periodCaption} > {groupLabel.TrimEnd(':')}";
            output.Add(new LinearizedRow(path, label, values));

            if (label.StartsWith("Total", StringComparison.OrdinalIgnoreCase))
            {
                groupLabel = null;
            }
        }

        // Rows read as headers that no data row ever followed aren't headers at all - MSFT's exhibit index
        // ends with text-only rows ("31.1 | Certification of Chief Executive Officer ... | X"), which were
        // silently dropped until kept here as plain lines.
        output.AddRange(unconsumedHeaderRows.Select(text => new LinearizedRow(null, text, [])));

        LinearizedTable linearized = new(LinearizedTableKind.Financial, null, units, output, []);

        // Safety net for whatever rule misses next: every cell's text must survive somewhere in the
        // rendered output (row paths, column labels, values, units), or the table falls back to Markdown,
        // where nothing is lost. Silent content loss becomes a counted fallback with a reason.
        string rendered = string.Join('\n', Render(linearized));
        string? lost = nonEmptyRows.SelectMany(r => r).Select(c => c.Text).Distinct().FirstOrDefault(t => !rendered.Contains(t, StringComparison.Ordinal));
        return lost is null ? linearized : Fallback($"cell text lost: '{(lost.Length > 60 ? lost[..60] + "..." : lost)}'");

        LinearizedTable Fallback(string reason) => new(LinearizedTableKind.Fallback, reason, null, [], []);
    }

    /// <summary>
    /// Renders a linearized table as the lines a chunk would carry. <paramref name="omitNil"/> drops "—"
    /// values (equity roll-forwards are mostly nil cells); <paramref name="factorCaption"/> writes a
    /// caption shared by every column label ("Year Ended June 30,") once, instead of on every value.
    /// </summary>
    public static IEnumerable<string> Render(LinearizedTable table, bool omitNil = false, bool factorCaption = false)
    {
        if (table.Units is not null)
        {
            yield return table.Units;
        }

        string caption = factorCaption ? SharedCaption(table) : string.Empty;
        if (caption.Length > 0)
        {
            yield return caption;
        }

        foreach (string line in RenderRows(table, omitNil, caption))
        {
            yield return line;
        }
    }

    /// <summary>
    /// The form the Linearized strategy ships, chosen in the spike: nil "—" values omitted, and the units
    /// plus a caption shared by every column ("Year Ended June 30,") written once, as the block's context
    /// line - which TokenChunker repeats on every piece if the block has to be split.
    /// </summary>
    /// <summary>
    /// The text path alone, for any table: each non-empty row's cells joined with " | ", as <see cref="Linearize"/>
    /// does for a table with no numeric data rows. It keeps every cell's text by construction, so the Structured
    /// strategy uses it where <see cref="Linearize"/> falls back (see StructuredChunkingStrategy).
    /// </summary>
    public static LinearizedTable LinearizeAsText(IHtmlTableElement table)
    {
        List<string> lines = table.Rows.Select(ReadRow)
            .Select(r => r.Where(c => c.Text.Length > 0).ToList())
            .Where(r => r.Count > 0)
            .Select(r => string.Join(" | ", r.Select(c => c.Text)))
            .ToList();
        return new LinearizedTable(lines.Count == 0 ? LinearizedTableKind.Empty : LinearizedTableKind.Text, null, null, [], lines);
    }

    /// <summary>
    /// How many of a table's leading non-empty rows are column names, read from the only marker these filers
    /// use - bold text (no filing uses &lt;thead&gt; or &lt;th&gt;): the leading rows whose text is all bold,
    /// or 0 if every row is (a bold cover-page box has no header). For a text table's continuation pieces
    /// (StructuredChunkingStrategy); a bold title row is caught too ("Critical audit matter" tables), which only
    /// puts the title on every piece.
    /// </summary>
    public static int LeadingBoldRowCount(IHtmlTableElement table)
    {
        List<IHtmlTableRowElement> rows = table.Rows.Where(r => r.Cells.Any(c => c.TextContent.Trim().Length > 0)).ToList();
        int count = rows.TakeWhile(r => r.Cells.Where(c => c.TextContent.Trim().Length > 0).All(IsAllTextBold)).Count();
        return count == rows.Count ? 0 : count;
    }

    private static bool IsAllTextBold(IHtmlTableCellElement cell) =>
        cell.Descendants<IText>().Where(t => t.Data.Trim().Length > 0).All(t => BoldBetween(t.ParentElement, cell));

    // Bold if the text's own element or any ancestor up to the cell is <b>/<strong> or styled font-weight bold/700.
    private static bool BoldBetween(IElement? element, IElement cell)
    {
        for (IElement? e = element; e is not null; e = e.ParentElement)
        {
            string style = (e.GetAttribute("style") ?? string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
            if (e.LocalName is "b" or "strong" || style.Contains("font-weight:bold") || style.Contains("font-weight:700"))
            {
                return true;
            }

            if (e == cell)
            {
                return false;
            }
        }

        return false;
    }

    public static RowBlock ToRowBlock(LinearizedTable table)
    {
        string caption = SharedCaption(table);
        string context = string.Join(" ", new[] { table.Units ?? string.Empty, caption }.Where(s => s.Length > 0));
        return new RowBlock(context.Length == 0 ? null : context, RenderRows(table, omitNil: true, caption).ToList());
    }

    private static IEnumerable<string> RenderRows(LinearizedTable table, bool omitNil, string caption)
    {
        foreach (string line in table.TextLines)
        {
            yield return line;
        }

        foreach (LinearizedRow row in table.Rows)
        {
            if (row.Values.Count == 0)
            {
                yield return row.Label;
                continue;
            }

            string path = row.GroupLabel is null ? row.Label : $"{row.GroupLabel.TrimEnd(':')} > {row.Label}";
            IEnumerable<LinearizedValue> shown = omitNil ? row.Values.Where(v => !DashRegex().IsMatch(v.Text)) : row.Values;
            List<(string Column, List<string> Texts)> columns = new();
            foreach (LinearizedValue v in shown)
            {
                string column = v.ColumnLabel[Math.Min(caption.Length, v.ColumnLabel.Length)..].Trim();
                if (columns.Count > 0 && columns[^1].Column == column && column.Length > 0)
                {
                    columns[^1].Texts.Add(v.Text); // sub-columns under one header: "Change: 7,048,262 / 18%"
                }
                else
                {
                    columns.Add((column, [v.Text]));
                }
            }

            string values = string.Join(" | ", columns.Select(c =>
                c.Column.Length == 0 ? string.Join(" / ", c.Texts) : $"{c.Column}: {string.Join(" / ", c.Texts)}"));
            if (values.Length == 0)
            {
                yield return path;
                continue;
            }
            yield return path.Length == 0 ? values : $"{path} — {values}";
        }
    }

    // The longest word-boundary prefix shared by every column label - only when it leaves each label
    // something of its own, so "2026" / "2025" stay distinguishable.
    private static string SharedCaption(LinearizedTable table)
    {
        List<string> labels = table.Rows.SelectMany(r => r.Values).Select(v => v.ColumnLabel).Where(l => l.Length > 0).Distinct().ToList();
        if (labels.Count < 2)
        {
            return string.Empty;
        }

        string prefix = labels.Aggregate((a, b) => new string(a.Zip(b).TakeWhile(p => p.First == p.Second).Select(p => p.First).ToArray()));
        int cut = prefix.LastIndexOf(' ');
        prefix = cut < 0 ? string.Empty : prefix[..cut];
        return labels.All(l => l.Length > prefix.Length + 1) ? prefix.Trim() : string.Empty;
    }

    private static List<Cell> ReadRow(IHtmlTableRowElement row)
    {
        List<Cell> cells = new();
        int position = 0;
        foreach (IHtmlTableCellElement cell in row.Cells)
        {
            int span = Math.Max(1, cell.ColumnSpan);
            List<Fact> facts = cell.Descendants<IElement>()
                .Where(e => e.LocalName.Equals("ix:nonfraction", StringComparison.OrdinalIgnoreCase))
                .Select(e => new Fact(e.GetAttribute("contextref") ?? string.Empty, e.GetAttribute("name") ?? string.Empty, e.GetAttribute("unitref") ?? string.Empty))
                .ToList();
            cells.Add(new Cell(position, position + span - 1, CellText(cell), facts));
            position += span;
        }

        return cells;
    }

    // TextContent would glue "comprehensive<br>income" into one word; block boundaries become spaces.
    private static string CellText(INode node)
    {
        StringBuilder text = new();
        Append(node);
        return WhitespaceRegex().Replace(text.ToString().Replace(' ', ' '), " ").Trim();

        void Append(INode current)
        {
            foreach (INode child in current.ChildNodes)
            {
                if (child is IText t)
                {
                    text.Append(t.Data);
                }
                else if (child is IElement e)
                {
                    bool block = e.LocalName is "p" or "div" or "br" or "li" or "tr";
                    if (block)
                    {
                        text.Append(' ');
                    }

                    Append(e);
                    if (block)
                    {
                        text.Append(' ');
                    }
                }
            }
        }
    }

    // "$" and "(" belong to the value on their right, ")" and "%" to the value on their left - filers put
    // them in cells of their own (MSFT: "$" | "133,812"; negatives as "(5" | ")").
    private static List<ValueGroup> MergeFragments(List<Cell> cells)
    {
        List<ValueGroup> groups = new();
        string prefix = string.Empty;

        foreach (Cell cell in cells)
        {
            string text = cell.Text;
            if (text is "$" or "(" or "$(" or "($")
            {
                prefix += text;
                continue;
            }

            if (text is ")" or "%" or ")%" or "%)" && groups.Count > 0)
            {
                ValueGroup last = groups[^1];
                groups[^1] = last with { Text = last.Text + text, End = cell.End };
                continue;
            }

            string merged = prefix.Length > 0 ? $"{prefix}{text}" : text;
            groups.Add(new ValueGroup(
                cell.Start,
                cell.End,
                merged,
                IsNumericText(text),
                BareYearRegex().IsMatch(text),
                cell.Facts.FirstOrDefault(f => f.ContextRef.Length > 0)));
            prefix = string.Empty;
        }

        return groups;
    }

    private static bool IsNumericText(string text) => NumericRegex().IsMatch(text) || DashRegex().IsMatch(text);

    private static bool IsRealNumber(string text) => IsNumericText(text) && !BareYearRegex().IsMatch(text) && !DashRegex().IsMatch(text);

    // Columns (leaves) are the narrowest header cells: a cell spanning two or more separate header cells in
    // other rows is a group header over several columns, never a column itself. Each leaf's label stacks
    // the captions and every group header overlapping it, widest first - "Year ended December 31," over
    // "2025" (NFLX), and equally "2026" over "Shares" / "Amount" when a filer puts the year row *below*
    // them (MSFT's share repurchases).
    //
    // A group header covering every column says nothing about any one column, so it becomes a caption (or
    // the units): NFLX and NDAQ put "(in thousands, except percentages)" as the last header row, across
    // every column - taken as a column, it swallowed the whole header block into one.
    private static string? BuildLeaves(List<HeaderCell> headerCells, List<string> captions, int block, List<Leaf> leaves, ref string? units)
    {
        // A units cell sitting among the header cells ("(in millions)" over some of NDAQ's columns) is the
        // table's units, never part of a column label.
        foreach (HeaderCell unitsCell in headerCells.Where(h => UnitsRegex().IsMatch(h.Text)).ToList())
        {
            units ??= unitsCell.Text;
            headerCells.Remove(unitsCell);
        }

        if (headerCells.Count == 0)
        {
            return null; // units were the only header (a single-column roll-forward): rows read "Label: value"
        }

        bool IsSpanning(HeaderCell h)
        {
            List<HeaderCell> covered = headerCells.Where(o => o != h && o.Row != h.Row && Overlaps(o.Start, o.End, h.Start, h.End)).ToList();
            return covered.Any(a => covered.Any(b => !Overlaps(a.Start, a.End, b.Start, b.End)));
        }

        List<HeaderCell> columns = headerCells.Where(h => !IsSpanning(h)).ToList();
        List<HeaderCell> lowest = columns
            .Where(h => !columns.Any(o => o.Row > h.Row && Overlaps(o.Start, o.End, h.Start, h.End)))
            .OrderBy(h => h.Start)
            .ToList();

        if (lowest.Count == 0)
        {
            return "no column headers";
        }

        for (int i = 1; i < lowest.Count; i++)
        {
            if (lowest[i].Start <= lowest[i - 1].End)
            {
                return "overlapping column headers";
            }
        }

        foreach (HeaderCell wide in headerCells.Where(h => IsSpanning(h) && lowest.All(l => Overlaps(h.Start, h.End, l.Start, l.End))).ToList())
        {
            if (units is null && UnitsRegex().IsMatch(wide.Text))
            {
                units = wide.Text;
            }
            else
            {
                captions.Add(wide.Text);
            }

            headerCells.Remove(wide);
        }

        foreach ((HeaderCell leaf, int index) in lowest.Select((h, i) => (h, i)))
        {
            IEnumerable<string> stack = captions.Concat(headerCells
                .Where(h => Overlaps(h.Start, h.End, leaf.Start, leaf.End) && (h == leaf || !columns.Contains(h) || h.Row < leaf.Row))
                .OrderByDescending(h => h.End - h.Start)
                .ThenBy(h => h.Row)
                .Select(h => h.Text));
            leaves.Add(new Leaf($"b{block}c{index}", leaf.Start, leaf.End, string.Join(" ", stack.Distinct())));
        }

        return null;
    }

    // Overlap wins; otherwise the nearest header by column distance. A tie is ambiguous (null) - except
    // when the tie is between a header on the left and one on the right, where the left one is taken:
    // filers without colspan put the year over the "$" cell, left of the number (MSFT).
    private static Leaf? NearestLeaf(List<Leaf> leaves, ValueGroup group)
    {
        List<(Leaf Leaf, int Distance)> ranked = leaves
            .Select(l => (l, Overlaps(l.Start, l.End, group.Start, group.End) ? 0 : Math.Min(Math.Abs(l.Start - group.End), Math.Abs(group.Start - l.End))))
            .OrderBy(x => x.Item2)
            .ThenBy(x => x.l.Start)
            .ToList();

        if (ranked.Count > 1 && ranked[0].Distance == ranked[1].Distance && ranked[0].Distance == 0)
        {
            return null;
        }

        return ranked[0].Leaf;
    }

    private static bool Overlaps(int aStart, int aEnd, int bStart, int bEnd) => aStart <= bEnd && bStart <= aEnd;
}
