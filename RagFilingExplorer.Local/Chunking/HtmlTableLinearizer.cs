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
/// <param name="ContextRef">The inline-XBRL contextRef of a tagged value - carried through, with its concept and unit - PeriodLabels names a roll-forward row's period from it, and the tests check alignment against it; never used for alignment.</param>
/// <param name="ColumnParts">The header cells <paramref name="ColumnLabel"/> is joined from, widest first - a caption shared by every column is cut only between them.</param>
internal sealed record LinearizedValue(string ColumnKey, string ColumnLabel, string Text, string? ContextRef, string? Concept, string? Unit, IReadOnlyList<string>? ColumnParts = null);

internal sealed record LinearizedRow(string? GroupLabel, string Label, IReadOnlyList<LinearizedValue> Values);

internal sealed record LinearizedTable(
    LinearizedTableKind Kind,
    string? FallbackReason,
    string? Units,
    IReadOnlyList<LinearizedRow> Rows,
    IReadOnlyList<string> TextLines);

/// <summary>
/// Turns one HTML table into self-contained lines - "Label — Column: value | Column: value" - working from
/// the HTML itself rather than markitdown's Markdown, because markitdown drops colspan (most of a filing's
/// tables use it), which is what left Markdown rows misaligned and forced the Markdown strategy to guess.
///
/// Only layout is used: cell positions with colspan and rowspan expanded, which rows carry numbers, which carry
/// header text. Inline-XBRL tags are read only to pass their contextRef through on each value (for PeriodLabels and
/// the tests) - using them for alignment would make the tests' alignment check circular. A table the rules can't linearize
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

    [GeneratedRegex(@"^(19|20)\d{2}\b")]
    private static partial Regex LeadingYearRegex();

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"^\(?\s*(in|dollars in|amounts in|\$ in)\s+(millions|thousands|billions)", RegexOptions.IgnoreCase)]
    private static partial Regex UnitsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^((as of|as at|at|for the)\s+)?(((fiscal\s+)?(years?|quarters?|months|weeks)\s+ended,?)\s+)?(january|february|march|april|may|june|july|august|september|october|november|december)\s+\d{1,2},?\s+(19|20)\d{2}:?$|^(fiscal\s+(year\s+)?)?(19|20)\d{2}:?$", RegexOptions.IgnoreCase)]
    private static partial Regex PeriodCaptionRegex();

    // A row label's indent from its inline style (CellIndent); "padding-left" but never "line-height" or "margin-top".
    [GeneratedRegex(@"(?<![-\w])(?:padding-left|margin-left|text-indent|padding-inline-start|margin-inline-start|left):\s*(?<value>-?[\d.]+)(?<unit>pt|px|in|em|%)?")]
    private static partial Regex IndentRegex();

    [GeneratedRegex(@"(?<![-\w])padding:\s*([^;""]+)")]
    private static partial Regex PaddingShorthandRegex();

    [GeneratedRegex(@"^(?<value>-?[\d.]+)(?<unit>pt|px|in|em|%)?$")]
    private static partial Regex LengthRegex();

    private sealed record Fact(string ContextRef, string Concept, string Unit);

    // Indent: how far in the cell's first text starts, in points (CellIndent).
    private sealed record Cell(int Start, int End, string Text, IReadOnlyList<Fact> Facts, double Indent);

    private sealed record ValueGroup(int Start, int End, string Text, bool IsNumeric, bool IsBareYear, Fact? Fact);

    private sealed record HeaderCell(int Row, int Start, int End, string Text);

    // A row not named as a total is taken for one only when its figures add up in this many columns: one could be
    // chance - MSFT's table of contents, where pages 3 + 12 + 14 make "Item 1B." on page 29 look like a total.
    private const int UnnamedTotalAmounts = 2;

    private sealed record Leaf(string Key, int Start, int End, string Label, IReadOnlyList<string> Parts);

    /// <summary>
    /// The data rows of a run - a group, from its label, or everything since the last total - for the two signs that a
    /// row isn't in the group still in force. A group runs until a "Total" row, but filers print a row outside it
    /// two other ways: back at the group label's own indent while the group's rows sat deeper (MSFT's "Comprehensive
    /// income" after "Other comprehensive income (loss), net of tax:", ORCL's "Effect of exchange rate changes" after
    /// "Cash flows from financing activities:"), or as a grand total right after the last group, indented like its
    /// rows (NFLX's derivatives "Total" over both hedging groups) - there only the figures tell, by adding up.
    /// </summary>
    private sealed class RunningRows(double labelIndent)
    {
        // Indents are compared within one table; anything closer than this is the same indent.
        private const double SameIndent = 0.5;

        private readonly Dictionary<string, (decimal Sum, int Count)> sums = [];
        private double? shallowestRow;

        public int Count { get; private set; }

        public void Add(double indent, IReadOnlyList<LinearizedValue> values)
        {
            Count++;
            shallowestRow = Math.Min(shallowestRow ?? indent, indent);
            foreach (LinearizedValue value in values)
            {
                if (Amount(value.Text) is { } amount)
                {
                    (decimal sum, int count) = sums.GetValueOrDefault(value.ColumnKey);
                    sums[value.ColumnKey] = (sum + amount.Value, count + 1);
                }
            }
        }

        // A row (not a total - a group's own total is often printed at its label's indent) back at the label's indent,
        // when every row of the group so far was indented deeper.
        public bool IsOutdentedPast(double indent, string label) =>
            label.Length > 0 && shallowestRow is { } deepest && deepest > labelIndent + SameIndent && indent <= labelIndent + SameIndent;

        // A total whose every figure is the sum of these rows.
        public bool AddsUpTo(IReadOnlyList<LinearizedValue> values, int minAmounts = 1)
        {
            List<(decimal Value, decimal Unit, string Key)> amounts = values
                .Select(v => (Amount: Amount(v.Text), v.ColumnKey))
                .Where(a => a.Amount is not null)
                .Select(a => (a.Amount!.Value.Value, a.Amount!.Value.Unit, a.ColumnKey))
                .ToList();
            return amounts.Count >= minAmounts && amounts.All(a => sums.TryGetValue(a.Key, out var addends) && AddsUp(addends, (a.Value, a.Unit)));
        }

        // A row whose figures are the sum of every running row but not of this group's rows alone.
        public bool IsGrandTotal(IReadOnlyList<LinearizedValue> values, RunningRows running, int minAmounts)
        {
            if (running.Count <= Count)
            {
                return false;
            }

            int compared = 0;
            bool ofGroup = true, ofAll = true;
            foreach (LinearizedValue value in values)
            {
                if (Amount(value.Text) is not { } total)
                {
                    continue;
                }

                compared++;
                ofGroup &= sums.TryGetValue(value.ColumnKey, out var mine) && AddsUp(mine, total);
                ofAll &= running.sums.TryGetValue(value.ColumnKey, out var all) && AddsUp(all, total);
            }

            return compared >= minAmounts && ofAll && !ofGroup;
        }

        // Each addend is rounded to the total's last digit, so the sum may be off by half a unit per addend.
        private static bool AddsUp((decimal Sum, int Count) addends, (decimal Value, decimal Unit) total) =>
            Math.Abs(addends.Sum - total.Value) <= total.Unit / 2 * addends.Count;

        // "$(1,610,019)" -> -1610019 in units of 1; "3.32" in units of 0.01. Percentages and dashes aren't amounts.
        private static (decimal Value, decimal Unit)? Amount(string text)
        {
            string digits = text.Replace("$", string.Empty).Replace(",", string.Empty).Replace(" ", string.Empty);
            bool negative = digits.StartsWith('(') && digits.EndsWith(')');
            digits = digits.Trim('(', ')');
            if (!decimal.TryParse(digits, System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal value))
            {
                return null;
            }

            int decimals = digits.Contains('.') ? digits.Length - digits.IndexOf('.') - 1 : 0;
            return (negative ? -value : value, (decimal)Math.Pow(10, -decimals));
        }
    }

    public static LinearizedTable Linearize(IHtmlTableElement table)
    {
        List<List<Cell>> rows = ReadRows(table);
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
                nonEmptyRows.Select(r => TextRow(r.Select(c => c.Text))).ToList());
        }

        string? units = null;
        string? groupLabel = null;
        string? periodCaption = null; // a label-column period row ("June 30, 2026") - on every row path below it
        int block = -1;
        List<HeaderCell> headerCells = [];
        List<string> tableCaptions = []; // above every header row, e.g. "June 30, 2026" - kept for the whole table
        List<string> captions = [];      // label-column text of the current header block's rows
        List<Leaf> leaves = [];
        List<string> unconsumedHeaderRows = []; // header rows not yet followed by a data row
        bool lastWasData = false;
        List<LinearizedRow> output = [];
        RunningRows groupRows = new(0);   // the current group's data rows, from its label
        RunningRows running = new(0);     // every data row but totals, since the table's start or its last period caption
        (string Label, int FirstRow)? outdented = null; // a group a row seemed to leave, until a total settles it

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
                    running = new RunningRows(0);
                    outdented = null;
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
                    groupRows = new RunningRows(row[0].Indent);
                    outdented = null;
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

            List<LinearizedValue> values = [];
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
                HashSet<string> used = [];
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

                    values.Add(new LinearizedValue(key, leaf.Label, group.Text, group.Fact?.ContextRef, group.Fact?.Concept, group.Fact?.Unit, leaf.Parts));
                }
            }

            // A group's total ends it, named so or not: NFLX's "Net cash used in financing activities" adds up the
            // financing rows, and "Effect of exchange rate changes" after it is no financing item. Unless the group's
            // own "Total ..." comes later - then a total before it is a subtotal inside the group (MSFT's "Total cash,
            // cash equivalents, and short-term investments" before "Total current assets"; NDAQ's "Foreign currency
            // translation, net" before "Total other comprehensive income (loss), net of tax").
            bool groupTotalLater = groupLabel is not null && nonEmptyRows.Skip(rowIndex + 1)
                .Any(r => r[0].Text.StartsWith("Total", StringComparison.OrdinalIgnoreCase) && NamesGroup(r[0].Text, groupLabel));
            bool isTotal = !groupTotalLater && (label.StartsWith("Total", StringComparison.OrdinalIgnoreCase)
                || (groupLabel is not null && groupRows.Count >= 2 && groupRows.AddsUpTo(values, UnnamedTotalAmounts)));

            // A running total adds up every row of the table so far, not the group's alone, whatever its label: NFLX's
            // derivatives "Total", ORCL's "Net cash provided by operating activities" (indented deeper than the
            // working-capital rows above it), NDAQ's "Revenues less transaction-based expenses" (indented like them).
            bool isRunningTotal = groupLabel is not null && groupRows.IsGrandTotal(values, running, label.StartsWith("Total", StringComparison.OrdinalIgnoreCase) ? 1 : UnnamedTotalAmounts);
            if (isRunningTotal)
            {
                groupLabel = null;
            }
            else if (groupLabel is not null && !isTotal && groupRows.IsOutdentedPast(row[0].Indent, label))
            {
                outdented = (groupLabel, output.Count);
                groupLabel = null;
            }
            else if (isTotal && outdented is { } held && NamesGroup(label, held.Label) && groupRows.AddsUpTo(values))
            {
                // The indent misled: the group's own total ("Total deferred tax liabilities" under "Deferred tax
                // liabilities:") adds up only with the rows printed at its label's indent (NFLX's "Acquired intangibles"
                // and "Other"), so they were the group's after all. A bare "Total" may be the whole table's (MSFT's
                // contractual obligations, after "Long-term debt:") - it never pulls rows back.
                groupLabel = held.Label;
                for (int i = held.FirstRow; i < output.Count; i++)
                {
                    output[i] = output[i] with { GroupLabel = PathOf(groupLabel) };
                }
            }

            output.Add(new LinearizedRow(PathOf(groupLabel), label, values));

            // A total's figures are its rows', already in the running sum.
            if (isTotal || isRunningTotal)
            {
                groupLabel = null;
                outdented = null;
            }
            else
            {
                // An outdented row stays counted with its group until a total settles which it was.
                groupRows.Add(row[0].Indent, values);
                running.Add(row[0].Indent, values);
            }

            string? PathOf(string? g) => (periodCaption, g) switch
            {
                (null, _) => g,
                (_, null) => periodCaption,
                ({ } period, { } named) => $"{period} > {named.TrimEnd(':')}",
            };
        }

        // Rows read as headers that no data row ever followed aren't headers at all - MSFT's exhibit index
        // ends with text-only rows ("31.1 | Certification of Chief Executive Officer ... | X"), which would
        // otherwise be dropped silently - kept here as plain lines.
        output.AddRange(unconsumedHeaderRows.Select(text => new LinearizedRow(null, text, [])));

        LinearizedTable linearized = new(LinearizedTableKind.Financial, null, units, output, []);

        // Safety net for whatever rule misses next: every cell's text must survive somewhere in the
        // rendered output (row paths, column labels, values, units), or the table falls back - to Markdown in
        // the Linearized strategy, to text rows in Structured (LinearizeAsText) - where nothing is lost. Silent
        // content loss becomes a counted fallback with a reason.
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
    /// The text path alone, for any table: each non-empty row's cells joined with " | " (a lone bullet with a space), as <see cref="Linearize"/>
    /// does for a table with no numeric data rows. It keeps every cell's text by construction, so the Structured
    /// strategy uses it where <see cref="Linearize"/> falls back (Structured.FilingBlockReader.ReadTable).
    /// </summary>
    public static LinearizedTable LinearizeAsText(IHtmlTableElement table)
    {
        List<string> lines = ReadRows(table)
            .Select(r => r.Where(c => c.Text.Length > 0).ToList())
            .Where(r => r.Count > 0)
            .Select(r => TextRow(r.Select(c => c.Text)))
            .ToList();
        return new LinearizedTable(lines.Count == 0 ? LinearizedTableKind.Empty : LinearizedTableKind.Text, null, null, [], lines);
    }

    // A text row's cells joined with " | ", but a bullet in a cell of its own joins its text with a space: NDAQ lays out
    // a nested list as a table - the bullet in one cell, its text in the next, a level deeper one cell further right -
    // and "▪ | Capital Access Platforms" read as two columns of a table.
    private static string TextRow(IEnumerable<string> cells)
    {
        List<string> texts = cells.ToList();
        return texts.Count > 1 && texts[0] is "•" or "●" or "▪" or "◦"
            ? $"{texts[0]} {string.Join(" | ", texts.Skip(1))}"
            : string.Join(" | ", texts);
    }

    /// <summary>
    /// How many of a table's leading non-empty rows are column names, read from the only marker these filers
    /// use - bold text (no filing uses &lt;thead&gt; or &lt;th&gt;): the leading rows whose text is all bold,
    /// or 0 if every row is (a bold cover-page box has no header). For a text table's continuation pieces
    /// (Structured.FilingBlockReader.TextRowBlock); a bold title row is caught too ("Critical audit matter"
    /// tables), which only puts the title on every piece.
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

    /// <summary>
    /// The form both strategies use, chosen in the spike: nil "—" values omitted, and the units plus a caption
    /// shared by every column ("Year Ended June 30,") written once, as the block's context line - which
    /// TokenChunker repeats on every piece if the block has to be split.
    /// </summary>
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
            List<(string Column, List<string> Texts)> columns = [];
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
    // something of its own, so "2026" / "2025" stay distinguishable. Inside one header cell it may only cut before a
    // year ("December 31," over "2025" / "2024"); anywhere else it backs off to the header cells shared whole - NFLX's
    // "Derivative Assets" / "Derivative Liabilities" share a first word, and cut there the columns read "Assets ...".
    private static string SharedCaption(LinearizedTable table)
    {
        List<LinearizedValue> columns = table.Rows.SelectMany(r => r.Values).Where(v => v.ColumnLabel.Length > 0).DistinctBy(v => v.ColumnLabel).ToList();
        if (columns.Count < 2)
        {
            return string.Empty;
        }

        string prefix = columns.Select(v => v.ColumnLabel).Aggregate((a, b) => new string(a.Zip(b).TakeWhile(p => p.First == p.Second).Select(p => p.First).ToArray()));
        int cut = prefix.LastIndexOf(' ');
        prefix = cut < 0 ? string.Empty : prefix[..cut];
        if (!columns.All(v => v.ColumnLabel.Length > prefix.Length + 1))
        {
            return string.Empty;
        }

        bool atCellBoundary = columns.All(v => v.ColumnParts is not { } parts
            || Enumerable.Range(0, parts.Count + 1).Any(n => string.Join(" ", parts.Take(n)) == prefix));
        if (atCellBoundary || columns.All(v => LeadingYearRegex().IsMatch(v.ColumnLabel[prefix.Length..].TrimStart())))
        {
            return prefix.Trim();
        }

        List<IReadOnlyList<string>> partLists = columns.Select(v => v.ColumnParts ?? [v.ColumnLabel]).ToList();
        int whole = Enumerable.Range(0, partLists.Min(p => p.Count) - 1).TakeWhile(i => partLists.All(p => p[i] == partLists[0][i])).Count();
        return string.Join(" ", partLists[0].Take(whole));
    }

    /// <summary>
    /// Every row's cells at their column positions, as the HTML table model places them: a cell spans its colspan, and
    /// a cell with rowspan also holds its columns in the rows below, so their cells start after it. Laid out row by row
    /// from column 0, a rowspan cell at the left or in the middle of a header would shift the next row's year cells
    /// left, and values would go under the wrong year with no text lost. The spanning cell's text stays in its own row.
    /// </summary>
    private static List<List<Cell>> ReadRows(IHtmlTableElement table)
    {
        List<List<Cell>> rows = [];
        Dictionary<int, int> held = []; // column -> how many more rows a rowspan cell above still holds it for
        foreach (IHtmlTableRowElement row in table.Rows)
        {
            List<Cell> cells = [];
            Dictionary<int, int> heldFromHere = [];
            int position = 0;
            foreach (IHtmlTableCellElement cell in row.Cells)
            {
                while (held.ContainsKey(position))
                {
                    position++;
                }

                int span = Math.Max(1, cell.ColumnSpan);
                List<Fact> facts = cell.Descendants<IElement>()
                    .Where(e => e.LocalName.Equals("ix:nonfraction", StringComparison.OrdinalIgnoreCase))
                    .Select(e => new Fact(e.GetAttribute("contextref") ?? string.Empty, e.GetAttribute("name") ?? string.Empty, e.GetAttribute("unitref") ?? string.Empty))
                    .ToList();
                cells.Add(new Cell(position, position + span - 1, CellText(cell), facts, CellIndent(cell)));

                // rowspan="0" holds the columns to the end of the table.
                int below = cell.RowSpan == 0 ? int.MaxValue : cell.RowSpan - 1;
                for (int column = position; below > 0 && column < position + span; column++)
                {
                    heldFromHere[column] = below;
                }

                position += span;
            }

            rows.Add(cells);

            // This row used up one row of every hold from above; the holds this row started apply from the next row.
            foreach (int column in held.Keys.ToList())
            {
                if (held[column] != int.MaxValue && --held[column] == 0)
                {
                    held.Remove(column);
                }
            }

            foreach ((int column, int count) in heldFromHere)
            {
                held[column] = count;
            }
        }

        return rows;
    }

    // How far in a cell's first text starts, in points: every left padding, margin, text indent and (NDAQ's positioned
    // spans) left offset from the cell down to that text. Filers mix units - inches (MSFT, ORCL), points (NFLX, NDAQ),
    // percentages with a negative text indent for a hanging line - so only rows of one table are compared, and a
    // percentage counts as 5pt per 1%, about its share of a label column.
    private static double CellIndent(IElement cell)
    {
        IText? first = cell.Descendants<IText>().FirstOrDefault(t => t.Data.Trim().Length > 0);
        double indent = 0;
        for (IElement? element = first?.ParentElement; element is not null; element = element == cell ? null : element.ParentElement)
        {
            string style = element.GetAttribute("style") ?? string.Empty;
            foreach (Match m in IndentRegex().Matches(style))
            {
                indent += Points(m.Groups["value"].Value, m.Groups["unit"].Value);
            }

            if (PaddingShorthandRegex().Match(style) is { Success: true } padding)
            {
                string[] sides = padding.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string left = sides.Length switch { 4 => sides[3], >= 2 => sides[1], _ => sides[0] };
                if (LengthRegex().Match(left) is { Success: true } length)
                {
                    indent += Points(length.Groups["value"].Value, length.Groups["unit"].Value);
                }
            }
        }

        return indent;
    }

    private static double Points(string value, string unit) =>
        double.Parse(value, System.Globalization.CultureInfo.InvariantCulture) * unit switch
        {
            "in" => 72, "px" => 0.75, "em" => 10, "%" => 5, _ => 1,
        };

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
        List<ValueGroup> groups = [];
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

    // "Total deferred tax liabilities" is the total of "Deferred tax liabilities:", and "Total finance lease
    // liabilities" of "Finance Leases": the group's name opens the total's, plurals aside. Opens it - "Total
    // liabilities and stockholders' equity" is no total of "Stockholders' equity:".
    private static bool NamesGroup(string totalLabel, string groupLabel)
    {
        List<string> name = Words(groupLabel);
        List<string> total = Words(totalLabel[Math.Min("Total".Length, totalLabel.Length)..]);
        return name.Count > 0 && total.Take(name.Count).SequenceEqual(name);

        static List<string> Words(string text) => WordRegex().Matches(text.ToLowerInvariant())
            .Select(m => m.Value.Length > 3 && m.Value.EndsWith('s') ? m.Value[..^1] : m.Value)
            .ToList();
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
            List<string> parts = stack.Distinct().ToList();
            leaves.Add(new Leaf($"b{block}c{index}", leaf.Start, leaf.End, string.Join(" ", parts), parts));
        }

        return null;
    }

    // Overlap wins, but a value overlapping two headers is ambiguous (null). Otherwise the nearest header by column
    // distance, the leftmost on a tie: filers without colspan put the year over the "$" cell, left of the number (MSFT).
    private static Leaf? NearestLeaf(List<Leaf> leaves, ValueGroup group)
    {
        List<(Leaf Leaf, int Distance)> ranked = leaves
            .Select(l => (Leaf: l, Distance: Overlaps(l.Start, l.End, group.Start, group.End) ? 0 : Math.Min(Math.Abs(l.Start - group.End), Math.Abs(group.Start - l.End))))
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Leaf.Start)
            .ToList();

        if (ranked.Count > 1 && ranked[0].Distance == ranked[1].Distance && ranked[0].Distance == 0)
        {
            return null;
        }

        return ranked[0].Leaf;
    }

    private static bool Overlaps(int aStart, int aEnd, int bStart, int bEnd) => aStart <= bEnd && bStart <= aEnd;
}
