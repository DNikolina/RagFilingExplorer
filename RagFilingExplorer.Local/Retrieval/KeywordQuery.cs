using System.Text.RegularExpressions;

namespace RagFilingExplorer.Local.Retrieval;

/// <summary>
/// Turns a question into the FTS5 query for the keyword half of hybrid search: its content words, each quoted and
/// joined by OR, so bm25 ranks a chunk by how many of them it holds and how rare they are. Dropped: question and
/// function words, words every chunk of a filing shares ("fiscal", "total", "statement"), four-digit years (every
/// table has several), and the question's company names - the company is already the search's filter, and a name
/// would only favour chunks that repeat it. Measured by replay before it was built (docs/Decision-Log.md, "Step 2 -
/// hybrid search"). Two-word phrases were tried there too and not kept: T5's "U.S. government securities" went 4 -> 2,
/// H6 and H13 1 -> 2.
/// </summary>
internal static class KeywordQuery
{
    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "the", "of", "in", "on", "at", "to", "for", "from", "by", "with", "and", "or", "as", "is", "was", "were",
        "be", "been", "are", "what", "which", "who", "whom", "how", "much", "many", "did", "does", "do", "its", "it", "this",
        "that", "these", "those", "their", "there", "than", "then", "per", "during", "year", "years", "fiscal", "ended",
        "ending", "end", "according", "statement", "statements", "total", "amount", "value", "company", "s",
    ];

    /// <summary>The FTS5 MATCH expression, or null when the question has no content word left to search for.</summary>
    public static string? Build(string question, IEnumerable<string> companyNames)
    {
        HashSet<string> names = companyNames.SelectMany(Words).ToHashSet();
        string[] terms = Words(question)
            .Where(w => !StopWords.Contains(w) && !names.Contains(w) && !Regex.IsMatch(w, @"^(19|20)\d\d$"))
            .Distinct()
            .ToArray();

        // Words are [a-z0-9]+ only, so quoting each one is all the escaping FTS5 needs.
        return terms.Length == 0 ? null : string.Join(" OR ", terms.Select(t => $"\"{t}\""));
    }

    private static IEnumerable<string> Words(string text) =>
        Regex.Matches(text.ToLowerInvariant(), "[a-z0-9]+").Select(m => m.Value);
}
