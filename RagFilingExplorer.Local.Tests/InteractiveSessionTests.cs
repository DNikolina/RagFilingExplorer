using Microsoft.Extensions.AI;
using RagFilingExplorer.Local.Retrieval;

namespace RagFilingExplorer.Local.Tests;

[TestFixture]
public class InteractiveSessionTests
{
    private static async IAsyncEnumerable<ChatResponseUpdate> NoUpdates()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static RagAnswer Answer(string[] filings, string? statementType) =>
        new(filings, statementType, ReasoningEffort.None, [], NoUpdates());

    // The manual pass checks this line on every question, and tools/replay_recall.py parses the filings and
    // "statement type: <type>" back out of a --verbose run log - a format change would silently break both.
    [TestCase(new[] { "MSFT-10K-2026.html" }, null, "(filtering to MSFT-10K-2026.html)")]
    [TestCase(new[] { "MSFT-10K-2026.html" }, "balance_sheet", "(filtering to MSFT-10K-2026.html, statement type: balance_sheet)")]
    [TestCase(new string[0], "income_statement", "(filtering to statement type: income_statement)")]
    [TestCase(new[] { "MSFT-10K-2026.html", "ORCL-10K-2026.html" }, null, "(searching MSFT-10K-2026.html and ORCL-10K-2026.html separately)")]
    [TestCase(new[] { "MSFT-10K-2026.html", "ORCL-10K-2026.html" }, "income_statement",
        "(searching MSFT-10K-2026.html and ORCL-10K-2026.html separately, statement type: income_statement)")]
    public void FormatMatchedFilter_ResolvedFilter_ReadsAsTheFilterLine(string[] filings, string? statementType, string expected)
    {
        Assert.That(InteractiveSession.FormatMatchedFilter(Answer(filings, statementType)), Is.EqualTo(expected));
    }

    // Hybrid search: the statement type boosts instead of filtering, and the line says so - still ending in "statement
    // type: <type>", which tools/replay_recall.py reads.
    [TestCase(new[] { "ORCL-10K-2026.html" }, "income_statement", "(filtering to ORCL-10K-2026.html, boosting statement type: income_statement)")]
    [TestCase(new string[0], "income_statement", "(boosting statement type: income_statement)")]
    [TestCase(new[] { "ORCL-10K-2026.html" }, null, "(filtering to ORCL-10K-2026.html)")]
    public void FormatMatchedFilter_Hybrid_SaysTheStatementTypeBoosts(string[] filings, string? statementType, string expected)
    {
        RagAnswer answer = new(filings, statementType, ReasoningEffort.None, [], NoUpdates(), SearchMode.Hybrid, "\"x\"");

        Assert.That(InteractiveSession.FormatMatchedFilter(answer), Is.EqualTo(expected));
    }

    [TestCase("\"deferred\" OR \"revenues\"", "(keywords: \"deferred\" OR \"revenues\")")]
    [TestCase(null, "(keywords: none)")]
    public void FormatKeywords_ReadsAsTheKeywordsLine(string? keywordQuery, string expected)
    {
        RagAnswer answer = new([], null, ReasoningEffort.None, [], NoUpdates(), SearchMode.Hybrid, keywordQuery);

        Assert.That(InteractiveSession.FormatKeywords(answer), Is.EqualTo(expected));
    }

    [Test]
    public void FormatMatchedFilter_NoFilter_IsNull()
    {
        Assert.That(InteractiveSession.FormatMatchedFilter(Answer([], null)), Is.Null);
    }
}
