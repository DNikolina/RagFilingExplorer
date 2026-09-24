namespace RagFilingExplorer.Local.Chunking;

/// <summary>
/// Builds the text actually sent to the embedding model - richer than the chunk's displayed
/// <c>Content</c>. A sparse Markdown table (mostly "|", "-", and bare numbers) embeds poorly against
/// natural-language financial questions, so this surfaces each table's actual row labels (e.g.
/// "Total revenues", "Gross margin", "Cost of revenue") as plain text, since those are exactly the
/// terms a real question is likely to use, while <c>Content</c> - what's shown to the user and the
/// LLM - stays untouched.
///
/// Worth being honest about what this did and didn't fix: Step 7's retrieval-quality debugging tried
/// this specifically to help distinguish "the revenue table" from "the balance sheet table" from "the
/// equity table" in filings with many similarly-shaped tables (e.g. an "Item 15" appendix), but on its
/// own it did **not** move the needle on the actual blocking questions (documented in
/// Decision-Log.md's "Follow-up: retrieval quality" as "tried and found insufficient"). The fix
/// that actually solved that problem was metadata filtering - <see cref="StatementTypeDetector"/> +
/// <c>QueryIntentResolver</c> narrowing the vector search itself. This class is kept as a cheap,
/// still-reasonable secondary signal alongside that filtering, not as the fix in its own right.
/// </summary>
internal static class EmbeddingTextBuilder
{
    // Applies to any chunk that *contains* a table, not just one that starts with a table row. The
    // original "starts with |" check silently skipped the first piece of every oversized table once
    // TokenChunker started prefixing it with a short caption (the statement title) - and the first piece
    // is exactly the one holding the headline rows. ORCL's revenue piece dropped to last of its income
    // statement's 4 chunks, out of Q20's context. It also skipped the prose-plus-table chunks that had
    // always started with a lead-in sentence ("The following table shows...").
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

    private static List<string> ExtractRowLabels(string tableContent)
    {
        HashSet<string> seen = new();
        List<string> labels = new();

        foreach (string line in tableContent.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith('|'))
            {
                continue;
            }

            string[] cells = trimmed.Trim('|').Split('|');
            if (cells.Length == 0)
            {
                continue;
            }

            string label = cells[0].Trim();

            // Skip blank cells, separator rows ("---"), and purely numeric labels - not useful
            // natural-language signal.
            if (label.Length < 2 || label.All(c => c == '-') || !label.Any(char.IsLetter))
            {
                continue;
            }

            if (seen.Add(label))
            {
                labels.Add(label);
            }
        }

        return labels;
    }
}
