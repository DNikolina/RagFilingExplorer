namespace RagFilingExplorer.Local.Chunking;

/// <summary>
/// A linearized table: a context line and self-contained rows. The Structured strategy holds one per table
/// block (Structured.TableBlock) and never writes it out. The Linearized strategy sends it through markitdown:
/// each table it can linearize becomes a &lt;pre&gt; holding this format, which markitdown emits as a fenced
/// block with no Markdown escaping (checked directly: "$", "*", "_", "&lt;" and line breaks all survive). The
/// fence is what lets SectionSplitter skip the rows for heading detection and TokenChunker keep them atomic.
///
/// <code>
/// ```
/// #rows
/// #context (In millions) Year Ended June 30,
/// Net income — 2026: $133,749 | 2025: $101,832
/// ```
/// </code>
/// </summary>
internal sealed record RowBlock(string? Context, IReadOnlyList<string> Rows)
{
    public const string Fence = "```";
    private const string Marker = "#rows";
    private const string ContextPrefix = "#context ";

    /// <summary>The text a chunk carries: the context line (if any), then one line per row.</summary>
    public string Text => string.Join('\n', Context is null ? Rows : Rows.Prepend(Context));

    /// <summary>The &lt;pre&gt; content the Linearized strategy puts in place of a table.</summary>
    public string Format() => string.Join('\n', new[] { Marker }
        .Concat(Context is null ? [] : [ContextPrefix + Context])
        .Concat(Rows));

    /// <summary>Parses a fenced block as markitdown emitted it; null for any other block.</summary>
    public static RowBlock? TryParse(string block)
    {
        string[] lines = block.Replace("\r\n", "\n").Trim().Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != Fence || lines[1].Trim() != Marker || lines[^1].Trim() != Fence)
        {
            return null;
        }

        string[] body = lines[2..^1];
        string? context = body.Length > 0 && body[0].StartsWith(ContextPrefix, StringComparison.Ordinal) ? body[0][ContextPrefix.Length..].Trim() : null;
        List<string> rows = body.Skip(context is null ? 0 : 1).Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        return new RowBlock(context, rows);
    }
}
