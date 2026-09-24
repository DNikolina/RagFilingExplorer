using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.ML.Tokenizers;
using RagFilingExplorer.Local;
using RagFilingExplorer.Local.Chunking;

// Linearization spike: runs HtmlTableLinearizer over every filing and reports coverage, token impact and
// the linearized text of every table, plus a JSON file (values with their XBRL contextRef) for
// tools/xbrl_column_check.py. Nothing here touches the app's pipeline or index.
string outDirectory = args.Length > 0 ? args[0] : "spike-out";
Directory.CreateDirectory(outDirectory);

DirectoryInfo repoRoot = RepoPaths.FindRoot(AppContext.BaseDirectory);
Tokenizer tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");
HtmlParser parser = new();

foreach (FileInfo filing in new DirectoryInfo(Path.Combine(repoRoot.FullName, "data")).GetFiles("*.html").OrderBy(f => f.Name))
{
    byte[] bytes = await File.ReadAllBytesAsync(filing.FullName);
    string html = MarkItDownConverter.StripIxHeader(MarkItDownConverter.DetectEncoding(bytes).GetString(bytes));
    IHtmlDocument document = parser.ParseDocument(html);

    List<IHtmlTableElement> tables = document.QuerySelectorAll("table").OfType<IHtmlTableElement>().ToList();
    List<IHtmlTableElement> topLevel = tables.Where(t => t.ParentElement?.Closest("table") is null).ToList();

    Dictionary<LinearizedTableKind, int> kinds = Enum.GetValues<LinearizedTableKind>().ToDictionary(k => k, _ => 0);
    Dictionary<string, int> reasons = new();
    Dictionary<string, (int Tables, int Financial)> byStatement = new();
    int markdownTokens = 0, linearizedTokens = 0, fallbackTokens = 0, taggedValues = 0, allValues = 0, noNilTokens = 0, compactTokens = 0;

    StringBuilder text = new();
    List<object> json = new();

    for (int i = 0; i < topLevel.Count; i++)
    {
        IHtmlTableElement table = topLevel[i];
        string statement = FindStatementType(table) ?? "-";
        LinearizedTable result = HtmlTableLinearizer.Linearize(table);
        kinds[result.Kind]++;

        if (statement != "-")
        {
            (int t, int f) = byStatement.GetValueOrDefault(statement);
            byStatement[statement] = (t + 1, f + (result.Kind == LinearizedTableKind.Financial ? 1 : 0));
        }

        string markdown = ApproximateMarkdown(table);
        int markdownCount = tokenizer.CountTokens(markdown);
        if (result.Kind == LinearizedTableKind.Empty)
        {
            continue;
        }

        markdownTokens += markdownCount;
        string rendered = string.Join('\n', HtmlTableLinearizer.Render(result));
        if (result.Kind == LinearizedTableKind.Fallback)
        {
            reasons[Bucket(result.FallbackReason!)] = reasons.GetValueOrDefault(Bucket(result.FallbackReason!)) + 1;
            fallbackTokens += markdownCount;
        }
        else
        {
            linearizedTokens += tokenizer.CountTokens(rendered);
            noNilTokens += tokenizer.CountTokens(string.Join('\n', HtmlTableLinearizer.Render(result, omitNil: true)));
            compactTokens += tokenizer.CountTokens(string.Join('\n', HtmlTableLinearizer.Render(result, omitNil: true, factorCaption: true)));
        }

        foreach (LinearizedValue value in result.Rows.SelectMany(r => r.Values))
        {
            allValues++;
            taggedValues += value.ContextRef is null ? 0 : 1;
        }

        text.AppendLine($"=== Table {i} | statement: {statement} | {result.Kind}{(result.FallbackReason is null ? "" : $" ({result.FallbackReason})")} ===");
        text.AppendLine(result.Kind == LinearizedTableKind.Fallback ? markdown : rendered);
        text.AppendLine();

        json.Add(new
        {
            index = i,
            statement,
            kind = result.Kind.ToString(),
            reason = result.FallbackReason,
            rows = result.Rows.Select(r => new
            {
                group = r.GroupLabel,
                label = r.Label,
                values = r.Values.Select(v => new { col = v.ColumnKey, colLabel = v.ColumnLabel, text = v.Text, ctx = v.ContextRef, concept = v.Concept, unit = v.Unit }),
            }),
        });
    }

    string stem = Path.GetFileNameWithoutExtension(filing.Name);
    await File.WriteAllTextAsync(Path.Combine(outDirectory, $"{stem}.linearized.txt"), text.ToString());
    await File.WriteAllTextAsync(Path.Combine(outDirectory, $"{stem}.linearized.json"), JsonSerializer.Serialize(json));

    int nonEmpty = topLevel.Count - kinds[LinearizedTableKind.Empty];
    Console.WriteLine($"=== {filing.Name} ===");
    Console.WriteLine($"tables: {tables.Count} ({tables.Count - topLevel.Count} nested, skipped) | non-empty top-level: {nonEmpty}");
    Console.WriteLine($"  financial: {kinds[LinearizedTableKind.Financial]} | text: {kinds[LinearizedTableKind.Text]} | fallback: {kinds[LinearizedTableKind.Fallback]} | empty: {kinds[LinearizedTableKind.Empty]}");
    foreach ((string reason, int count) in reasons.OrderByDescending(r => r.Value))
    {
        Console.WriteLine($"    fallback: {count} x {reason}");
    }

    Console.WriteLine($"  statement tables (financial/found): {string.Join(", ", byStatement.OrderBy(s => s.Key).Select(s => $"{s.Key} {s.Value.Financial}/{s.Value.Tables}"))}");
    Console.WriteLine($"  values: {allValues} ({taggedValues} XBRL-tagged)");
    string Delta(int linearized) => $"{linearized + fallbackTokens} ({100.0 * (linearized + fallbackTokens) / markdownTokens - 100:+0;-0}%)";
    Console.WriteLine($"  tokens incl. fallback tables kept as markdown ({fallbackTokens}) - markdown approx: {markdownTokens} | full: {Delta(linearizedTokens)} | no nil: {Delta(noNilTokens)} | no nil + shared caption: {Delta(compactTokens)}");
    Console.WriteLine();
}

// markitdown emits one Markdown column per <td> (colspan ignored), "|"-separated, with a separator row
// after the first row - approximated here so both sides are measured on exactly the same tables.
static string ApproximateMarkdown(IHtmlTableElement table)
{
    List<string> lines = table.Rows
        .Select(r => "| " + string.Join(" | ", r.Cells.Select(c => c.TextContent.Replace(' ', ' ').Trim())) + " |")
        .ToList();
    if (lines.Count > 0)
    {
        int columns = table.Rows[0].Cells.Length;
        lines.Insert(1, "| " + string.Join(" | ", Enumerable.Repeat("---", Math.Max(1, columns))) + " |");
    }

    return string.Join('\n', lines);
}

static string Bucket(string reason) =>
    reason.StartsWith("two values") ? "two values map to one column"
    : reason.StartsWith("value '") ? "value equidistant from two headers"
    : reason;

// The statement a table belongs to, from the nearest statement title above it (same detector the app
// uses), searching previous siblings a few levels up. Approximate - for reporting scope only.
static string? FindStatementType(IHtmlTableElement table)
{
    IElement? current = table;
    for (int level = 0; level < 4 && current is not null; level++)
    {
        IElement? sibling = current.PreviousElementSibling;
        for (int n = 0; n < 12 && sibling is not null; n++, sibling = sibling.PreviousElementSibling)
        {
            if (sibling.LocalName == "table" || sibling.QuerySelector("table") is not null)
            {
                return null;
            }

            foreach (string line in LeafTexts(sibling).Reverse())
            {
                if (StatementTypeDetector.IsNotesToFinancialStatementsBoundary(line))
                {
                    return null;
                }

                if (StatementTypeDetector.Detect(line) is { } detected)
                {
                    return detected;
                }
            }
        }

        current = current.ParentElement;
    }

    return null;
}

static IEnumerable<string> LeafTexts(IElement element)
{
    IEnumerable<IElement> blocks = element.QuerySelectorAll("p, div").Where(e => e.QuerySelector("p, div") is null);
    if (!blocks.Any())
    {
        blocks = [element];
    }

    return blocks.Select(b => string.Join(' ', b.TextContent.Replace(' ', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
        .Where(t => t.Length > 0);
}
