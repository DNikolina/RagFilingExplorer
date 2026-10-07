namespace RagFilingExplorer.Local.Chunking;

/// <summary>
/// Builds the text actually sent to the embedding model - richer than the chunk's displayed
/// <c>Content</c>. A sparse Markdown table (mostly "|", "-", and bare numbers) embeds poorly against
/// natural-language financial questions, so this surfaces each table's actual row labels (e.g.
/// "Total revenues", "Gross margin", "Cost of revenue") as plain text, since those are exactly the
/// terms a real question is likely to use, while <c>Content</c> - what's shown to the user and the
/// LLM - stays untouched.
///
/// On its own this doesn't tell "the revenue table" from "the balance sheet table" in a filing with many
/// similarly-shaped tables - that's metadata filtering's job (<see cref="StatementTypeDetector"/> +
/// <c>QueryIntentResolver</c>; hybrid search keeps the statement type as a boost). This is a cheap secondary
/// signal alongside it (Decision-Log.md, "Follow-up: retrieval quality"). It only reads Markdown (pipe) tables;
/// the Structured strategy's row lines carry their labels already, and its chunks also open with the company line
/// (see FilingChunkRecords).
/// </summary>
internal static class EmbeddingTextBuilder
{
    // Applies to any chunk that *contains* a table, not just one that starts with a table row: the first
    // piece of an oversized table opens with its caption (the statement title, from TokenChunker) and holds
    // the headline rows, and a prose-plus-table chunk opens with a lead-in ("The following table shows...").
    public static string Build(string heading, string content)
    {
        if (!content.Split('\n').Any(line => line.TrimStart().StartsWith('|')))
        {
            return $"{heading}\n\n{content}";
        }

        List<string> labels = ExtractRowLabels(content);
        string labelSummary = labels.Count > 0
            ? $"Financial data table with rows: {string.Join(", ", labels)}."
            : "Financial data table.";

        return $"{heading}\n{labelSummary}\n\n{content}";
    }

    // Each label once, in first-seen order (HashSet.Add as the filter: Enumerable.Distinct doesn't promise an order).
    private static List<string> ExtractRowLabels(string tableContent)
    {
        HashSet<string> seen = [];
        return tableContent.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith('|'))
            .Select(line => line.Trim('|').Split('|')[0].Trim())
            // Skip blank cells, separator rows ("---"), and purely numeric labels - not useful natural-language signal.
            .Where(label => label.Length >= 2 && !label.All(c => c == '-') && label.Any(char.IsLetter))
            .Where(seen.Add)
            .ToList();
    }
}
